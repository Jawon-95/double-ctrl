using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace DoubleCtrl
{
    // 用 Raw Input 而不是 WH_KEYBOARD_LL：低级钩子回调一旦超时会被系统静默摘除，
    // 表现就是“莫名其妙不工作”。Raw Input 只是收消息，不在系统按键路径上。
    internal sealed class InputMonitor
    {
        private const int MaxTapMs = 350;
        private const int MaxGapMs = 400;
        private const ushort MouseDownMask = 0x0001 | 0x0004 | 0x0010 | 0x0040 | 0x0100;
        private const ushort KeyBreak = 0x01;
        private const ushort KeyE1 = 0x04;

        public event Action DoubleCtrl;
        public event Action MouseDown;
        public event Action EscapePressed;

        private Native.WndProc _wndProc;
        private bool _ctrlDown, _armed, _second, _tapValid;
        private int _downTick, _upTick;
        private int _suppressUntil;
        private int _userActivity;

        // 每次用户按键或点击鼠标都会变化，用来判断等待期间用户有没有自己操作。
        public int UserActivity
        {
            get { return Volatile.Read(ref _userActivity); }
        }

        public void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "InputMonitor" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        // 取词时自己模拟的按键不能再被当作用户操作。
        public void SuppressInjected(int ms)
        {
            Volatile.Write(ref _suppressUntil, Environment.TickCount + ms);
        }

        private void Run()
        {
            _wndProc = OnMessage;
            var inst = Native.GetModuleHandle(null);
            var cls = new Native.WNDCLASS { lpfnWndProc = _wndProc, hInstance = inst, lpszClassName = "DoubleCtrl.Input" };
            Native.RegisterClass(ref cls);
            var hwnd = Native.CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, inst, IntPtr.Zero);
            var devices = new[]
            {
                new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd },
                new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd },
            };
            if (!Native.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                throw new Win32Exception();

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                Native.DispatchMessage(ref msg);
        }

        private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_INPUT)
                ReadRawInput(lParam);
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private void ReadRawInput(IntPtr handle)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
            uint size = 0;
            Native.GetRawInputData(handle, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(handle, Native.RID_INPUT, buffer, ref size, headerSize) != size) return;
                var header = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(Native.RAWINPUTHEADER));
                var body = new IntPtr(buffer.ToInt64() + headerSize);
                if (header.dwType == Native.RIM_TYPEKEYBOARD)
                {
                    ushort flags = (ushort)Marshal.ReadInt16(body, 2);
                    ushort vkey = (ushort)Marshal.ReadInt16(body, 6);
                    bool injected = header.hDevice == IntPtr.Zero;
                    if (injected && Environment.TickCount - Volatile.Read(ref _suppressUntil) < 0) return;
                    OnKey(vkey, flags);
                }
                else if (header.dwType == Native.RIM_TYPEMOUSE)
                {
                    ushort buttons = (ushort)Marshal.ReadInt16(body, 4);
                    if ((buttons & MouseDownMask) != 0) OnMouseDown();
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private void OnKey(ushort vkey, ushort flags)
        {
            bool up = (flags & KeyBreak) != 0;
            if (!up) Interlocked.Increment(ref _userActivity);
            // Pause 键会带一个伪造的 Ctrl（E1 前缀），不算真实按键。
            if (vkey == Native.VK_CONTROL && (flags & KeyE1) == 0)
            {
                if (up) OnCtrlUp(); else OnCtrlDown();
                return;
            }
            if (up) return;
            Reset();
            if (vkey == Native.VK_ESCAPE)
            {
                var handler = EscapePressed;
                if (handler != null) handler();
            }
        }

        private void OnCtrlDown()
        {
            if (_ctrlDown) return; // 长按时的自动重复
            _ctrlDown = true;
            int now = Environment.TickCount;
            _second = _armed && now - _upTick <= MaxGapMs;
            _armed = false;
            _tapValid = true;
            _downTick = now;
        }

        // 在第二次松开时才触发：此时物理 Ctrl 已抬起，后续模拟的组合键不会和它叠加。
        private void OnCtrlUp()
        {
            _ctrlDown = false;
            int now = Environment.TickCount;
            bool valid = _tapValid && now - _downTick <= MaxTapMs;
            bool fire = valid && _second;
            _tapValid = false;
            _second = false;
            _armed = valid && !fire;
            _upTick = now;
            if (fire)
            {
                var handler = DoubleCtrl;
                if (handler != null) handler();
            }
        }

        private void OnMouseDown()
        {
            Interlocked.Increment(ref _userActivity);
            Reset(); // Ctrl+单击多选不能被当成双击 Ctrl
            var handler = MouseDown;
            if (handler != null) handler();
        }

        private void Reset()
        {
            _armed = false;
            _second = false;
            _tapValid = false;
        }
    }
}
