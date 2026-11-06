using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DoubleCtrl
{
    // 保存剪贴板里的全部格式（而不只是文本），这样图片、文件列表也能原样恢复。
    internal sealed class ClipboardSnapshot
    {
        private const long MaxBytes = 256L * 1024 * 1024;

        private readonly List<KeyValuePair<uint, byte[]>> _globals = new List<KeyValuePair<uint, byte[]>>();
        private IntPtr _metafile;

        public static bool Open()
        {
            for (int i = 0; i < 20; i++)
            {
                if (Native.OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        public static ClipboardSnapshot Take()
        {
            if (!Open()) return null;
            var snapshot = new ClipboardSnapshot();
            try
            {
                long total = 0;
                uint format = 0;
                while ((format = Native.EnumClipboardFormats(format)) != 0)
                {
                    // 这些格式由系统从 DIB/增强图元合成，不需要也无法按内存块保存。
                    if (format == Native.CF_BITMAP || format == Native.CF_METAFILEPICT || format == Native.CF_PALETTE) continue;
                    var handle = Native.GetClipboardData(format);
                    if (handle == IntPtr.Zero) continue;
                    if (format == Native.CF_ENHMETAFILE)
                    {
                        snapshot._metafile = Native.CopyEnhMetaFile(handle, IntPtr.Zero);
                        continue;
                    }
                    var bytes = ReadGlobal(handle);
                    if (bytes == null || (total += bytes.Length) > MaxBytes) continue;
                    snapshot._globals.Add(new KeyValuePair<uint, byte[]>(format, bytes));
                }
            }
            finally { Native.CloseClipboard(); }
            return snapshot;
        }

        public void Restore()
        {
            if (!Open())
            {
                Log.Write("restore failed: clipboard busy");
                return;
            }
            try
            {
                Native.EmptyClipboard();
                foreach (var item in _globals)
                {
                    var handle = WriteGlobal(item.Value);
                    if (handle != IntPtr.Zero && Native.SetClipboardData(item.Key, handle) == IntPtr.Zero)
                        Native.GlobalFree(handle);
                }
                if (_metafile != IntPtr.Zero)
                {
                    if (Native.SetClipboardData(Native.CF_ENHMETAFILE, _metafile) == IntPtr.Zero)
                        Native.DeleteEnhMetaFile(_metafile);
                    _metafile = IntPtr.Zero;
                }
                // 恢复动作不应该在 Win+V 历史里再多出一条。
                foreach (var name in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                {
                    var flag = WriteGlobal(BitConverter.GetBytes(0));
                    if (flag != IntPtr.Zero) Native.SetClipboardData(Native.RegisterClipboardFormat(name), flag);
                }
            }
            finally { Native.CloseClipboard(); }
        }

        public void Discard()
        {
            if (_metafile != IntPtr.Zero)
            {
                Native.DeleteEnhMetaFile(_metafile);
                _metafile = IntPtr.Zero;
            }
        }

        // 调用前需要已打开剪贴板。
        public static string ReadText()
        {
            var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var ptr = Native.GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Native.GlobalUnlock(handle); }
        }

        // VS Code 在没有选区时 Ctrl+C 会复制整行，靠它写入的元数据区分。调用前需要已打开剪贴板。
        public static bool IsVsCodeEmptySelection()
        {
            uint format = Native.RegisterClipboardFormat("Chromium Web Custom MIME Data Format");
            var handle = Native.GetClipboardData(format);
            if (handle == IntPtr.Zero) return false;
            var bytes = ReadGlobal(handle);
            if (bytes == null) return false;
            return Encoding.Unicode.GetString(bytes).Replace(" ", "").Contains("\"isFromEmptySelection\":true");
        }

        private static byte[] ReadGlobal(IntPtr handle)
        {
            long size = (long)Native.GlobalSize(handle).ToUInt64();
            if (size <= 0 || size > MaxBytes) return null;
            var ptr = Native.GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { Native.GlobalUnlock(handle); }
        }

        private static IntPtr WriteGlobal(byte[] bytes)
        {
            var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, new UIntPtr((ulong)bytes.Length));
            if (handle == IntPtr.Zero) return handle;
            var ptr = Native.GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                Native.GlobalFree(handle);
                return IntPtr.Zero;
            }
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Native.GlobalUnlock(handle);
            return handle;
        }
    }
}
