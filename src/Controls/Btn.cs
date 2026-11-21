// ============================================================================
//  Btn.cs — 按钮
//  主按钮 / 幽灵 / 危险 / 强调四种形态，含按下位移、悬停辉光与焦点环
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
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go
{
    public enum BtnKind { Primary, Ghost, Quiet, Danger, Accent }

    // ========================================================================
    //  Btn — 三层结构：底板（背景/描边/阴影） + 内容（图标/文字） + 焦点环
    //  按下时：背景加深一档、内容下移 1px、阴影收缩；抬起 90ms 内回弹。
    //  命中语义：Down 捕获鼠标，Up 仅当指针仍在按钮内才提交点击。
    // ========================================================================
    public class Btn : Border
    {
        private const uint InkPrimary = T.Root;

        private static Brush BgPrimary = T.AccentGrad();
        private static Brush BgPrimaryHover = T.AccentGradBright();
        private static SolidColorBrush BgPrimaryDown = T.S(T.AccentDim);
        private static readonly Brush BgGhost = T.Grad(0xFF202E47, 0xFF141E31, 90);
        private static readonly Brush BgGhostHover = T.Grad(0xFF283A58, 0xFF1A273F, 90);
        private static readonly SolidColorBrush BgGhostDown = T.S(T.Inset);
        private static readonly SolidColorBrush BgQuietHover = T.S(T.Hover);
        private static readonly SolidColorBrush BgQuietDown = T.S(T.Raised);
        private static Brush BgAccent = T.Grad(T.AccentA(0x22), T.Accent2A(0x10), 135);
        private static Brush BgAccentHover = T.Grad(T.AccentA(0x36), T.Accent2A(0x1E), 135);
        private static SolidColorBrush BgAccentDown = T.S(T.AccentGlow);
        private static SolidColorBrush LineAccent = CtlTint.Wash(T.Accent, 0x58);
        private static SolidColorBrush LineAccentHover = CtlTint.Wash(T.Accent, 0x9A);
        private static SolidColorBrush LineAccentDown = CtlTint.Wash(T.Accent, 0xCC);

        static Btn() { T.ThemeChanged += RefreshTheme; }

        // 主题切换时重建强调色画刷：静态捕获会在换主题后陈旧（Rev73 遗留缺陷）。
        private static void RefreshTheme()
        {
            BgPrimary = T.AccentGrad();
            BgPrimaryHover = T.AccentGradBright();
            BgPrimaryDown = T.S(T.AccentDim);
            BgAccent = T.Grad(T.AccentA(0x22), T.Accent2A(0x10), 135);
            BgAccentHover = T.Grad(T.AccentA(0x36), T.Accent2A(0x1E), 135);
            BgAccentDown = T.S(T.AccentGlow);
            LineAccent = CtlTint.Wash(T.Accent, 0x58);
            LineAccentHover = CtlTint.Wash(T.Accent, 0x9A);
            LineAccentDown = CtlTint.Wash(T.Accent, 0xCC);
        }
        private static readonly Brush BgDanger = T.Grad(0x28FF5C63, 0x12FF5C63, 90);
        private static readonly Brush BgDangerHover = T.Grad(0x3EFF5C63, 0x1EFF5C63, 90);
        private static readonly SolidColorBrush BgDangerDown = CtlTint.Wash(T.Bad, 0x38);
        private static readonly SolidColorBrush LineDanger = CtlTint.Wash(T.Bad, 0x55);
        private static readonly SolidColorBrush LineDangerHover = CtlTint.Wash(T.Bad, 0x96);
        private static readonly SolidColorBrush LineDangerDown = CtlTint.Wash(T.Bad, 0xCC);

        private BtnKind _kind;
        private bool _enabled = true;
        private TextBlock _text;
        private Path _icon;
        private Border _plate;
        private Border _sheen;
        private Border _contentHost;
        private Border _ring;
        private TranslateTransform _shift;
        private DispatcherTimer _pulse;
        private bool _hover;
        private bool _pressed;
        private bool _armed;
        private bool _spaceDown;
        private bool _themeHooked;

        public event EventHandler Click;
        public event EventHandler Hover;
        public event EventHandler Pressed;

        public Btn(string label, BtnKind kind)
        {
            _kind = kind;
            Height = T.BtnH;
            Padding = new Thickness(0);
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            CornerRadius = new CornerRadius(T.RadiusSm);
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Cursor = Cursors.Hand;
            Focusable = true;
            FocusVisualStyle = null;
            KeyboardNavigation.SetIsTabStop(this, true);
            Hit.Track();

            _plate = new Border();
            _plate.CornerRadius = new CornerRadius(T.RadiusSm);
            _plate.BorderThickness = new Thickness(1);
            _plate.SnapsToDevicePixels = true;
            _plate.IsHitTestVisible = false;

            // 顶部 1px 精密光泽线（Quiet 除外）：按压时收敛光泽，强化机加工物理深度。
            _sheen = new Border();
            _sheen.Height = 1;
            _sheen.VerticalAlignment = VerticalAlignment.Top;
            _sheen.HorizontalAlignment = HorizontalAlignment.Stretch;
            _sheen.Margin = new Thickness(3, 1, 3, 0);
            _sheen.Background = T.Bevel();
            _sheen.IsHitTestVisible = false;
            _sheen.SnapsToDevicePixels = true;
            _sheen.Visibility = _kind == BtnKind.Quiet ? Visibility.Collapsed : Visibility.Visible;

            // Rev100：全面脱离 DropShadowEffect 离屏 D3D 渲染纹理，纯矢量高光+描边+位移。

            _contentHost = new Border();
            _contentHost.Padding = new Thickness(14, 0, 14, 0);
            _contentHost.Margin = new Thickness(1);
            _contentHost.Background = Brushes.Transparent;
            _contentHost.SnapsToDevicePixels = true;
            _shift = new TranslateTransform(0, 0);
            _contentHost.RenderTransform = _shift;

            _text = new TextBlock();
            _text.Text = Ui.Spaced(label);
            _text.FontSize = T.FsSmall;
            _text.FontFamily = T.FDisplayReg;
            _text.VerticalAlignment = VerticalAlignment.Center;
            _text.HorizontalAlignment = HorizontalAlignment.Center;
            _text.IsHitTestVisible = false;
            _contentHost.Child = _text;

            _ring = new Border();
            _ring.Margin = new Thickness(1);
            _ring.CornerRadius = new CornerRadius(T.RadiusSm - 1);
            _ring.BorderThickness = new Thickness(1);
            _ring.BorderBrush = T.S(T.Accent);
            _ring.Visibility = Visibility.Collapsed;
            _ring.IsHitTestVisible = false;

            Grid root = new Grid();
            root.Background = Brushes.Transparent;
            root.Children.Add(_plate);
            root.Children.Add(_sheen);
            root.Children.Add(_contentHost);
            root.Children.Add(_ring);
            Child = root;

            Apply();
            Hook();
        }

        // ---- 公共 API ------------------------------------------------------

        public string Label
        {
            get { return _text.Text; }
            set { _text.Text = Ui.Spaced(value); }
        }

        /// <summary>紧凑形态：行内文字按钮用，收窄内容内边距（外层 Padding 只内缩底板，不改内容）。</summary>
        public bool Compact
        {
            set { _contentHost.Padding = value ? new Thickness(8, 0, 8, 0) : new Thickness(14, 0, 14, 0); }
        }

        public BtnKind Kind
        {
            get { return _kind; }
            set
            {
                if (_kind == value) return;
                _kind = value;
                Apply();
            }
        }

        public void SetIcon(string pathData, double size)
        {
            _contentHost.Child = null;
            _text.HorizontalAlignment = HorizontalAlignment.Left;

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;

            _icon = Ui.Icon(pathData, size, GetFg(), 1.4);
            _icon.Margin = new Thickness(0, 0, T.GapIcon, 0);
            _icon.IsHitTestVisible = false;
            sp.Children.Add(_icon);
            sp.Children.Add(_text);
            _contentHost.Child = sp;
            Apply();
        }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Cursor = value ? Cursors.Hand : Cursors.Arrow;
                Opacity = value ? 1.0 : T.DisabledOpacity;
                Focusable = value;
                KeyboardNavigation.SetIsTabStop(this, value);
                if (!value)
                {
                    ResetPress();
                    _spaceDown = false;
                }
                Apply();
                UpdateRing();
            }
        }

        // ---- 视觉 ----------------------------------------------------------

        private uint GetFg()
        {
            switch (_kind)
            {
                case BtnKind.Primary: return InkPrimary;
                case BtnKind.Accent: return T.Accent;
                case BtnKind.Danger: return T.Bad;
                case BtnKind.Quiet: return T.Dim;
                default: return T.Text;
            }
        }

        private void Apply()
        {
            Brush bg;
            SolidColorBrush line;

            switch (_kind)
            {
                case BtnKind.Primary:
                    bg = _pressed ? (Brush)BgPrimaryDown : (_hover ? BgPrimaryHover : BgPrimary);
                    line = T.S(0x44FFFFFF);
                    break;
                case BtnKind.Accent:
                    bg = _pressed ? (Brush)BgAccentDown : (_hover ? BgAccentHover : BgAccent);
                    line = _pressed ? LineAccentDown : (_hover ? LineAccentHover : LineAccent);
                    break;
                case BtnKind.Danger:
                    bg = _pressed ? (Brush)BgDangerDown : (_hover ? BgDangerHover : BgDanger);
                    line = _pressed ? LineDangerDown : (_hover ? LineDangerHover : LineDanger);
                    break;
                case BtnKind.Quiet:
                    bg = _pressed ? (Brush)BgQuietDown : (_hover ? BgQuietHover : Brushes.Transparent);
                    line = Brushes.Transparent;
                    break;
                default:
                    bg = _pressed ? (Brush)BgGhostDown : (_hover ? BgGhostHover : BgGhost);
                    line = _pressed ? T.S(T.LineHi) : (_hover ? T.S(T.LineHi) : T.S(T.LineMid));
                    break;
            }

            _plate.Background = bg;
            _plate.BorderBrush = line;

            if (_sheen != null && _kind != BtnKind.Quiet)
            {
                if (!_enabled) _sheen.Opacity = 0.25;
                else if (_pressed) _sheen.Opacity = 0.20;
                else if (_hover) _sheen.Opacity = 1.0;
                else _sheen.Opacity = _kind == BtnKind.Primary ? 1.0 : 0.80;
            }

            uint fg = GetFg();
            _text.Foreground = T.S(fg);
            _text.FontWeight = _kind == BtnKind.Primary ? FontWeights.SemiBold : FontWeights.Normal;
            if (_icon != null) _icon.Stroke = T.S(fg);
            _ring.BorderBrush = T.S(_kind == BtnKind.Primary ? T.Hi : T.Accent);
        }

        private void Shift(bool down)
        {
            double to = down ? 1 : 0;
            if (!Ui.Animate)
            {
                _shift.BeginAnimation(TranslateTransform.YProperty, null);
                _shift.Y = to;
                return;
            }
            Duration d = new Duration(TimeSpan.FromMilliseconds(down ? 60 : 90));
            DoubleAnimation a = new DoubleAnimation(to, d);
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _shift.BeginAnimation(TranslateTransform.YProperty, a);
        }

        private void Shadow(bool down)
        {
            // 投影表面已在 Rev90 移除；保留无开销空桩避免改坏状态机签名。
        }

        // ---- 点击状态机 ----------------------------------------------------

        private void SetPressed(bool value)
        {
            if (_pressed == value) return;
            bool wasPressed = _pressed;
            _pressed = value;
            Apply();
            Shift(value);
            Shadow(value);
            if (value && !wasPressed && Pressed != null) Pressed(this, EventArgs.Empty);
        }

        private bool Inside(Point p)
        {
            return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }

        private void ResetPress()
        {
            _armed = false;
            bool wasPressed = _pressed;
            _pressed = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (wasPressed)
            {
                Shift(false);
                Shadow(false);
                Apply();
            }
        }

        private void Fire()
        {
            if (!_enabled) return;
            if (Click != null) Click(this, EventArgs.Empty);
        }

        private void Pulse()
        {
            if (!_enabled || _armed) return;
            SetPressed(true);
            if (_pulse == null)
            {
                _pulse = new DispatcherTimer();
                _pulse.Interval = TimeSpan.FromMilliseconds(90);
                _pulse.Tick += delegate
                {
                    _pulse.Stop();
                    SetPressed(false);
                };
            }
            _pulse.Stop();
            _pulse.Start();
        }

        private void Hook()
        {
            MouseEnter += delegate
            {
                if (!_enabled) return;      // 禁用态（0.4 透明）不该给出"可点"的假信号
                _hover = true;
                if (_armed)
                {
                    if (Mouse.LeftButton == MouseButtonState.Pressed) SetPressed(true);
                    else ResetPress();
                }
                else Apply();
                if (Hover != null) Hover(this, EventArgs.Empty);
            };

            MouseLeave += delegate
            {
                _hover = false;
                if (_armed) SetPressed(false); // 保留捕获：拖回仍可确认
                else Apply();
            };

            MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (!_enabled) return;
                e.Handled = true;      // 按钮自己消化点击，外层容器不要再触发一次
                _armed = true;
                _hover = true;
                Focus();
                UpdateRing();
                SetPressed(true);
                CaptureMouse();
            };

            MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!_armed) return;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    ResetPress();
                    return;
                }
                bool inside = Inside(e.GetPosition(this));
                if (inside != _pressed) SetPressed(inside);
            };

            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                if (!_armed) return;
                e.Handled = true;
                bool inside = Inside(e.GetPosition(this));
                _armed = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                SetPressed(false);
                if (inside) Fire();
            };

            LostMouseCapture += delegate
            {
                if (_armed) ResetPress();
            };

            KeyDown += delegate (object s, KeyEventArgs e)
            {
                UpdateRing();
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
                    Pulse();
                    Fire();
                }
            };

            KeyUp += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Space)
                {
                    e.Handled = true;
                    if (!_spaceDown) return;
                    _spaceDown = false;
                    SetPressed(false);
                    Fire();
                }
            };

            IsKeyboardFocusedChanged += delegate
            {
                if (!IsKeyboardFocused)
                {
                    _spaceDown = false;
                    if (!_armed && _pressed) SetPressed(false);
                }
                UpdateRing();
            };

            PreviewKeyDown += delegate { UpdateRing(); };
            PreviewMouseDown += delegate { UpdateRing(); };

            Loaded += delegate
            {
                if (!_themeHooked) { _themeHooked = true; T.ThemeChanged += OnThemeChanged; }
                Apply();
            };
            Unloaded += delegate
            {
                if (_themeHooked) { _themeHooked = false; T.ThemeChanged -= OnThemeChanged; }
            };
        }

        private void OnThemeChanged() { Apply(); }

        private void UpdateRing()
        {
            bool show = _enabled && IsKeyboardFocused && Hit.LastWasKeyboard;
            _ring.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ========================================================================
    //  HoldBtn — 长按确认：按住 holdMs 才触发
    //  按下：边框进入武装态、进度条 0→100%、文字变为秒级倒计时；
    //  松开/移出：120ms 回弹；成功/取消均留下可读终态。
    // ========================================================================
}
