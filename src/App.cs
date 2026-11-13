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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace DoubleCtrl
{
    internal sealed class App
    {
        private const string MutexName = "DoubleCtrl.SingleInstance";
        private const string ShowEventName = "DoubleCtrl.Show";
        private const string QuitEventName = "DoubleCtrl.Quit";

        private readonly Application _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        private readonly Settings _settings = Settings.Load();
        private readonly InputMonitor _monitor = new InputMonitor();
        private readonly SelectionReader _reader;
        private PopupController _popup;
        private NotifyIcon _tray;
        private int _capturing;
        private Native.POINT _lastAnchor;

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
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    SignalRunningInstance(args);
                    return;
                }
                if (args.Contains("--quit")) return;
                new App().Run(args.Contains(AutoStart.Argument));
                GC.KeepAlive(mutex);
            }
        }

        private static void SignalRunningInstance(string[] args)
        {
            if (args.Contains(AutoStart.Argument)) return;
            string name = args.Contains("--quit") ? QuitEventName : ShowEventName;
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name))
                signal.Set();
        }

        private void Run(bool autostart)
        {
            // 常驻后台的工具不该因为一处界面异常就整个退出，记下来继续运行。
            _app.DispatcherUnhandledException += (sender, e) =>
            {
                Log.Crash(e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Log.Crash(e.ExceptionObject);

            AutoStart.Refresh();
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

            if (autostart || _settings.StartHidden) ShowFirstRunBalloon();
            else OnUi(delegate { _popup.ShowForInput(null); });

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
