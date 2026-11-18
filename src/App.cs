using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using Application = System.Windows.Application;

[assembly: AssemblyTitle("Double Ctrl")]
[assembly: AssemblyProduct("Double Ctrl")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace DoubleCtrl
{
    internal sealed class App
    {
        private const string MutexName = "DoubleCtrl.SingleInstance";
        private const string ShowEventName = "DoubleCtrl.Show";
        private const string QuitEventName = "DoubleCtrl.Quit";
        private static readonly TimeSpan UpdateDelay = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan InstallRetry = TimeSpan.FromSeconds(30);

        private readonly Application _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        private readonly Settings _settings = Settings.Load();
        private readonly InputMonitor _monitor = new InputMonitor();
        private readonly SelectionReader _reader;
        private PopupController _popup;
        private NotifyIcon _tray;
        private int _capturing;
        private Native.POINT _lastAnchor;
        private string _downloadedUpdate;

        private App()
        {
            _reader = new SelectionReader(_monitor);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Log.Enabled = args.Contains("--debug");
            SelectionReader.ClipboardDisabled = args.Contains("--no-clipboard");
            bool created;
            bool updated = args.Contains(Updater.UpdatedArgument);
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                // 自动更新后启动的新进程：旧进程正在退出，等它放开互斥量。
                if (!created && updated) created = WaitForMutex(mutex);
                if (!created)
                {
                    SignalRunningInstance(args);
                    return;
                }
                if (args.Contains("--quit")) return;
                new App().Run(args.Contains(AutoStart.Argument), updated);
                GC.KeepAlive(mutex);
            }
        }

        private static bool WaitForMutex(Mutex mutex)
        {
            try { return mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { return true; }
        }

        private static void SignalRunningInstance(string[] args)
        {
            if (args.Contains(AutoStart.Argument)) return;
            string name = args.Contains("--quit") ? QuitEventName : ShowEventName;
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name))
                signal.Set();
        }

        private void Run(bool autostart, bool updated)
        {
            // 常驻后台的工具不该因为一处界面异常就整个退出，记下来继续运行。
            _app.DispatcherUnhandledException += (sender, e) =>
            {
                Log.Crash(e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Log.Crash(e.ExceptionObject);

            AutoStart.Refresh();
            Updater.CleanUp();
            SelectionReader.WarmUp();
            DictClient.WarmUp();

            Theme.Apply(_settings.Theme);
            SystemEvents.UserPreferenceChanged += delegate
            {
                if (_settings.Theme == Theme.System) OnUi(delegate { Theme.Apply(Theme.System); });
            };

            _popup = new PopupController(_settings, _monitor);
            _popup.ExitRequested += Exit;
            CreateTray();

            _reader.LateSelection += text => OnUi(delegate { _popup.ShowSelection(Normalize(text), _lastAnchor); });
            _monitor.DoubleCtrl += OnDoubleCtrl;
            _monitor.MouseDown += delegate { OnUi(_popup.OnGlobalMouseDown); };
            _monitor.EscapePressed += delegate { OnUi(_popup.OnGlobalEscape); };
            _monitor.Start();
            WatchSignal(ShowEventName, delegate { _popup.ShowForInput(null); });
            WatchSignal(QuitEventName, Exit);

            if (updated) _tray.ShowBalloonTip(4000, "Double Ctrl 已更新到 " + Updater.CurrentText, "选中文字后双击 Ctrl 即可翻译。", ToolTipIcon.Info);
            else if (autostart || _settings.StartHidden) ShowFirstRunBalloon();
            else OnUi(delegate { _popup.ShowForInput(null); });
            StartUpdateChecks();

            _app.Run();
        }

        private void CreateTray()
        {
            _tray = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Double Ctrl",
                Visible = true,
            };
            _tray.MouseClick += (sender, e) =>
            {
                if (e.Button == MouseButtons.Left) _popup.ShowForInput(null);
                else if (e.Button == MouseButtons.Right) _popup.ShowTrayMenu();
            };
        }

        // 启动后稍等再查，不和预热抢网络；常驻后台的程序很少重启，之后每天再查一次。
        private void StartUpdateChecks()
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = UpdateDelay };
            timer.Tick += delegate
            {
                timer.Interval = UpdateInterval;
                CheckForUpdate();
            };
            timer.Start();
        }

        private async void CheckForUpdate()
        {
            if (_downloadedUpdate != null) return;
            try { _downloadedUpdate = await Updater.DownloadAsync(); }
            catch (Exception e) { Log.Write("update check failed: " + e.Message); }
            if (_downloadedUpdate != null) TryInstallUpdate();
        }

        // 用户正在看翻译或开着菜单时不重启，过一会儿再试。
        private void TryInstallUpdate()
        {
            if (_popup.IsVisible || _popup.IsMenuOpen)
            {
                var retry = new System.Windows.Threading.DispatcherTimer { Interval = InstallRetry };
                retry.Tick += delegate
                {
                    retry.Stop();
                    TryInstallUpdate();
                };
                retry.Start();
                return;
            }
            Log.Write("installing update " + _downloadedUpdate);
            if (Updater.Install(_downloadedUpdate)) Exit();
        }

        private static Icon LoadTrayIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico"))
                return new Icon(stream, SystemInformation.SmallIconSize);
        }

        private void ShowFirstRunBalloon()
        {
            if (_settings.HintShown) return;
            _settings.HintShown = true;
            _settings.Save();
            _tray.ShowBalloonTip(4000, "Double Ctrl 已在后台运行", "选中文字后双击 Ctrl 即可翻译。", ToolTipIcon.Info);
        }

        private void OnDoubleCtrl()
        {
            // 取词可能要等几百毫秒，放到线程池，不阻塞按键监听。
            if (Interlocked.Exchange(ref _capturing, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Capture(); }
                catch (Exception e) { Log.Write("capture failed: " + e); }
                finally { Volatile.Write(ref _capturing, 0); }
            });
        }

        private void Capture()
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground == _popup.Handle)
            {
                OnUi(_popup.OnDoubleCtrlInside);
                return;
            }

            int state;
            if (Native.SHQueryUserNotificationState(out state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN)
            {
                Log.Write("skipped: fullscreen game");
                return;
            }

            var anchor = FindAnchor(foreground);
            _lastAnchor = anchor;
            string text = Normalize(_reader.Read(foreground));

            Log.Write("captured: [" + text + "]");
            if (text.Length > 0)
            {
                OnUi(delegate { _popup.ShowSelection(text, anchor); });
                return;
            }
            // 没选中文字但焦点在输入框里：打开输入翻译，译文写回这个输入框。
            var target = InputTarget.Detect(foreground, _monitor);
            if (target != null) OnUi(delegate { _popup.ShowCompose(target); });
            else OnUi(delegate { _popup.ShowForInput(anchor); });
        }

        private static string Normalize(string text)
        {
            text = text == null ? "" : text.Replace("\r\n", "\n").Trim();
            return text.Length > DictClient.MaxQueryLength ? text.Substring(0, DictClient.MaxQueryLength) : text;
        }

        // 优先贴着文本光标弹出，键盘选中时鼠标可能在别处；拿不到光标时再用鼠标位置。
        private static Native.POINT FindAnchor(IntPtr foreground)
        {
            uint pid;
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            if (Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(foreground, out pid), ref info) && info.hwndCaret != IntPtr.Zero)
            {
                var caret = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
                if (Native.ClientToScreen(info.hwndCaret, ref caret)) return caret;
            }
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            return cursor;
        }

        private void WatchSignal(string name, Action handler)
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            var thread = new Thread(() =>
            {
                while (signal.WaitOne()) OnUi(handler);
            }) { IsBackground = true };
            thread.Start();
        }

        private void OnUi(Action action)
        {
            _app.Dispatcher.BeginInvoke(action);
        }

        private void Exit()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _app.Shutdown();
        }
    }
}
