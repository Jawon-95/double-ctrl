using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DoubleCtrl
{
    // iOS 风格的菜单。用 Popup 自绘而不是 ContextMenu：要圆角分组、开关和弹出动画，
    // 而且托盘菜单打开时程序不在前台，点外部关闭由 PopupController 用全局鼠标事件处理。
    internal sealed class IosMenu
    {
        private const double ShadowRoom = 14;
        private const double RowHeight = 30;
        private const double Radius = 10;
        private const double FontSize = 12;
        private const double SwitchTravel = 12;
        private const double SegmentWidth = 34;
        private static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI, Segoe UI");
        private static readonly Brush SwitchOn = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));

        private readonly Popup _popup;
        private readonly Border _panel;
        private readonly StackPanel _rows = new StackPanel();
        private readonly List<Border> _items = new List<Border>();
        private readonly double _width;
        private IosMenu _child;
        private object _lastChildOwner;
        private int _childClosedTick;

        public event Action Closed;

        public IosMenu(double width)
        {
            _width = width;
            _panel = new Border
            {
                Width = width,
                Margin = new Thickness(ShadowRoom),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Radius),
                Child = _rows,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.3 },
                RenderTransform = new ScaleTransform(1, 1),
            };
            Paint(_panel, Border.BackgroundProperty, "MenuPanel");
            Paint(_panel, Border.BorderBrushProperty, "MenuEdge");
            TextOptions.SetTextFormattingMode(_panel, TextFormattingMode.Display);
            // 面板外再包一层：Popup 定位时不计入直接子元素的 Margin，阴影空间要由容器提供。
            _popup = new Popup { AllowsTransparency = true, StaysOpen = true, Child = new Grid { Children = { _panel } } };
            _popup.Closed += delegate
            {
                var handler = Closed;
                if (handler != null) handler();
            };
        }

        public void AddAction(string text, Action onClick, bool destructive = false)
        {
            AddRow(text, destructive ? "Destructive" : "Text", null, delegate
            {
                Close();
                onClick();
            });
        }

        // 单选项：对勾放在行尾，选中后关闭菜单。
        public void AddChoice(string text, bool selected, Action onClick)
        {
            AddRow(text, "Text", selected ? CheckMark() : null, delegate
            {
                Close();
                onClick();
            });
        }

        private static TextBlock CheckMark()
        {
            var mark = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Paint(mark, TextBlock.ForegroundProperty, "Accent");
            return mark;
        }

        // 开关项：切换后菜单保持打开，和 iOS 设置页一样可以连续操作。
        public void AddSwitch(string text, bool isOn, Action onToggle)
        {
            var off = new Border { CornerRadius = new CornerRadius(9) };
            Paint(off, Border.BackgroundProperty, "SwitchOff");
            var on = new Border { CornerRadius = new CornerRadius(9), Background = SwitchOn, Opacity = isOn ? 1 : 0 };
            var slide = new TranslateTransform(isOn ? SwitchTravel : 0, 0);
            var knob = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.White,
                Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = slide,
                Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.3 },
            };
            var view = new Grid { Width = 30, Height = 18, VerticalAlignment = VerticalAlignment.Center, Children = { off, on, knob } };

            bool state = isOn;
            AddRow(text, "Text", view, delegate
            {
                state = !state;
                var duration = TimeSpan.FromMilliseconds(170);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(state ? SwitchTravel : 0, duration) { EasingFunction = ease });
                on.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(state ? 1 : 0, duration));
                onToggle();
            });
        }

        // 分段控件：每段是（值，文字）。切换后菜单保持打开，能马上看到效果。
        public void AddSegments(string text, KeyValuePair<string, string>[] options, string selected, Action<string> onSelect)
        {
            var back = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            Paint(back, Border.BackgroundProperty, "SegmentBack");
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            int selectedIndex = options.ToList().FindIndex(o => o.Key == selected);
            var slide = new TranslateTransform(selectedIndex * SegmentWidth, 0);
            var thumb = new Border { Width = SegmentWidth, Height = 18, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = slide };
            Paint(thumb, Border.BackgroundProperty, "SegmentThumb");
            back.Child = new Grid { Children = { thumb, strip } };

            for (int i = 0; i < options.Length; i++)
            {
                var value = options[i].Key;
                double position = i * SegmentWidth;
                var label = new TextBlock { Text = options[i].Value, FontFamily = UiFont, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
                Paint(label, TextBlock.ForegroundProperty, "Text");
                var segment = new Border { Width = SegmentWidth, Height = 18, Background = Brushes.Transparent, Child = label, Cursor = Cursors.Hand };
                segment.MouseLeftButtonUp += delegate
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                    slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(position, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
                    onSelect(value);
                };
                strip.Children.Add(segment);
            }

            var row = AddRow(text, "Text", back, null);
            row.Cursor = Cursors.Arrow;
        }

        // 自定义内容的行：内容自己处理点击，行本身没有高亮。
        public void AddContent(FrameworkElement content)
        {
            var row = new Border { Height = RowHeight + 4, Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, Child = content };
            AddItem(row);
        }

        // 子菜单：贴着菜单里的某个元素弹出，父菜单保持打开。
        public void OpenChild(IosMenu child, FrameworkElement owner)
        {
            // 子菜单开着时再点同一个元素：全局鼠标事件已先把它关掉，这里不能又打开。
            if (owner == _lastChildOwner && Environment.TickCount - _childClosedTick < 300) return;
            CloseChild();
            _child = child;
            child.Closed += delegate
            {
                if (_child != child) return;
                _child = null;
                _lastChildOwner = owner;
                _childClosedTick = Environment.TickCount;
            };
            child.OpenBelow(owner, false);
        }

        // 全局鼠标按下：点在子菜单外先关子菜单。返回 true 表示点在菜单里，菜单不用关。
        public bool HandleMouseDown()
        {
            if (_child != null)
            {
                if (_child.ContainsCursor()) return true;
                CloseChild();
            }
            return ContainsCursor();
        }

        private void CloseChild()
        {
            if (_child != null) _child.Close();
        }

        public void AddGroupGap()
        {
            var gap = new Border { Height = 5 };
            Paint(gap, Border.BackgroundProperty, "MenuGap");
            _rows.Children.Add(gap);
        }

        public void OpenBelow(UIElement target, bool alignRight)
        {
            _popup.PlacementTarget = target;
            _popup.Placement = PlacementMode.Bottom;
            _popup.VerticalOffset = 2 - ShadowRoom;
            _popup.HorizontalOffset = alignRight ? ((FrameworkElement)target).ActualWidth - _width - ShadowRoom : -ShadowRoom;
            Open(alignRight ? 1 : 0, 0);
        }

        public void OpenAtMouse()
        {
            _popup.Placement = PlacementMode.Mouse;
            Open(0, 1);
        }

        public void Close()
        {
            CloseChild();
            _popup.IsOpen = false;
        }

        public bool ContainsCursor()
        {
            if (!_popup.IsOpen || PresentationSource.FromVisual(_panel) == null) return false;
            var topLeft = _panel.PointToScreen(new Point(0, 0));
            var bottomRight = _panel.PointToScreen(new Point(_panel.ActualWidth, _panel.ActualHeight));
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            return cursor.X >= topLeft.X && cursor.X < bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
        }

        private Border AddRow(string text, string foreground, FrameworkElement trailing, Action onClick)
        {
            var row = BuildRow(text, foreground, trailing, onClick);
            AddItem(row);
            return row;
        }

        private void AddItem(Border row)
        {
            if (_rows.Children.Count > 0 && _rows.Children[_rows.Children.Count - 1] is Border
                && _items.Contains((Border)_rows.Children[_rows.Children.Count - 1]))
                _rows.Children.Add(Separator());
            _items.Add(row);
            _rows.Children.Add(row);
        }

        private static Border Separator()
        {
            var line = new Border { Height = 1, Margin = new Thickness(12, 0, 0, 0) };
            Paint(line, Border.BackgroundProperty, "MenuLine");
            return line;
        }

        private static Border BuildRow(string text, string foreground, FrameworkElement trailing, Action onClick)
        {
            var content = new DockPanel { LastChildFill = true };
            if (trailing != null)
            {
                DockPanel.SetDock(trailing, Dock.Right);
                content.Children.Add(trailing);
            }
            var label = new TextBlock { Text = text, FontFamily = UiFont, FontSize = FontSize, VerticalAlignment = VerticalAlignment.Center };
            Paint(label, TextBlock.ForegroundProperty, foreground);
            content.Children.Add(label);

            var row = new Border { Height = RowHeight, Padding = new Thickness(12, 0, 10, 0), Background = Brushes.Transparent, Child = content, Cursor = Cursors.Hand };
            if (onClick != null)
            {
                row.MouseEnter += delegate { row.Background = Theme.Get("MenuHover"); };
                row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += delegate { onClick(); };
            }
            return row;
        }

        private static void Paint(DependencyObject target, DependencyProperty property, string key)
        {
            target.SetValue(property, Theme.Get(key));
        }

        private void Open(double originX, double originY)
        {
            // 行的高亮背景要贴合面板圆角。
            double inner = Radius - 1;
            foreach (var row in _items)
            {
                bool first = row == _rows.Children[0];
                bool last = row == _rows.Children[_rows.Children.Count - 1];
                row.CornerRadius = new CornerRadius(first ? inner : 0, first ? inner : 0, last ? inner : 0, last ? inner : 0);
            }

            _panel.RenderTransformOrigin = new Point(originX, originY);
            _popup.IsOpen = true;

            var scale = (ScaleTransform)_panel.RenderTransform;
            var grow = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
