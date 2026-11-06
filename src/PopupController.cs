using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DoubleCtrl
{
    internal sealed class PopupController
    {
        private const string HintText = "选中文字后双击 Ctrl 即可翻译，也可以在上方直接输入。";
        private const int AnchorGapPx = 14;
        private const double ShadowMargin = 12;
        private const double TitleHeight = 42;
        private const double ExpectedHeight = 510; // 结果最长时窗口的高度，用来提前判断往哪边弹
        private const double MaxResultsHeight = 340;
        private const double NonResultsHeight = 170;
        private const double MenuReopenGuardMs = 300;
        private const double MenuWidth = 176;
        private const double CollapsedScale = 0.72;
        private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(150);

        private static readonly KeyValuePair<string, string>[] Languages =
        {
            new KeyValuePair<string, string>("en", "英汉互译"),
            new KeyValuePair<string, string>("fr", "法汉互译"),
            new KeyValuePair<string, string>("ja", "日汉互译"),
            new KeyValuePair<string, string>("ko", "韩汉互译"),
            new KeyValuePair<string, string>("de", "德汉互译"),
            new KeyValuePair<string, string>("es", "西汉互译"),
            new KeyValuePair<string, string>("ru", "俄汉互译"),
            new KeyValuePair<string, string>("pt", "葡汉互译"),
        };

        private static readonly KeyValuePair<string, string>[] ThemeOptions =
        {
            new KeyValuePair<string, string>(Theme.System, "系统"),
            new KeyValuePair<string, string>(Theme.Light, "浅色"),
            new KeyValuePair<string, string>(Theme.Dark, "深色"),
        };

        private readonly Settings _settings;
        private readonly VoicePlayer _voice = new VoicePlayer();
        private readonly Window _window;
        private readonly TextBox _input;
        private readonly StackPanel _results;
        private readonly ScrollViewer _scroller;
        private readonly Button _pin, _more, _clear, _lang, _moreLink;
        private readonly TextBlock _langText;
        private readonly FrameworkElement _bottomGap, _titleBar, _card, _root;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly DispatcherTimer _typingTimer, _hoverTimer;
        private readonly IntPtr _hwnd;

        private bool _pinned, _settingText, _hovering, _inputMode, _growUp, _closing;
        private IosMenu _menu;
        private object _menuOwner, _lastMenuOwner;
        private int _menuClosedTick;
        private int _queryId;
        private Native.POINT? _anchor;
        private DictResult _current;

        public event Action ExitRequested;

        public PopupController(Settings settings)
        {
            _settings = settings;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Popup.xaml"))
                _window = (Window)XamlReader.Load(stream);

            _input = Find<TextBox>("Input");
            _results = Find<StackPanel>("Results");
            _scroller = Find<ScrollViewer>("Scroller");
            _pin = Find<Button>("PinButton");
            _more = Find<Button>("MoreButton");
            _clear = Find<Button>("ClearButton");
            _lang = Find<Button>("LangButton");
            _moreLink = Find<Button>("MoreLink");
            _langText = Find<TextBlock>("LangText");
            _bottomGap = Find<FrameworkElement>("BottomGap");
            _titleBar = Find<FrameworkElement>("TitleBar");
            _card = Find<FrameworkElement>("Card");

            Find<Image>("AppIcon").Source = LoadIcon();

            _hwnd = new WindowInteropHelper(_window).EnsureHandle();
            _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _typingTimer.Tick += delegate { _typingTimer.Stop(); QueryInput(); };

            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _hoverTimer.Tick += delegate
            {
                _hoverTimer.Stop();
                _hovering = false;
                UpdateTitleBar();
            };
            _root = Find<FrameworkElement>("Root");
            _root.RenderTransform = _scale;
            _root.MouseEnter += delegate
            {
                _hoverTimer.Stop();
                _hovering = true;
                UpdateTitleBar();
            };
            _root.MouseLeave += delegate { _hoverTimer.Start(); };

            _titleBar.MouseLeftButtonDown += delegate { DragWindow(); };
            Find<Button>("CloseButton").Click += delegate { Close(); };
            Find<Button>("SearchButton").Click += delegate { QueryInput(); };
            _pin.Click += delegate { SetPinned(!_pinned); };
            _clear.Click += delegate { Clear(); };
            _more.Click += delegate { ShowMoreMenu(); };
            _lang.Click += delegate { ShowLanguageMenu(); };
            _moreLink.Click += delegate { OpenInBrowser(); };

            _input.TextChanged += OnInputChanged;
            _input.PreviewKeyDown += OnInputKeyDown;
            _window.PreviewKeyDown += OnWindowKeyDown;
            _window.Deactivated += OnDeactivated;
            _window.SizeChanged += delegate { PlaceAtAnchor(); };

            UpdateLanguageLabel();
            ShowHint();
        }

        public IntPtr Handle { get { return _hwnd; } }

        public bool IsVisible { get { return _window.IsVisible; } }

        // 有选中文字：不抢焦点，否则资源管理器的重命名框一失焦就会提交退出。
        public void ShowSelection(string text, Native.POINT anchor)
        {
            SetInput(text);
            _inputMode = false;
            UpdateTitleBar();
            MoveTo(anchor);
            _window.ShowActivated = false;
            Reveal();
            QueryInput();
        }

        // 没有选中文字：激活窗口，方便直接输入。
        public void ShowForInput(Native.POINT? anchor)
        {
            _inputMode = true;
            UpdateTitleBar();
            if (anchor.HasValue) MoveTo(anchor.Value);
            else if (!_window.IsVisible) CenterOnCursorScreen();
            _window.ShowActivated = false;
            Reveal();
            TakeForeground();
            _window.Activate();
            _input.Focus();
            _input.SelectAll();
        }

        // 窗口里按双击 Ctrl：有选中文字就原地重新翻译，不改变窗口。
        public void OnDoubleCtrlInside()
        {
            var box = Keyboard.FocusedElement as TextBox;
            string text = box != null ? box.SelectedText.Trim() : "";
            if (text.Length > 0) Retranslate(text);
            else ShowForInput(null);
        }

        public void Hide()
        {
            _typingTimer.Stop();
            if (_menuOwner != null) CloseMenu();
            if (!_window.IsVisible || _closing) return;
            _closing = true;
            var collapse = Animate(CollapsedScale, CloseDuration, EasingMode.EaseIn);
            // 收缩途中又弹出时 _closing 已被清掉，这里不能再把窗口藏起来。
            collapse.Completed += delegate
            {
                if (!_closing) return;
                _closing = false;
                _window.Hide();
            };
            Start(collapse, 0, CloseDuration);
        }

        // 用户主动关闭：同时解除固定，下次弹出恢复点击外部自动隐藏。
        private void Close()
        {
            SetPinned(false);
            Hide();
        }

        public void OnGlobalMouseDown()
        {
            if (_menu != null)
            {
                if (_menu.ContainsCursor()) return;
                CloseMenu();
            }
            if (!_window.IsVisible || _closing || _pinned) return;
            Native.POINT cursor;
            Native.RECT rect;
            Native.GetCursorPos(out cursor);
            Native.GetWindowRect(_hwnd, out rect);
            if (cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom)
                Hide();
        }

        public void OnGlobalEscape()
        {
            if (_menu != null) CloseMenu();
            else if (_window.IsVisible && !_window.IsActive && !_pinned) Hide();
        }

        private T Find<T>(string name) where T : class
        {
            return (T)_window.FindName(name);
        }

        private void Reveal()
        {
            bool hidden = !_window.IsVisible;
            if (hidden || _closing)
            {
                _closing = false;
                // 动画先于 Show 启动，窗口第一帧就是收缩状态，不会闪一下完整大小。
                SetAnimationOrigin();
                Start(Animate(1, OpenDuration, EasingMode.EaseOut), 1, OpenDuration, hidden);
            }
            if (hidden) _window.Show();
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private static DoubleAnimation Animate(double to, TimeSpan duration, EasingMode mode)
        {
            return new DoubleAnimation(to, duration) { EasingFunction = new CubicEase { EasingMode = mode } };
        }

        private void Start(DoubleAnimation scale, double opacity, TimeSpan duration, bool fromCollapsed = false)
        {
            if (fromCollapsed) scale.From = CollapsedScale;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            var fade = new DoubleAnimation(opacity, duration);
            if (fromCollapsed) fade.From = 0;
            _root.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 缩放中心放在可见卡片的中心。标题栏隐藏时卡片上方是透明区，不能用整个窗口的中心。
        private void SetAnimationOrigin()
        {
            double height = _root.ActualHeight;
            if (height <= TitleHeight) return;
            double top = _titleBar.Visibility == Visibility.Visible ? 0 : TitleHeight;
            _root.RenderTransformOrigin = new Point(0.5, (top + (height - top) / 2) / height);
        }

        // 后台进程直接调用 SetForegroundWindow 会被前台锁拒绝，要先挂到当前前台线程上。
        private void TakeForeground()
        {
            uint pid;
            uint foreground = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            uint self = Native.GetCurrentThreadId();
            bool attached = foreground != self && Native.AttachThreadInput(self, foreground, true);
            Native.SetForegroundWindow(_hwnd);
            if (attached) Native.AttachThreadInput(self, foreground, false);
        }

        private void SetPinned(bool pinned)
        {
            _pinned = pinned;
            _pin.Content = pinned ? "\uE840" : "\uE718";
            _pin.SetResourceReference(Control.ForegroundProperty, pinned ? "Accent" : "Muted");
            UpdateTitleBar();
        }

        // 拖动过的窗口视为用户想留着参考，自动固定。只点一下标题栏不算。
        private void DragWindow()
        {
            Native.RECT before, after;
            Native.GetWindowRect(_hwnd, out before);
            _window.DragMove();
            Native.GetWindowRect(_hwnd, out after);
            if (before.Left == after.Left && before.Top == after.Top) return;
            _anchor = null;
            SetPinned(true);
        }

        private void UpdateTitleBar()
        {
            bool show = _pinned || _inputMode || _hovering || _menu != null;
            _titleBar.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            Grid.SetRow(_card, show ? 0 : 1);
            Grid.SetRowSpan(_card, show ? 2 : 1);
        }

        private void MoveTo(Native.POINT anchor)
        {
            if (_pinned && _window.IsVisible) return;
            _anchor = anchor;
            ChooseDirection(anchor);
            PlaceAtAnchor();
        }

        // 弹出时结果还没回来，窗口之后才长高，所以要按最终高度提前决定方向：
        // 下方放不下就往上长，窗口底边贴着锚点，内容变多时只向上延伸。
        private void ChooseDirection(Native.POINT anchor)
        {
            var work = WorkArea(anchor);
            double scale = VisualTreeHelper.GetDpi(_window).DpiScaleY;
            int expected = (int)(ExpectedHeight * scale);
            int below = work.Bottom - (anchor.Y + AnchorGapPx - (int)((ShadowMargin + TitleHeight) * scale));
            int above = anchor.Y - AnchorGapPx - work.Top + (int)(ShadowMargin * scale);
            _growUp = below < expected && above > below;
            _scroller.MaxHeight = Math.Min(MaxResultsHeight, (work.Bottom - work.Top) / scale - NonResultsHeight);
        }

        // 所有坐标都用物理像素，避免多显示器不同缩放时换算出错。
        private void PlaceAtAnchor()
        {
            Native.RECT rect;
            Native.GetWindowRect(_hwnd, out rect);
            double scale = VisualTreeHelper.GetDpi(_window).DpiScaleY;
            // SizeChanged 触发时窗口句柄还没改尺寸，GetWindowRect 拿到的是旧高度，要用 WPF 的实际尺寸。
            int width = _window.ActualWidth > 0 ? (int)Math.Ceiling(_window.ActualWidth * scale) : rect.Right - rect.Left;
            int height = _window.ActualHeight > 0 ? (int)Math.Ceiling(_window.ActualHeight * scale) : rect.Bottom - rect.Top;
            if (!_anchor.HasValue)
            {
                KeepOnScreen(rect, height);
                return;
            }

            var anchor = _anchor.Value;
            var work = WorkArea(anchor);
            int shadow = (int)(ShadowMargin * scale);
            int topInset = (int)((ShadowMargin + TitleHeight) * scale); // 卡片上方的透明区不算占位

            int x = anchor.X - shadow;
            if (x + width > work.Right) x = anchor.X - width + shadow; // 右边放不下就放到鼠标左侧
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            int y = _growUp ? anchor.Y - AnchorGapPx - height + shadow : anchor.Y + AnchorGapPx - topInset;
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // 固定后结果变长时，别让窗口底部跑出屏幕。
        private void KeepOnScreen(Native.RECT rect, int height)
        {
            var corner = new Native.POINT { X = rect.Left, Y = rect.Top };
            var work = WorkArea(corner);
            if (rect.Top + height <= work.Bottom) return;
            int y = Math.Max(work.Top, work.Bottom - height);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        private void CenterOnCursorScreen()
        {
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            var work = WorkArea(cursor);
            Native.RECT rect;
            Native.GetWindowRect(_hwnd, out rect);
            _anchor = null;
            int x = (work.Left + work.Right - (rect.Right - rect.Left)) / 2;
            int y = work.Top + (work.Bottom - work.Top) / 4;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        private static Native.RECT WorkArea(Native.POINT point)
        {
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            Native.GetMonitorInfo(Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST), ref info);
            return info.rcWork;
        }

        // 抢前台的过程中会收到瞬时的 Deactivated，等消息处理完再确认是否真的失去了前台。
        private void OnDeactivated(object sender, EventArgs e)
        {
            _window.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!_pinned && _menu == null && Native.GetForegroundWindow() != _hwnd) Hide();
            }), DispatcherPriority.Background);
        }

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            _clear.Visibility = _input.Text.Length > 0 ? Visibility.Visible : Visibility.Hidden;
            _input.FontSize = _input.Text.Length > 24 ? 14 : 20;
            if (_settingText) return;
            _typingTimer.Stop();
            if (_input.Text.Trim().Length == 0) ShowHint();
            else _typingTimer.Start();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _typingTimer.Stop();
            QueryInput();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        private void Retranslate(string text)
        {
            _typingTimer.Stop();
            SetInput(text);
            QueryInput();
        }

        private void SetInput(string text)
        {
            _settingText = true;
            _input.Text = text;
            _settingText = false;
            _input.CaretIndex = text.Length;
        }

        private void Clear()
        {
            SetInput("");
            ShowHint();
            _input.Focus();
        }

        private void ShowHint()
        {
            _queryId++;
            _current = null;
            ShowMessage(HintText);
        }

        private void ShowMessage(string text)
        {
            _results.Children.Clear();
            _moreLink.Visibility = Visibility.Collapsed;
            _bottomGap.Visibility = Visibility.Visible;
            _results.Children.Add(Label(text, 12, "Muted"));
        }

        private async void QueryInput()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0)
            {
                ShowHint();
                return;
            }

            int id = ++_queryId;
            ShowMessage("翻译中…");
            try
            {
                var watch = Stopwatch.StartNew();
                var result = await DictClient.QueryAsync(text, _settings.Le);
                Log.Write("query ms=" + watch.ElapsedMilliseconds + " le=" + result.Le + " entries=" + result.Entries.Count + " sentence=" + (result.Sentence != null));
                if (id != _queryId) return;
                _current = result;
                Render(result);
            }
            catch (Exception e)
            {
                if (id != _queryId) return;
                Log.Write("query failed: " + e);
                ShowMessage("翻译失败，请检查网络后重试。");
            }
        }

        private void Render(DictResult result)
        {
            _results.Children.Clear();
            _scroller.ScrollToTop();
            if (result.IsEmpty)
            {
                ShowMessage("没有找到释义。");
                return;
            }

            if (result.Phonetics.Count > 0 && result.Sentence == null)
                _results.Children.Add(BuildPhonetics(result));

            if (result.Sentence != null)
                _results.Children.Add(BuildSentence(result));

            foreach (var entry in result.Entries)
                _results.Children.Add(entry.IsWord ? BuildWordEntry(entry) : BuildEntry(entry));

            if (result.Web.Count > 0)
                _results.Children.Add(BuildLine("网络", string.Join(" / ", result.Web)));

            if (result.Forms.Count > 0)
            {
                var forms = Selectable(string.Join("   ", result.Forms), 11);
                forms.SetResourceReference(Control.ForegroundProperty, "Muted");
                forms.Margin = new Thickness(0, 4, 0, 2);
                _results.Children.Add(forms);
            }

            _moreLink.Visibility = Visibility.Visible;
            _bottomGap.Visibility = Visibility.Collapsed;
        }

        private UIElement BuildPhonetics(DictResult result)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var phonetic in result.Phonetics)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
                if (phonetic.Label != null)
                {
                    var label = Label(phonetic.Label, 12, "Muted");
                    label.Margin = new Thickness(0, 0, 4, 0);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    item.Children.Add(label);
                }
                var text = SingleLine("/" + phonetic.Text + "/", 12);
                text.SetResourceReference(Control.ForegroundProperty, "Muted");
                item.Children.Add(text);
                if (!string.IsNullOrEmpty(phonetic.Speech))
                    item.Children.Add(SpeakerButton(phonetic.Speech));
                row.Children.Add(item);
            }
            return row;
        }

        private UIElement BuildSentence(DictResult result)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            var text = Selectable(result.Sentence, 14);
            panel.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-5, 4, 0, 0) };
            if (!string.IsNullOrEmpty(result.SentenceSpeech))
                actions.Children.Add(SpeakerButton(result.SentenceSpeech));
            var copy = IconButton("\uE8C8", "复制译文");
            copy.Click += delegate { CopyText(result.Sentence); };
            actions.Children.Add(copy);
            panel.Children.Add(actions);
            return panel;
        }

        private UIElement BuildEntry(Entry entry)
        {
            return BuildLine(entry.Pos, entry.Text);
        }

        private UIElement BuildLine(string label, string text)
        {
            var grid = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            if (!string.IsNullOrEmpty(label))
            {
                var pos = Label(label, 12, "Muted");
                pos.TextWrapping = TextWrapping.NoWrap;
                pos.Margin = new Thickness(0, 1, 8, 0);
                DockPanel.SetDock(pos, Dock.Left);
                grid.Children.Add(pos);
            }
            grid.Children.Add(Selectable(text, 13));
            return grid;
        }

        private UIElement BuildWordEntry(Entry entry)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            var word = SingleLine(entry.Text, 14);
            word.FontWeight = FontWeights.Bold;
            if (!string.IsNullOrEmpty(entry.Detail)) word.ToolTip = entry.Detail;
            row.Children.Add(word);
            if (!string.IsNullOrEmpty(entry.Speech))
                row.Children.Add(SpeakerButton(entry.Speech));
            return row;
        }

        private Button SpeakerButton(string speech)
        {
            var button = IconButton("\uE767", "发音");
            button.Click += delegate { _voice.Play(speech); };
            return button;
        }

        private Button IconButton(string glyph, string tip)
        {
            return new Button { Content = glyph, ToolTip = tip, Style = (Style)_window.Resources["IconButton"] };
        }

        private TextBlock Label(string text, double size, string brush)
        {
            var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return label;
        }

        private TextBox Selectable(string text, double size)
        {
            return new TextBox { Text = text, FontSize = size, Style = (Style)_window.Resources["ReadOnlyText"] };
        }

        private TextBox SingleLine(string text, double size)
        {
            var box = Selectable(text, size);
            box.TextWrapping = TextWrapping.NoWrap;
            box.VerticalAlignment = VerticalAlignment.Center;
            return box;
        }

        private static void CopyText(string text)
        {
            try { Clipboard.SetDataObject(text, true); }
            catch (System.Runtime.InteropServices.ExternalException e) { Log.Write("copy: " + e.Message); }
        }

        private void OpenInBrowser()
        {
            string query = _current != null ? _current.Query : _input.Text.Trim();
            string le = _current != null ? _current.Le : _settings.Le;
            Process.Start("https://dict.youdao.com/result?word=" + Uri.EscapeDataString(query) + "&lang=" + le);
        }

        private void UpdateLanguageLabel()
        {
            _langText.Text = Languages.First(l => l.Key == _settings.Le).Value;
        }

        private void ShowLanguageMenu()
        {
            if (JustClosedBy(_lang)) return;
            var menu = new IosMenu(116);
            foreach (var item in Languages)
            {
                var language = item;
                menu.AddChoice(language.Value, language.Key == _settings.Le, delegate
                {
                    _settings.Le = language.Key;
                    _settings.Save();
                    UpdateLanguageLabel();
                    if (_input.Text.Trim().Length > 0) QueryInput();
                });
            }
            OpenMenu(menu, _lang);
            menu.OpenBelow(_lang, false);
        }

        private void ShowMoreMenu()
        {
            if (JustClosedBy(_more)) return;
            var menu = new IosMenu(MenuWidth);
            AddSettings(menu);
            OpenMenu(menu, _more);
            menu.OpenBelow(_more, true);
        }

        public void ShowTrayMenu()
        {
            if (JustClosedBy(this)) return;
            var menu = new IosMenu(MenuWidth);
            menu.AddAction("显示窗口", delegate { ShowForInput(null); });
            menu.AddGroupGap();
            AddSettings(menu);
            OpenMenu(menu, this);
            menu.OpenAtMouse();
        }

        private void AddSettings(IosMenu menu)
        {
            menu.AddSwitch("静默启动", _settings.StartHidden, delegate
            {
                _settings.StartHidden = !_settings.StartHidden;
                _settings.Save();
            });
            menu.AddSwitch("开机自启", AutoStart.IsEnabled, delegate { AutoStart.Set(!AutoStart.IsEnabled); });
            menu.AddSegments("主题", ThemeOptions, _settings.Theme, mode =>
            {
                _settings.Theme = mode;
                _settings.Save();
                Theme.Apply(mode);
            });
            menu.AddGroupGap();
            menu.AddAction("退出", delegate
            {
                var handler = ExitRequested;
                if (handler != null) handler();
            }, true);
        }

        // 菜单开着时再点它的按钮：全局鼠标事件已先把菜单关掉，这里不能又打开。
        private bool JustClosedBy(object owner)
        {
            return owner == _lastMenuOwner && Environment.TickCount - _menuClosedTick < MenuReopenGuardMs;
        }

        private void OpenMenu(IosMenu menu, object owner)
        {
            CloseMenu();
            _menu = menu;
            _menuOwner = owner == this ? null : owner;
            _lastMenuOwner = owner;
            menu.Closed += delegate
            {
                if (_menu != menu) return;
                _menu = null;
                _menuOwner = null;
                _menuClosedTick = Environment.TickCount;
                UpdateTitleBar();
            };
            UpdateTitleBar();
        }

        private void CloseMenu()
        {
            if (_menu != null) _menu.Close();
        }

        private static BitmapSource LoadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico"))
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return decoder.Frames.First(f => f.PixelWidth == 64);
            }
        }
    }

    internal sealed class VoicePlayer
    {
        private const string Alias = "doublectrlvoice";
        private readonly string _file = Path.Combine(Path.GetTempPath(), "doublectrl-voice.mp3");
        private int _playId;

        public async void Play(string speech)
        {
            int id = ++_playId;
            try
            {
                var audio = await DictClient.DownloadVoiceAsync(speech);
                if (id != _playId) return;
                Native.MciSend("close " + Alias, null, 0, IntPtr.Zero);
                File.WriteAllBytes(_file, audio);
                int error = Native.MciSend("open \"" + _file + "\" type mpegvideo alias " + Alias, null, 0, IntPtr.Zero);
                if (error == 0) error = Native.MciSend("play " + Alias, null, 0, IntPtr.Zero);
                if (error != 0) Log.Write("mci error " + error);
            }
            catch (Exception e) { Log.Write("voice: " + e.Message); }
        }
    }
}
