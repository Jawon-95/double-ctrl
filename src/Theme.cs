using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DoubleCtrl
{
    // 颜色放在 Application.Resources 里，界面都用 DynamicResource 引用，切换主题时无需重建窗口和菜单。
    internal static class Theme
    {
        public const string System = "system";
        public const string Light = "light";
        public const string Dark = "dark";

        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static readonly Dictionary<string, string> DarkColors = new Dictionary<string, string>
        {
            { "Surface", "#1C1C1E" }, { "Outline", "#38383A" }, { "Hover", "#2C2C2E" },
            { "Text", "#FFFFFF" }, { "Muted", "#8E8E93" }, { "Accent", "#0A84FF" },
            { "MenuPanel", "#F52C2C2E" }, { "MenuEdge", "#33FFFFFF" }, { "MenuHover", "#3F3F42" },
            { "MenuLine", "#48484A" }, { "MenuGap", "#1A1A1C" }, { "Destructive", "#FF453A" },
            { "SwitchOff", "#48484C" }, { "SegmentBack", "#1C1C1E" }, { "SegmentThumb", "#636366" },
        };

        private static readonly Dictionary<string, string> LightColors = new Dictionary<string, string>
        {
            { "Surface", "#FFFFFF" }, { "Outline", "#D1D1D6" }, { "Hover", "#E5E5EA" },
            { "Text", "#1C1C1E" }, { "Muted", "#6E6E73" }, { "Accent", "#007AFF" },
            { "MenuPanel", "#F7F9F9FB" }, { "MenuEdge", "#1F000000" }, { "MenuHover", "#E5E5EA" },
            { "MenuLine", "#D8D8DC" }, { "MenuGap", "#E9E9EE" }, { "Destructive", "#FF3B30" },
            { "SwitchOff", "#D9D9DE" }, { "SegmentBack", "#E3E3E8" }, { "SegmentThumb", "#FFFFFF" },
        };

        public static void Apply(string mode)
        {
            bool dark = mode == Dark || (mode != Light && SystemIsDark());
            var resources = Application.Current.Resources;
            foreach (var color in dark ? DarkColors : LightColors)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Value));
                brush.Freeze();
                resources[color.Key] = brush;
            }
            resources["ShadowOpacity"] = dark ? 0.45 : 0.2;
        }

        private static bool SystemIsDark()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
            {
                var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                return value is int && (int)value == 0;
            }
        }
    }
}
