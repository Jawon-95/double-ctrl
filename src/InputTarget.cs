using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace DoubleCtrl
{
    // 输入翻译的目标输入框。写入方式按能否不切焦点依次选择：
    //   1. 标准 Edit：EM_SETSEL + EM_REPLACESEL
    //   2. UI Automation 的 ValuePattern：WPF、WinForms、UWP 的原生控件
    //   3. 切回目标窗口，选中上次写入的译文后粘贴：浏览器、Electron 和其他自绘控件
    // 网页里不用 ValuePattern 写入：直接改 DOM 会绕过网页编辑器自己的状态，发出去的还是旧内容。
    // 但可以用 UIA 读内容、选中文字，粘贴前后都按实际内容核对，不靠数光标。
    internal sealed class InputTarget
    {
        private const uint MessageTimeoutMs = 200;
        private const int DetectTimeoutMs = 1000; // Chromium 第一次被请求时要先建无障碍树
        private const int UiaTimeoutMs = 500;
        private const int ForegroundTimeoutMs = 500;
        private const int FocusTimeoutMs = 150;
        private const int FocusSettleMs = 60; // 读不到内容时只能按经验等焦点落定
        private const int SelectTimeoutMs = 150; // Chromium 的无障碍选区动作是异步生效的
        private const int VerifyTimeoutMs = 600;
        private const int PasteSettleMs = 300; // 读不到内容时无法确认目标程序何时读完剪贴板，只能多等

        private static readonly HashSet<string> NativeFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WPF", "WinForm", "XAML", "DirectUI", "Win32",
        };

        private readonly IntPtr _window;
        private readonly IntPtr _focus;
        private readonly InputMonitor _monitor;
        private AutomationElement _element;
        private ValuePattern _value; // 可以直接写入的原生控件
        private ValuePattern _valueReader; // 网页里只用来读内容
        private TextPattern _text; // 网页里用来读内容和选中上次的译文
        private KeyBlocker _blocker;
        private bool _isEdit, _multiline;
        private int _start = -1; // 已写入译文在目标文本里的位置，只有直接写入时才知道
        private string _inserted = "";

        private InputTarget(IntPtr window, IntPtr focus, InputMonitor monitor)
        {
            _window = window;
            _focus = focus;
            _monitor = monitor;
        }

        // 屏幕上的位置，物理像素。写入后输入框可能长高，由窗口重新读取并更新。
        public Native.RECT Bounds { get; set; }

        // 粘贴写入时要把焦点暂时交还给目标窗口。
        public bool NeedsFocus
        {
            get { return !_isEdit && _value == null; }
        }

        // 焦点在可编辑的输入框里才返回目标，密码框、只读框和终端都不算。
        public static InputTarget Detect(IntPtr foreground, InputMonitor monitor)
        {
            uint pid;
            uint thread = Native.GetWindowThreadProcessId(foreground, out pid);
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            Native.GetGUIThreadInfo(thread, ref info);
            var focus = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : foreground;
            if (SelectionReader.IsConsole(SelectionReader.ProcessName(pid), SelectionReader.ClassOf(foreground))) return null;

            var target = new InputTarget(foreground, focus, monitor);
            if (SelectionReader.IsEditClass(SelectionReader.ClassOf(focus)))
                return target.InitEdit() ? target : null;
            if (target.InitUia(pid)) return target;

            // 拿不到控件信息时，有文本光标也算在输入，只是不知道输入框多大，按光标所在的行处理。
            if (info.hwndCaret != IntPtr.Zero)
            {
                target = new InputTarget(foreground, focus, monitor); // UIA 超时的话，上一个对象之后还可能被改写
                var topLeft = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
                var bottom = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
                if (Native.ClientToScreen(info.hwndCaret, ref topLeft) && Native.ClientToScreen(info.hwndCaret, ref bottom))
                {
                    target.Bounds = new Native.RECT { Left = topLeft.X, Top = topLeft.Y, Right = topLeft.X + 1, Bottom = bottom.Y };
                    Log.Write("input target: caret");
                    return target;
                }
            }
            return null;
        }

        private bool InitEdit()
        {
            int style = Native.GetWindowLong(_focus, Native.GWL_STYLE);
            if ((style & (Native.ES_PASSWORD | Native.ES_READONLY)) != 0) return false;
            IntPtr selection;
            if (Native.SendMessageTimeout(_focus, Native.EM_GETSEL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out selection) == IntPtr.Zero)
                return false;
            Native.RECT rect;
            Native.GetWindowRect(_focus, out rect);
            Bounds = rect;
            _isEdit = true;
            _multiline = (style & Native.ES_MULTILINE) != 0;
            _start = selection.ToInt32() & 0xFFFF;
            Log.Write("input target: edit multiline=" + _multiline);
            return true;
        }

        private bool InitUia(uint pid)
        {
            var task = Task.Run(() =>
            {
                var element = AutomationElement.FocusedElement;
                if (element == null) return false;
                var current = element.Current;
                if (current.ProcessId != (int)pid || current.IsPassword || !current.IsEnabled) return false;

                object pattern;
                var value = element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) ? (ValuePattern)pattern : null;
                // 没有 ValuePattern 的编辑区（比如网页里的 contenteditable）按控件类型判断。
                bool editable = value != null ? !value.Current.IsReadOnly : current.ControlType == ControlType.Edit;
                var rect = current.BoundingRectangle;
                if (!editable || rect.IsEmpty || rect.Width < 1) return false;

                _element = element;
                Bounds = ToRect(rect);
                if (value != null && NativeFrameworks.Contains(current.FrameworkId))
                {
                    _value = value;
                    _start = CaretOffset(element, value.Current.Value ?? "");
                }
                else
                {
                    _valueReader = value;
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) _text = (TextPattern)pattern;
                }
                Log.Write("input target: uia framework=" + current.FrameworkId + " type=" + current.ControlType.ProgrammaticName
                    + " class=" + current.ClassName + " value=" + (_value != null) + " text=" + (_text != null));
                return true;
            });
            try { return task.Wait(DetectTimeoutMs) && task.Result; }
            catch (AggregateException e)
            {
                Log.Write("input target uia: " + e.InnerException.Message);
                return false;
            }
        }

        private static int CaretOffset(AutomationElement element, string value)
        {
            object pattern;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return value.Length;
            var text = (TextPattern)pattern;
            var selection = text.GetSelection();
            if (selection.Length == 0) return value.Length;
            var before = text.DocumentRange.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            return Math.Min(before.GetText(-1).Length, value.Length);
        }

        // 输入框会随内容长高，写入后重新取位置。只有光标信息时取不到，返回 null。
        public Native.RECT? ReadBounds()
        {
            if (_isEdit)
            {
                Native.RECT rect;
                return Native.GetWindowRect(_focus, out rect) ? rect : (Native.RECT?)null;
            }
            if (_element == null) return null;
            var task = Task.Run(() => _element.Current.BoundingRectangle);
            try
            {
                if (!task.Wait(UiaTimeoutMs) || task.Result.IsEmpty) return null;
                return ToRect(task.Result);
            }
            catch (AggregateException) { return null; } // 输入框已经不在了
        }

        // 用 text 替换上次写入的译文，text 为空就是删掉。在后台线程调用。
        // 返回 null 表示用户又开始打字，这次没动原输入框，稍后再写。
        public bool? Write(string text, Func<bool> userBusy)
        {
            if (_isEdit)
            {
                if (WriteEdit(text)) return true;
                _isEdit = false;
                Log.Write("input target: edit write failed, fallback to paste");
            }
            if (_value != null)
            {
                bool? written = WriteValue(text);
                if (written == true) return true;
                // 超时的写入之后可能还会生效，这时再粘贴就重复了，这次先放弃。
                if (written == null) return false;
                _value = null;
                Log.Write("input target: value write failed, fallback to paste");
            }
            return WritePaste(text, userBusy);
        }

        // 先在原位置核对上次写入的内容，用户改动过就按内容重新找，找不到就当作已被删掉。
        private int Locate(string current)
        {
            if (_start >= 0 && _start + _inserted.Length <= current.Length
                && string.CompareOrdinal(current, _start, _inserted, 0, _inserted.Length) == 0)
                return _start;
            return _inserted.Length > 0 ? current.LastIndexOf(_inserted, StringComparison.Ordinal) : -1;
        }

        private bool WriteEdit(string text)
        {
            text = _multiline ? text.Replace("\n", "\r\n") : text.Replace("\n", " ");
            IntPtr length;
            if (Native.SendMessageTimeout(_focus, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out length) == IntPtr.Zero)
                return false;
            int total = length.ToInt32();
            if (total + text.Length >= 0xFFFF) return false; // EM_GETSEL 只有 16 位

            var buffer = new StringBuilder(total + 1);
            IntPtr copied;
            if (Native.SendMessageTimeoutText(_focus, Native.WM_GETTEXT, new IntPtr(total + 1), buffer, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out copied) == IntPtr.Zero)
                return false;
            string current = buffer.ToString();

            int start = Locate(current);
            int end = start >= 0 ? start + _inserted.Length : -1;
            if (start < 0)
            {
                IntPtr selection;
                if (Native.SendMessageTimeout(_focus, Native.EM_GETSEL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out selection) == IntPtr.Zero)
                    return false;
                start = end = selection.ToInt32() & 0xFFFF;
            }

            IntPtr ignored;
            if (Native.SendMessageTimeout(_focus, Native.EM_SETSEL, new IntPtr(start), new IntPtr(end), Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out ignored) == IntPtr.Zero)
                return false;
            // wParam=1：用户可以在原输入框里 Ctrl+Z 撤销。
            if (Native.SendMessageTimeoutString(_focus, Native.EM_REPLACESEL, new IntPtr(1), text, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out ignored) == IntPtr.Zero)
                return false;
            _start = start;
            _inserted = text;
            return true;
        }

        private bool? WriteValue(string text)
        {
            text = text.Replace("\n", Environment.NewLine);
            var value = _value;
            var task = Task.Run(() =>
            {
                string current = value.Current.Value ?? "";
                int start = Locate(current);
                int length = _inserted.Length;
                if (start < 0)
                {
                    start = _start >= 0 && _start <= current.Length ? _start : current.Length;
                    length = 0;
                }
                string next = current.Substring(0, start) + text + current.Substring(start + length);
                if (next == current) return true;
                value.SetValue(next);
                // 有的控件会静默拒绝，读回来确认。
                if ((value.Current.Value ?? "") == current) return false;
                _start = start;
                _inserted = text;
                return true;
            });
            try { return task.Wait(UiaTimeoutMs) ? task.Result : (bool?)null; }
            catch (AggregateException e)
            {
                Log.Write("input target value: " + e.InnerException.Message);
                return false;
            }
        }

        // 切到目标窗口写入。期间扣下用户的按键，等弹窗拿回前台后由 ResumeKeys 补发，
        // 否则这段时间里打的字会落进原输入框，光标看起来就在两个输入框之间跳。
        private bool? WritePaste(string text, Func<bool> userBusy)
        {
            if (!Native.IsWindow(_window)) return false;
            SelectionReader.WaitForModifiersReleased();
            Native.timeBeginPeriod(1); // 下面的等待都按毫秒轮询，默认 15ms 的时钟精度太粗
            try
            {
                if (_blocker == null) _blocker = KeyBlocker.Start();
                // 钩子装好后再问一次：此后的按键都会被扣下，不会再有新的拼写开始；
                // 装好之前已经开始的拼写这里能发现。切走焦点会让输入法把拼音原样上屏，所以先不写。
                if (userBusy != null && userBusy())
                {
                    Log.Write("input target: paste deferred, user typing");
                    return null;
                }
                _monitor.SuppressInjected(ForegroundTimeoutMs + FocusTimeoutMs + SelectTimeoutMs + VerifyTimeoutMs);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool written = WritePasteCore(text);
                Log.Write("input target: paste written=" + written + " ms=" + watch.ElapsedMilliseconds);
                return written;
            }
            finally { Native.timeEndPeriod(1); }
        }

        // 弹窗拿回前台后调用，把写入期间扣下的按键补发给弹窗。不需要切焦点时什么也不做。
        public void ResumeKeys()
        {
            var blocker = Interlocked.Exchange(ref _blocker, null);
            if (blocker == null) return;
            string keys;
            int count = blocker.StopAndReplay(out keys);
            if (count > 0) Log.Write("input target: replayed keys=" + count + " [" + keys + "]");
        }

        private bool WritePasteCore(string text)
        {
            // 读内容不需要窗口在前台，先读；译文没变就不用切过去了。
            bool readable = _text != null || _valueReader != null;
            string before = readable ? ReadText() : null;
            bool present = _inserted.Length > 0 && (before == null || Collapse(before).Contains(Collapse(_inserted)));
            if (before != null && (present || _inserted.Length == 0) && text == _inserted) return true;

            if (!ActivateWindow()) return false;
            if (before == null) Thread.Sleep(FocusSettleMs);

            // 上次的译文还在原处：用 UIA 直接选中它；读不到内容或选不中时才退回按字数 Shift+←。
            // 已经不在了（用户自己删改过）就在光标处插入，不能再往左选，否则会删掉用户的内容。
            bool replace = false;
            var inputs = new List<Native.INPUT>();
            if (_inserted.Length > 0)
            {
                if (present)
                {
                    replace = true;
                    if (before == null || !SelectInserted()) AddSelectLeft(inputs, new StringInfo(_inserted).LengthInTextElements);
                }
            }

            ClipboardSnapshot snapshot = null;
            if (text.Length > 0)
            {
                snapshot = ClipboardSnapshot.Take();
                if (snapshot == null || !ClipboardSnapshot.SetText(text.Replace("\n", "\r\n"))) return false;
                inputs.Add(SelectionReader.Key(Native.VK_CONTROL, 0));
                inputs.Add(SelectionReader.Key(Native.VK_V, 0));
                inputs.Add(SelectionReader.Key(Native.VK_V, Native.KEYEVENTF_KEYUP));
                inputs.Add(SelectionReader.Key(Native.VK_CONTROL, Native.KEYEVENTF_KEYUP));
            }
            else if (replace)
            {
                inputs.Add(SelectionReader.Key(Native.VK_DELETE, Native.KEYEVENTF_EXTENDEDKEY));
                inputs.Add(SelectionReader.Key(Native.VK_DELETE, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP));
            }

            if (inputs.Count > 0)
                Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Native.INPUT)));

            // 能读内容时等新内容真正出现：既确认写进去了，也说明目标程序已经读完剪贴板，可以马上恢复。
            bool unchanged = false;
            if (before != null)
            {
                if (inputs.Count > 0 && !WaitForChange(before, text))
                {
                    unchanged = ReadText() == before;
                    Log.Write("input target: paste not confirmed, unchanged=" + unchanged);
                }
            }
            else if (snapshot != null) Thread.Sleep(PasteSettleMs);
            if (snapshot != null) snapshot.Restore();
            // 按键被目标程序吞掉了：原输入框里还是上次的译文，记录不能更新，否则下次会当成已删掉而重复插入。
            if (unchanged) return false;
            _inserted = text;
            return true;
        }

        private bool ActivateWindow()
        {
            Native.SetForegroundWindow(_window);
            int start = Environment.TickCount;
            while (Native.GetForegroundWindow() != _window)
            {
                if (Environment.TickCount - start > ForegroundTimeoutMs) return false;
                Thread.Sleep(1);
            }
            // 窗口激活后焦点要先回到原来的子窗口和输入框，按键和选区才会落在输入框里。
            uint pid;
            uint thread = Native.GetWindowThreadProcessId(_window, out pid);
            start = Environment.TickCount;
            while (Environment.TickCount - start < FocusTimeoutMs)
            {
                var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                if (Native.GetGUIThreadInfo(thread, ref info) && info.hwndFocus == _focus && HasKeyboardFocus()) break;
                Thread.Sleep(1);
            }
            return true;
        }

        private bool HasKeyboardFocus()
        {
            if (_element == null) return true;
            return Uia(() => _element.Current.HasKeyboardFocus, false);
        }

        private string ReadText()
        {
            return Uia(() => _text != null ? _text.DocumentRange.GetText(-1) : _valueReader.Current.Value ?? "", null);
        }

        // 在光标前找上次的译文并选中，避免选到前面碰巧相同的文字。选区生效后才返回。
        private bool SelectInserted()
        {
            if (_text == null) return false;
            string want = Collapse(_inserted);
            return Uia(() =>
            {
                var document = _text.DocumentRange;
                TextPatternRange found = null;
                var selection = _text.GetSelection();
                if (selection.Length > 0)
                {
                    var head = document.Clone();
                    head.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.End);
                    found = head.FindText(_inserted, true, false);
                }
                if (found == null) found = document.FindText(_inserted, true, false);
                if (found == null) return false;
                found.Select();
                int start = Environment.TickCount;
                while (Environment.TickCount - start < SelectTimeoutMs)
                {
                    var now = _text.GetSelection();
                    if (now.Length > 0 && Collapse(now[0].GetText(-1)) == want) return true;
                    Thread.Sleep(1);
                }
                // 已经发出选区动作，再按字数 Shift+← 会和它叠加，只能按已选中处理。
                Log.Write("input target: selection not confirmed");
                return true;
            }, false);
        }

        private bool WaitForChange(string before, string text)
        {
            string want = Collapse(text);
            int start = Environment.TickCount;
            while (Environment.TickCount - start < VerifyTimeoutMs)
            {
                string now = ReadText();
                if (now != null && now != before && (want.Length == 0 || Collapse(now).Contains(want))) return true;
                Thread.Sleep(1);
            }
            return false;
        }

        private static void AddSelectLeft(List<Native.INPUT> inputs, int count)
        {
            inputs.Add(SelectionReader.Key(Native.VK_SHIFT, 0));
            for (int i = 0; i < count; i++)
            {
                inputs.Add(SelectionReader.Key(Native.VK_LEFT, Native.KEYEVENTF_EXTENDEDKEY));
                inputs.Add(SelectionReader.Key(Native.VK_LEFT, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP));
            }
            inputs.Add(SelectionReader.Key(Native.VK_SHIFT, Native.KEYEVENTF_KEYUP));
        }

        // 网页编辑器会把换行拆成段落、把空格换成 &nbsp;，比较内容时把空白都折叠成一个空格。
        private static string Collapse(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || c == ' ')
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // UIA 跨进程调用，目标程序卡住时不能把写入线程一起拖住。
        private static T Uia<T>(Func<T> call, T fallback)
        {
            var task = Task.Run(call);
            try { return task.Wait(UiaTimeoutMs) ? task.Result : fallback; }
            catch (AggregateException e)
            {
                Log.Write("input target uia: " + e.InnerException.Message);
                return fallback;
            }
        }

        private static Native.RECT ToRect(System.Windows.Rect rect)
        {
            return new Native.RECT
            {
                Left = (int)Math.Floor(rect.Left), Top = (int)Math.Floor(rect.Top),
                Right = (int)Math.Ceiling(rect.Right), Bottom = (int)Math.Ceiling(rect.Bottom),
            };
        }
    }
}
