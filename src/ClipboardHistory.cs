using System;
using System.Collections;
using System.Reflection;
using System.Threading;

namespace DoubleCtrl
{
    // 模拟复制时，目标程序写入的内容会进 Win+V 历史，这里把那一条删掉。
    // 用反射调用 WinRT：构建不需要 Windows SDK，老系统没有这个 API 时自动跳过。
    internal static class ClipboardHistory
    {
        private const int Attempts = 15;
        private const int RetryDelayMs = 150; // 历史服务是异步收录的，要等它出现
        private const int AsyncTimeoutMs = 1000;

        private static readonly Type ClipboardType = Type.GetType("Windows.ApplicationModel.DataTransfer.Clipboard, Windows, ContentType=WindowsRuntime");
        private static readonly Type AsyncInfoType = Type.GetType("Windows.Foundation.IAsyncInfo, Windows, ContentType=WindowsRuntime");

        public static void Forget(string text, DateTimeOffset since)
        {
            if (string.IsNullOrEmpty(text) || ClipboardType == null) return;
            // WinRT 的 Clipboard 只能在 STA 线程上调用。
            var thread = new Thread(() =>
            {
                try { Run(text, since); }
                catch (Exception e) { Log.Write("history: " + e); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static void Run(string text, DateTimeOffset since)
        {
            if (!(bool)ClipboardType.GetMethod("IsHistoryEnabled").Invoke(null, null)) return;
            for (int i = 0; i < Attempts; i++)
            {
                Thread.Sleep(RetryDelayMs);
                var item = Find(text, since);
                if (item == null) continue;
                var deleted = ClipboardType.GetMethod("DeleteItemFromHistory").Invoke(null, new[] { item });
                Log.Write("history item deleted=" + deleted);
                return;
            }
            Log.Write("history item not found");
        }

        // 只匹配取词开始之后产生、且文字完全相同的条目，不会碰用户自己的历史。
        internal static object Find(string text, DateTimeOffset since)
        {
            var getItems = ClipboardType.GetMethod("GetHistoryItemsAsync");
            var result = Await(getItems.Invoke(null, null), getItems.ReturnType);
            var resultType = getItems.ReturnType.GetGenericArguments()[0];
            var itemsProperty = resultType.GetProperty("Items");
            var items = itemsProperty.GetValue(result) as IEnumerable;
            if (items == null) return null;

            var itemType = itemsProperty.PropertyType.GetGenericArguments()[0];
            var timestamp = itemType.GetProperty("Timestamp");
            var content = itemType.GetProperty("Content");
            var getText = content.PropertyType.GetMethod("GetTextAsync", Type.EmptyTypes);
            var contains = content.PropertyType.GetMethod("Contains");

            foreach (var item in items)
            {
                if ((DateTimeOffset)timestamp.GetValue(item) < since) break; // 历史按时间倒序
                var view = content.GetValue(item);
                if (!(bool)contains.Invoke(view, new object[] { "Text" })) continue;
                if ((string)Await(getText.Invoke(view, null), getText.ReturnType) == text) return item;
            }
            return null;
        }

        private static object Await(object operation, Type operationType)
        {
            var status = AsyncInfoType.GetProperty("Status");
            int start = Environment.TickCount;
            while (status.GetValue(operation).ToString() == "Started")
            {
                if (Environment.TickCount - start > AsyncTimeoutMs) throw new TimeoutException();
                Thread.Sleep(5);
            }
            return operationType.GetMethod("GetResults").Invoke(operation, null);
        }
    }
}
