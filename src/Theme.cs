using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DoubleCtrl
{
    // 切换主题时逐帧计算颜色，同时更新两份画刷：
    //   窗口：每帧换一组冻结画刷到 Application.Resources，界面用 DynamicResource 引用。
    //     不能对这些画刷做动画，模板和触发器用过之后 WPF 会把它们冻结，BeginAnimation 会抛异常。
    //   菜单：用 Get 拿到的可变画刷。Popup 不在窗口元素树里，收不到资源变更，只能直接改画刷。
    internal static class Theme
    {
        public const string System = "system";
        public const string Light = "light";
        public const string Dark = "dark";

        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const double FadeMs = 260;
        private const double DarkShadow = 0.45;
        private const double LightShadow = 0.2;

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

        private static readonly Dictionary<string, SolidColorBrush> Live = new Dictionary<string, SolidColorBrush>();
        private static readonly Stopwatch Clock = new Stopwatch();
        private static Dictionary<string, Color> _from, _to;
        private static double _shadow, _fromShadow, _toShadow;
        private static DispatcherTimer _timer;

        public static Brush Get(string key)
        {
            return Live[key];
        }

        public static void Apply(string mode)
        {
            bool dark = mode == Dark || (mode != Light && SystemIsDark());
            _to = (dark ? DarkColors : LightColors).ToDictionary(c => c.Key, c => (Color)ColorConverter.ConvertFromString(c.Value));
            _toShadow = dark ? DarkShadow : LightShadow;

            if (Live.Count == 0)
            {
                foreach (var color in _to) Live[color.Key] = new SolidColorBrush(color.Value);
                _shadow = _toShadow;
                Publish();
                return;
            }

            _from = Live.ToDictionary(b => b.Key, b => b.Value.Color);
            _fromShadow = _shadow;
            Clock.Restart();
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                _timer.Tick += delegate { Step(); };
            }
            _timer.Start();
        }

        private static void Step()
        {
            double t = Math.Min(1, Clock.ElapsedMilliseconds / FadeMs);
            double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            foreach (var brush in Live)
                brush.Value.Color = Mix(_from[brush.Key], _to[brush.Key], eased);
            _shadow = _fromShadow + (_toShadow - _fromShadow) * eased;
            Publish();
            if (t >= 1) _timer.Stop();
        }

        private static void Publish()
        {
            var frame = new ResourceDictionary();
            foreach (var brush in Live)
            {
                var frozen = new SolidColorBrush(brush.Value.Color);
                frozen.Freeze();
                frame[brush.Key] = frozen;
            }
            frame["ShadowOpacity"] = _shadow;

            // 换整个合并字典，每帧只触发一次资源刷新。
            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count == 0) merged.Add(frame);
            else merged[0] = frame;
        }

        private static Color Mix(Color from, Color to, double t)
        {
            return Color.FromArgb(Mix(from.A, to.A, t), Mix(from.R, to.R, t), Mix(from.G, to.G, t), Mix(from.B, to.B, t));
        }

        private static byte Mix(byte from, byte to, double t)
        {
            return (byte)Math.Round(from + (to - from) * t);
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
