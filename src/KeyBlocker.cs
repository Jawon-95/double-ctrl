using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DoubleCtrl
{
    // 写入浏览器类输入框时要把前台短暂交给原窗口，这几十毫秒里用户的按键会打进原输入框。
    // 期间用低级键盘钩子扣下真实按键，切回弹窗后按原顺序补发。钩子只在这段时间里装着，
    // 回调只做记录，不会因为超时被系统摘掉；即使被摘掉也只是退化成不拦截。
    internal sealed class KeyBlocker
    {
        private const int StartTimeoutMs = 200;
        private const int MaxBlockMs = 1500;

        private readonly List<Native.KBDLLHOOKSTRUCT> _keys = new List<Native.KBDLLHOOKSTRUCT>();
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private Native.HookProc _proc; // 回调委托必须一直被引用，否则会被回收
        private Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private int _startTick;

        private KeyBlocker() { }

        // 钩子装好才返回；装不上返回 null，调用方照常写入，只是不拦截按键。
        public static KeyBlocker Start()
        {
            var blocker = new KeyBlocker { _startTick = Environment.TickCount };
            blocker._thread = new Thread(blocker.Run) { IsBackground = true, Name = "KeyBlocker", Priority = ThreadPriority.Highest };
            blocker._thread.Start();
            if (blocker._ready.WaitOne(StartTimeoutMs) && blocker._hook != IntPtr.Zero) return blocker;
            Log.Write("key blocker: hook failed");
            blocker.Stop();
            return null;
        }

        private void Run()
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = OnKey;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            _ready.Set();
            if (_hook == IntPtr.Zero) return;
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                Native.DispatchMessage(ref msg);
            Native.UnhookWindowsHookEx(_hook);
        }

        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                // 自己模拟的按键要放行，只扣用户的真实按键。超时就全部放行，出了意外也不会锁死键盘。
                if ((info.flags & Native.LLKHF_INJECTED) == 0 && Environment.TickCount - _startTick < MaxBlockMs)
                {
                    lock (_keys) _keys.Add(info);
                    return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        // 卸下钩子，把期间扣下的按键补发给当前前台窗口。
        // summary 是给调试日志看的按键序列，比如 "50 49 ^50 ^49"，^ 表示抬起。
        public int StopAndReplay(out string summary)
        {
            Stop();
            Native.KBDLLHOOKSTRUCT[] keys;
            lock (_keys)
            {
                keys = _keys.ToArray();
                _keys.Clear();
            }
            summary = string.Join(" ", Array.ConvertAll(keys, k => ((k.flags & Native.LLKHF_UP) != 0 ? "^" : "") + k.vkCode.ToString("X")));
            if (keys.Length == 0) return 0;
            var inputs = new Native.INPUT[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                inputs[i] = SelectionReader.Key((ushort)keys[i].vkCode,
                    ((keys[i].flags & Native.LLKHF_EXTENDED) != 0 ? Native.KEYEVENTF_EXTENDEDKEY : 0)
                    | ((keys[i].flags & Native.LLKHF_UP) != 0 ? Native.KEYEVENTF_KEYUP : 0));
                inputs[i].u.ki.wScan = (ushort)keys[i].scanCode;
            }
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            return keys.Length;
        }

        private void Stop()
        {
            if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(StartTimeoutMs);
        }
    }
}
