using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DoubleCtrl
{
    internal static class Paths
    {
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoubleCtrl");
        public static readonly string Config = Path.Combine(Data, "config.json");
        public static readonly string DebugLog = Path.Combine(Data, "debug.log");
    }

    internal sealed class Settings
    {
        public bool StartHidden { get; set; }
        public string Le { get; set; }
        public string Theme { get; set; }
        public bool HintShown { get; set; }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(Paths.Config))
                {
                    var settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Paths.Config, Encoding.UTF8));
                    if (settings != null) return settings.Normalized();
                }
            }
            catch (Exception e) { Log.Write("settings: " + e.Message); }
            return new Settings().Normalized();
        }

        public void Save()
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Paths.Config, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
        }

        private Settings Normalized()
        {
            if (string.IsNullOrEmpty(Le)) Le = "en";
            if (string.IsNullOrEmpty(Theme)) Theme = DoubleCtrl.Theme.System;
            return this;
        }
    }

    internal static class AutoStart
    {
        public const string Argument = "--autostart";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DoubleCtrl";

        private static string Command
        {
            get { return "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" " + Argument; }
        }

        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(ValueName) != null;
            }
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
        }

        // exe 被移动后自启路径会失效，每次启动校正。
        public static void Refresh()
        {
            if (IsEnabled) Set(true);
        }
    }

    internal static class Log
    {
        public static bool Enabled;
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            if (!Enabled) return;
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Data);
                File.AppendAllText(Paths.DebugLog, DateTime.Now.ToString("HH:mm:ss.fff ") + message + Environment.NewLine, Encoding.UTF8);
            }
        }
    }
}
