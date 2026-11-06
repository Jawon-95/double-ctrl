using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace DoubleCtrl
{
    // 分三层读取选中文字，能不碰剪贴板就不碰：
    //   1. 标准 Edit 控件：直接读（资源管理器重命名框、地址栏、各种对话框）
    //   2. UI Automation：WPF、UWP、Windows Terminal 等
    //   3. 模拟复制后读剪贴板，再恢复：浏览器、Electron 等
    internal sealed class SelectionReader
    {
        private const uint MessageTimeoutMs = 200;
        private const int UiaTimeoutMs = 450;
        private const int CopyTimeoutMs = 500;
        private const int LateCopyWindowMs = 2500;
        private const int ClipboardSettleMs = 30;

        // 这些程序里没有选区时 Ctrl+C 是中断信号，只能用 Ctrl+Insert。
        private static readonly HashSet<string> TerminalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh", "mintty", "putty", "kitty",
            "xshell", "securecrt", "mobaxterm", "alacritty", "wezterm-gui", "hyper", "tabby",
            "code", "code - insiders", "cursor", "windsurf",
            "idea64", "pycharm64", "webstorm64", "goland64", "clion64", "rider64", "datagrip64", "phpstorm64", "rubymine64", "studio64",
        };

        private static readonly HashSet<string> TerminalClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "PuTTY", "SunAwtFrame",
        };

        private readonly InputMonitor _monitor;

        // 诊断用：关闭剪贴板这一层（--no-clipboard），自动化测试不会动到用户的剪贴板。
        public static bool ClipboardDisabled;

        // 目标程序过了超时才写入剪贴板时，在这里补发选中的文字。
        public event Action<string> LateSelection;

        public SelectionReader(InputMonitor monitor)
        {
            _monitor = monitor;
        }

        public static void WarmUp()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { var root = AutomationElement.RootElement; GC.KeepAlive(root); }
                catch (Exception e) { Log.Write("uia warmup: " + e.Message); }
            });
        }

        public string Read(IntPtr foreground)
        {
            uint pid;
            uint thread = Native.GetWindowThreadProcessId(foreground, out pid);
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            Native.GetGUIThreadInfo(thread, ref info);
            var focus = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : foreground;
            string focusClass = ClassOf(focus);
            string foregroundClass = ClassOf(foreground);
            string process = ProcessName(pid);
            Log.Write(string.Format("target process={0} fg={1} focus={2}", process, foregroundClass, focusClass));

            if (IsEditClass(focusClass))
            {
                bool definitive;
                string text = ReadEdit(focus, out definitive);
                if (definitive)
                {
                    Log.Write("tier=edit len=" + (text ?? "").Length);
                    return text;
                }
            }

            // Chromium 的 UIA 默认关闭，请求它既慢又会拖累目标程序，直接走复制。
            if (!focusClass.StartsWith("Chrome_", StringComparison.Ordinal))
            {
                bool isPassword;
                string text = ReadUia(out isPassword);
                if (isPassword) return null;
                if (!string.IsNullOrEmpty(text))
                {
                    Log.Write("tier=uia len=" + text.Length);
                    return text;
                }
            }

            // 文件列表和桌面上 Ctrl+C 复制的是文件，还会打断待粘贴的“剪切”状态。
            if (string.Equals(process, "explorer", StringComparison.OrdinalIgnoreCase))
                return null;

            if (ClipboardDisabled) return null;

            bool terminal = TerminalProcesses.Contains(process) || TerminalClasses.Contains(foregroundClass);
            string copied = ReadViaClipboard(terminal);
            Log.Write("tier=clipboard terminal=" + terminal + " len=" + (copied ?? "").Length);
            return copied;
        }

        private static bool IsEditClass(string name)
        {
            return string.Equals(name, "Edit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadEdit(IntPtr edit, out bool definitive)
        {
            definitive = false;
            if ((Native.GetWindowLong(edit, Native.GWL_STYLE) & Native.ES_PASSWORD) != 0)
            {
                definitive = true;
                return null;
            }

            IntPtr selection;
            if (Native.SendMessageTimeout(edit, Native.EM_GETSEL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out selection) == IntPtr.Zero)
                return null;
            int packed = selection.ToInt32();
            int start = packed & 0xFFFF, end = (packed >> 16) & 0xFFFF;

            IntPtr length;
            if (Native.SendMessageTimeout(edit, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out length) == IntPtr.Zero)
                return null;
            int total = length.ToInt32();
            if (total >= 0xFFFF) return null; // 超出 EM_GETSEL 16 位返回值的范围，交给下一层
            if (start == end)
            {
                definitive = true;
                return null;
            }

            var buffer = new StringBuilder(total + 1);
            IntPtr copied;
            if (Native.SendMessageTimeoutText(edit, Native.WM_GETTEXT, new IntPtr(total + 1), buffer, Native.SMTO_ABORTIFHUNG, MessageTimeoutMs, out copied) == IntPtr.Zero)
                return null;
            string all = buffer.ToString();
            if (end > all.Length || start > end) return null;
            definitive = true;
            return all.Substring(start, end - start);
        }

        private static string ReadUia(out bool isPassword)
        {
            isPassword = false;
            var task = Task.Run<string[]>(() =>
            {
                var element = AutomationElement.FocusedElement;
                if (element == null) return null;
                if ((bool)element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty))
                    return new[] { (string)null };
                object pattern;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return null;
                var parts = new List<string>();
                foreach (var range in ((TextPattern)pattern).GetSelection())
                    parts.Add(range.GetText(DictClient.MaxQueryLength));
                return new[] { string.Join("\n", parts) };
            });
            try
            {
                if (!task.Wait(UiaTimeoutMs) || task.Result == null) return null;
                if (task.Result[0] == null) isPassword = true;
                return task.Result[0];
            }
            catch (AggregateException e)
            {
                Log.Write("uia: " + e.InnerException.Message);
                return null;
            }
        }

        private string ReadViaClipboard(bool terminal)
        {
            var snapshot = ClipboardSnapshot.Take();
            if (snapshot == null) return null;

            WaitForModifiersReleased();
            uint before = Native.GetClipboardSequenceNumber();
            var copyStart = DateTimeOffset.Now.AddMilliseconds(-200);
            _monitor.SuppressInjected(300);
            SendCopy(terminal);

            if (!WaitForClipboardChange(before, CopyTimeoutMs))
            {
                WatchLateCopy(snapshot, before, copyStart);
                return null;
            }
            return TakeCopiedText(snapshot, copyStart);
        }

        // 浏览器冷启动时写剪贴板可能超过一秒。超时不代表没写，放任不管的话
        // 迟到的内容会永久覆盖用户的剪贴板，所以继续盯一段时间，到了就恢复。
        private void WatchLateCopy(ClipboardSnapshot snapshot, uint before, DateTimeOffset copyStart)
        {
            int activity = _monitor.UserActivity;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool changed = WaitForClipboardChange(before, LateCopyWindowMs);
                if (!changed || _monitor.UserActivity != activity)
                {
                    // 用户在此期间有操作时，变化可能是用户自己复制的，不能动。
                    snapshot.Discard();
                    return;
                }
                string text = TakeCopiedText(snapshot, copyStart);
                Log.Write("late copy len=" + (text ?? "").Length);
                var handler = LateSelection;
                if (!string.IsNullOrEmpty(text) && handler != null) handler(text);
            });
        }

        private static string TakeCopiedText(ClipboardSnapshot snapshot, DateTimeOffset copyStart)
        {
            string text = null;
            bool emptySelection = false;
            if (ClipboardSnapshot.Open())
            {
                try
                {
                    emptySelection = ClipboardSnapshot.IsVsCodeEmptySelection();
                    text = ClipboardSnapshot.ReadText();
                }
                finally { Native.CloseClipboard(); }
            }
            snapshot.Restore();
            ClipboardHistory.Forget(text, copyStart);
            return emptySelection ? null : text;
        }

        private static bool WaitForClipboardChange(uint before, int timeoutMs)
        {
            int start = Environment.TickCount;
            while (Environment.TickCount - start < timeoutMs)
            {
                uint current = Native.GetClipboardSequenceNumber();
                if (current != before)
                {
                    // 写入通常是“清空 + 多次设置”，等序列号稳定后再读。
                    while (true)
                    {
                        Thread.Sleep(ClipboardSettleMs);
                        uint next = Native.GetClipboardSequenceNumber();
                        if (next == current) return true;
                        current = next;
                    }
                }
                Thread.Sleep(5);
            }
            return false;
        }

        private static void WaitForModifiersReleased()
        {
            int start = Environment.TickCount;
            while (Environment.TickCount - start < 200)
            {
                if (!IsDown(Native.VK_CONTROL) && !IsDown(Native.VK_SHIFT) && !IsDown(Native.VK_MENU)
                    && !IsDown(Native.VK_LWIN) && !IsDown(Native.VK_RWIN))
                    return;
                Thread.Sleep(5);
            }
        }

        private static bool IsDown(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static void SendCopy(bool terminal)
        {
            ushort key = terminal ? Native.VK_INSERT : Native.VK_C;
            uint extended = terminal ? Native.KEYEVENTF_EXTENDEDKEY : 0;
            var inputs = new[]
            {
                Key(Native.VK_CONTROL, 0),
                Key(key, extended),
                Key(key, extended | Native.KEYEVENTF_KEYUP),
                Key(Native.VK_CONTROL, Native.KEYEVENTF_KEYUP),
            };
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static Native.INPUT Key(ushort vk, uint flags)
        {
            var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            input.u.ki.wVk = vk;
            input.u.ki.dwFlags = flags;
            return input;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            var name = new StringBuilder(256);
            Native.GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }

        private static string ProcessName(uint pid)
        {
            var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return "";
            try
            {
                var path = new StringBuilder(1024);
                uint size = (uint)path.Capacity;
                return Native.QueryFullProcessImageName(handle, 0, path, ref size)
                    ? Path.GetFileNameWithoutExtension(path.ToString())
                    : "";
            }
            finally { Native.CloseHandle(handle); }
        }
    }
}
