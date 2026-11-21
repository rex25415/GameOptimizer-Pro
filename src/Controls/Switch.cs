// ============================================================================
//  Switch.cs — 开关
//  34x18 轨道 + 缓动滑块，支持只读与键盘操作
//  来源：Ctl.cs（1440 行单体）按职责拆分，代码逐行迁移，行为零变化。
// ============================================================================
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go
{
    public class Switch : Grid
    {
        // Rev120 HYPERION：激活态升级为双色相发光实体胶囊 + 纯白光泽滑钮，未激活态为深空凹槽 + 金属拉丝钮。
        private static readonly SolidColorBrush TrackOff = T.S(0xFF0A111F);
        private static readonly SolidColorBrush TrackOffHover = T.S(0xFF142036);
        private static Brush TrackOn = T.AccentGrad();
        private static Brush TrackOnHover = T.AccentGradBright();
        private static readonly SolidColorBrush LineOff = T.S(0x42CFE6FF);
        private static readonly SolidColorBrush LineOffHover = T.S(0x6ECFE6FF);
        private static SolidColorBrush LineOn = CtlTint.Wash(T.Accent, 0xCC);
        private static SolidColorBrush LineOnHover = T.S(0xAAFFFFFF);
        private static readonly SolidColorBrush KnobOff = T.S(0xFF98AEC8);
        private static readonly SolidColorBrush KnobOffHover = T.S(0xFFD0E0F2);
        private static readonly SolidColorBrush KnobRimOff = T.S(0x34FFFFFF);
        private static readonly SolidColorBrush KnobRimOn = T.S(0x44000000);
        private static readonly SolidColorBrush KnobOn = T.S(0xFFFFFFFF);
        private static readonly SolidColorBrush KnobOnHover = T.S(0xFFFFFFFF);

        static Switch() { T.ThemeChanged += RefreshTheme; }

        private static void RefreshTheme()
        {
            TrackOn = T.AccentGrad();
            TrackOnHover = T.AccentGradBright();
            LineOn = CtlTint.Wash(T.Accent, 0xCC);
            LineOnHover = T.S(0xAAFFFFFF);
        }

        private Border _track;
        private Ellipse _knob;
        private Border _ring;
        private TranslateTransform _knobShift;
        private bool _on;
        private bool _readOnly;
        private bool _hover;
        private bool _pressed;
        private bool _armed;
        private bool _spaceDown;
        private bool _themeHooked;

        public event EventHandler Changed;

        /// <summary>
        /// 只读状态下被点击。
        /// 之前这里什么都不发生：Toggle() 直接 return，Changed 不触发，
        /// 用户点了没反应的开关，界面上也没有任何东西解释原因——
        /// 在图形页和网络页这就成了"点了没反应"的死按钮。控件负责把事件报出去，
        /// 由页面决定怎么说。
        /// </summary>
        public event EventHandler Blocked;

        public Switch(bool isOn)
        {
            Width = 44;
            Height = T.BtnH;
            HorizontalAlignment = HorizontalAlignment.Left;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Focusable = true;
            FocusVisualStyle = null;
            KeyboardNavigation.SetIsTabStop(this, true);
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Hit.Track();

            Grid visual = new Grid();
            visual.Width = 36;
            visual.Height = 20;
            visual.HorizontalAlignment = HorizontalAlignment.Right;
            visual.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(visual);

            _track = new Border();
            _track.CornerRadius = new CornerRadius(10);
            _track.BorderThickness = new Thickness(1);
            _track.IsHitTestVisible = false;
            visual.Children.Add(_track);

            _knob = new Ellipse();
            _knob.Width = 14;
            _knob.Height = 14;
            _knob.StrokeThickness = 1;
            _knob.HorizontalAlignment = HorizontalAlignment.Left;
            _knob.VerticalAlignment = VerticalAlignment.Center;
            _knob.IsHitTestVisible = false;
            _knobShift = new TranslateTransform(3, 0);
            _knob.RenderTransform = _knobShift;
            visual.Children.Add(_knob);

            _ring = new Border();
            _ring.Margin = new Thickness(1);
            _ring.CornerRadius = new CornerRadius(9);
            _ring.BorderThickness = new Thickness(1);
            _ring.BorderBrush = T.S(T.Accent);
            _ring.Visibility = Visibility.Collapsed;
            _ring.IsHitTestVisible = false;
            visual.Children.Add(_ring);

            _on = isOn;
            Paint();
            PlaceKnob(false);

            Hook();
        }

        public bool On
        {
            get { return _on; }
            set
            {
                if (_on == value) return;
                _on = value;
                Paint();
                PlaceKnob(true);
            }
        }

        public void SetReadOnly(bool ro)
        {
            _readOnly = ro;
            Cursor = ro ? Cursors.Arrow : Cursors.Hand;
            Focusable = !ro;
            KeyboardNavigation.SetIsTabStop(this, !ro);
            if (ro)
            {
                _armed = false;
                _pressed = false;
                _hover = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                Paint();
                PlaceKnob(false);
            }
            UpdateRing();
        }

        // ---- 视觉 ----------------------------------------------------------

        private void Paint()
        {
            bool lit = !_readOnly && (_hover || _pressed);
            if (_on)
            {
                _track.Background = lit ? TrackOnHover : TrackOn;
                _track.BorderBrush = lit ? LineOnHover : LineOn;
                _knob.Fill = lit ? KnobOnHover : KnobOn;
                _knob.Stroke = KnobRimOn;
            }
            else
            {
                _track.Background = lit ? TrackOffHover : TrackOff;
                _track.BorderBrush = lit ? LineOffHover : LineOff;
                _knob.Fill = lit ? KnobOffHover : KnobOff;
                _knob.Stroke = KnobRimOff;
            }
            _ring.BorderBrush = T.S(T.Accent);
        }

        private void PlaceKnob(bool animate)
        {
            double x = _on ? 19 : 3;
            if (_pressed) x += _on ? -1 : 1; // 按下时 knob 内缩 1px
            if (!animate || !Ui.Animate)
            {
                _knobShift.BeginAnimation(TranslateTransform.XProperty, null);
                _knobShift.X = x;
                return;
            }
            Duration d = new Duration(TimeSpan.FromMilliseconds(_pressed ? 60 : 130));
            DoubleAnimation a = new DoubleAnimation(x, d);
            a.EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut };
            _knobShift.BeginAnimation(TranslateTransform.XProperty, a);
        }

        private void SetPressed(bool value)
        {
            if (_pressed == value) return;
            _pressed = value;
            Paint();
            PlaceKnob(true);
        }

        private bool Inside(Point p)
        {
            return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }

        private void Toggle()
        {
            if (_readOnly) return;
            _on = !_on;
            Paint();
            PlaceKnob(true);
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        private void Hook()
        {
            MouseEnter += delegate
            {
                _hover = true;
                if (_armed)
                {
                    if (Mouse.LeftButton == MouseButtonState.Pressed) SetPressed(true);
                    else
                    {
                        _armed = false;
                        if (IsMouseCaptured) ReleaseMouseCapture();
                        SetPressed(false);
                    }
                }
                else if (!_readOnly) Paint();
            };

            MouseLeave += delegate
            {
                _hover = false;
                if (_armed) SetPressed(false);
                else Paint();
            };

            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                if (!_readOnly) return;
                if (!Inside(e.GetPosition(this))) return;
                e.Handled = true;
                if (Blocked != null) Blocked(this, EventArgs.Empty);
            };

            MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (_readOnly) return;
                e.Handled = true;      // 这是开关自己的点击，不要再让外层"整行可点"重复翻转
                _armed = true;
                Focus();
                UpdateRing();
                SetPressed(true);
                CaptureMouse();
            };

            MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!_armed) return;
                e.Handled = true;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    _armed = false;
                    if (IsMouseCaptured) ReleaseMouseCapture();
                    SetPressed(false);
                    return;
                }
                bool inside = Inside(e.GetPosition(this));
                SetPressed(inside);
            };

            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                if (!_armed) return;
                e.Handled = true;
                bool inside = Inside(e.GetPosition(this));
                _armed = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                SetPressed(false);
                if (inside) Toggle();
            };

            LostMouseCapture += delegate
            {
                _armed = false;
                SetPressed(false);
            };

            KeyDown += delegate (object s, KeyEventArgs e)
            {
                UpdateRing();
                if (_readOnly)
                {
                    if ((e.Key == Key.Enter || e.Key == Key.Space) && Blocked != null)
                    {
                        e.Handled = true;
                        Blocked(this, EventArgs.Empty);
                    }
                    return;
                }
                if (e.Key == Key.Space)
                {
                    if (e.IsRepeat) { e.Handled = true; return; }
                    _spaceDown = true;
                    e.Handled = true;
                    SetPressed(true);
                    return;
                }
                if (e.Key == Key.Enter)
                {
                    if (e.IsRepeat) { e.Handled = true; return; }
                    e.Handled = true;
                    SetPressed(true);
                    SetPressed(false);
                    Toggle();
                }
            };

            KeyUp += delegate (object s, KeyEventArgs e)
            {
                if (e.Key != Key.Space) return;
                e.Handled = true;
                if (!_spaceDown) return;
                _spaceDown = false;
                SetPressed(false);
                Toggle();
            };

            IsKeyboardFocusedChanged += delegate
            {
                if (!IsKeyboardFocused && _spaceDown) _spaceDown = false;
                UpdateRing();
            };
            PreviewKeyDown += delegate { UpdateRing(); };
            PreviewMouseDown += delegate { UpdateRing(); };

            Loaded += delegate
            {
                if (!_themeHooked) { _themeHooked = true; T.ThemeChanged += OnThemeChanged; }
                Paint();
            };
            Unloaded += delegate
            {
                if (_themeHooked) { _themeHooked = false; T.ThemeChanged -= OnThemeChanged; }
            };
        }

        private void OnThemeChanged() { Paint(); }

        private void UpdateRing()
        {
            bool show = !_readOnly && IsKeyboardFocused && Hit.LastWasKeyboard;
            _ring.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>状态徽章：极小字号 + 1px 描边 + 12% 底色。</summary>
}
