// ============================================================================
//  HoldBtn.cs — 长按按钮
//  按住 900ms 完成的安全动作，环形进度 + 可中断
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
    public class HoldBtn : Grid
    {
        private static readonly SolidColorBrush FrameBase = CtlTint.Wash(T.Bad, 0x0C);
        private static readonly SolidColorBrush FrameHover = CtlTint.Wash(T.Bad, 0x16);
        private static readonly SolidColorBrush FrameHold = CtlTint.Wash(T.Bad, 0x24);
        private static readonly SolidColorBrush LineBase = CtlTint.Wash(T.Bad, 0x40);
        private static readonly SolidColorBrush LineHover = CtlTint.Wash(T.Bad, 0x62);
        private static readonly SolidColorBrush LineHold = CtlTint.Wash(T.Bad, 0x9C);
        private static readonly SolidColorBrush LineOk = CtlTint.Wash(T.Ok, 0x99);
        private static readonly SolidColorBrush FrameOk = CtlTint.Wash(T.Ok, 0x18);
        private static readonly SolidColorBrush FillOk = CtlTint.Wash(T.Ok, 0x30);

        private Border _frame;
        private Border _fill;
        private ScaleTransform _fillScale;
        private Border _barFill;
        private ScaleTransform _barScale;
        private Border _edge;
        private TranslateTransform _edgeShift;
        private Border _ring;
        private TextBlock _text;
        private TextBlock _measure;
        private DispatcherTimer _timer;
        private DispatcherTimer _reset;
        private Stopwatch _watch;
        private readonly int _holdMs;
        private readonly string _label;
        private readonly uint _tint;
        private readonly SolidColorBrush _barBrush;
        private bool _armed;
        private bool _firing;
        private bool _hover;
        private bool _keyArm;
        private bool _themeHooked;
        private int _lastTenth = -1;

        public event EventHandler Confirmed;

        public HoldBtn(string label, int holdMs, uint tint)
        {
            _label = label;
            _holdMs = Math.Max(160, holdMs);
            _tint = tint;
            uint solidTint = (tint & 0x00FFFFFFu) | 0xE6000000u;
            _barBrush = CtlTint.Mix(solidTint == 0xE6000000u ? T.Bad : solidTint, 0xFFFFFFFF, 0.16);
            Height = T.BtnH;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Focusable = true;
            FocusVisualStyle = null;
            KeyboardNavigation.SetIsTabStop(this, true);
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Hit.Track();

            _frame = new Border();
            _frame.CornerRadius = new CornerRadius(T.RadiusSm);
            _frame.BorderThickness = new Thickness(1);
            _frame.BorderBrush = LineBase;
            _frame.Background = FrameBase;
            _frame.IsHitTestVisible = false;
            Children.Add(_frame);

            _fill = new Border();
            _fill.CornerRadius = new CornerRadius(T.RadiusSm - 1);
            _fill.Background = T.S(tint);
            _fill.HorizontalAlignment = HorizontalAlignment.Stretch;
            _fill.Margin = new Thickness(1);
            _fill.IsHitTestVisible = false;
            // 用 ScaleX 推进进度：宽度始终撑满，进度变化不再改变控件尺寸。
            _fillScale = new ScaleTransform(0, 1);
            _fill.RenderTransform = _fillScale;
            _fill.RenderTransformOrigin = new Point(0, 0.5);
            Children.Add(_fill);

            // 底部 2px 高对比进度导轨 + 前缘高光游标，确保低透明填充色下仍可精确读出进度。
            _barFill = new Border();
            _barFill.Height = 2;
            _barFill.VerticalAlignment = VerticalAlignment.Bottom;
            _barFill.HorizontalAlignment = HorizontalAlignment.Stretch;
            _barFill.Margin = new Thickness(1, 0, 1, 1);
            _barFill.CornerRadius = new CornerRadius(0, 0, T.RadiusSm - 1, T.RadiusSm - 1);
            _barFill.Background = _barBrush;
            _barFill.IsHitTestVisible = false;
            _barScale = new ScaleTransform(0, 1);
            _barFill.RenderTransform = _barScale;
            _barFill.RenderTransformOrigin = new Point(0, 0.5);
            Children.Add(_barFill);

            _edge = new Border();
            _edge.Width = 2;
            _edge.VerticalAlignment = VerticalAlignment.Stretch;
            _edge.HorizontalAlignment = HorizontalAlignment.Left;
            _edge.Margin = new Thickness(1, 2, 0, 2);
            _edge.CornerRadius = new CornerRadius(1);
            _edge.Background = T.S(0xCCFFFFFF);
            _edge.Visibility = Visibility.Collapsed;
            _edge.IsHitTestVisible = false;
            _edgeShift = new TranslateTransform(0, 0);
            _edge.RenderTransform = _edgeShift;
            Children.Add(_edge);

            _text = new TextBlock();
            _text.Text = Ui.Spaced(label);
            _text.FontSize = T.FsSmall;
            _text.FontFamily = T.FDisplayReg;
            _text.Foreground = T.S(T.Bad);
            _text.HorizontalAlignment = HorizontalAlignment.Center;
            _text.VerticalAlignment = VerticalAlignment.Center;
            _text.Margin = new Thickness(14, 0, 14, 0);
            _text.IsHitTestVisible = false;
            Children.Add(_text);

            // 不可见的等宽占位：长按时文字变成倒计时，宽度保持不变。
            _measure = new TextBlock();
            _measure.Text = Ui.Spaced(label);
            _measure.FontSize = T.FsSmall;
            _measure.FontFamily = T.FDisplayReg;
            _measure.HorizontalAlignment = HorizontalAlignment.Center;
            _measure.VerticalAlignment = VerticalAlignment.Center;
            _measure.Margin = new Thickness(14, 0, 14, 0);
            _measure.Visibility = Visibility.Hidden;
            _measure.IsHitTestVisible = false;
            Children.Add(_measure);

            _ring = new Border();
            _ring.Margin = new Thickness(1);
            _ring.CornerRadius = new CornerRadius(T.RadiusSm - 1);
            _ring.BorderThickness = new Thickness(1);
            _ring.BorderBrush = T.S(T.Accent);
            _ring.Visibility = Visibility.Collapsed;
            _ring.IsHitTestVisible = false;
            Children.Add(_ring);

            _watch = new Stopwatch();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += OnTick;

            _reset = new DispatcherTimer();
            _reset.Interval = TimeSpan.FromMilliseconds(520);
            _reset.Tick += OnReset;

            Hook();
            PaintFrame();

            // 控件被移出视觉树（换主题重建页面）时必须停表复位：
            // 否则计时器还活着，长按会在用户看不见的地方"完成"，Confirmed 照常触发。
            Unloaded += delegate
            {
                try { if (_timer != null) _timer.Stop(); } catch { /* 定时器已停：Stop 幂等 */ }
                try { if (_reset != null) _reset.Stop(); } catch { /* 同上 */ }
                _armed = false;
                _keyArm = false;
                if (_watch != null) _watch.Reset();
            };
        }

        public string Label
        {
            get { return _label; }
        }

        // ---- 状态 ----------------------------------------------------------

        private void Hook()
        {
            MouseEnter += delegate
            {
                _hover = true;
                if (!_armed && !_firing) PaintFrame();
            };

            MouseLeave += delegate
            {
                _hover = false;
                if (_armed) Cancel();
                else if (!_firing) PaintFrame();
            };

            MouseLeftButtonDown += delegate
            {
                Focus();
                _keyArm = false;
                StartHold();
                if (_armed) CaptureMouse();
            };

            MouseLeftButtonUp += delegate
            {
                if (_armed) Cancel();
            };

            LostMouseCapture += delegate
            {
                if (_armed) Cancel();
            };

            KeyDown += delegate (object s, KeyEventArgs e)
            {
                UpdateRing();
                if (e.Key != Key.Space) return;
                if (e.IsRepeat) { e.Handled = true; return; }
                e.Handled = true;
                _keyArm = true;
                StartHold();
            };

            KeyUp += delegate (object s, KeyEventArgs e)
            {
                if (e.Key != Key.Space) return;
                e.Handled = true;
                _keyArm = false;
                if (_armed) Cancel();
            };

            LostKeyboardFocus += delegate
            {
                if (_keyArm && _armed) Cancel();
                _keyArm = false;
                UpdateRing();
            };

            IsKeyboardFocusedChanged += delegate { UpdateRing(); };
            PreviewKeyDown += delegate { UpdateRing(); };
            PreviewMouseDown += delegate { UpdateRing(); };

            Loaded += delegate
            {
                if (!_themeHooked) { _themeHooked = true; T.ThemeChanged += OnThemeChanged; }
                OnThemeChanged();
            };
            Unloaded += delegate
            {
                if (_themeHooked) { _themeHooked = false; T.ThemeChanged -= OnThemeChanged; }
            };
        }

        private void OnThemeChanged()
        {
            _ring.BorderBrush = T.S(T.Accent);
        }

        private void StartHold()
        {
            if (_armed || _firing) return;
            _reset.Stop();
            _armed = true;
            _lastTenth = -1;
            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _fillScale.ScaleX = 0;
            _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _barScale.ScaleX = 0;
            _fill.Background = T.S(_tint);
            _barFill.Background = _barBrush;
            _edgeShift.X = 0;
            _edge.Visibility = Visibility.Collapsed;
            _watch.Reset();
            _watch.Start();
            _text.Text = Ui.Spaced((_holdMs / 1000.0).ToString("0.0") + "s");
            _text.Foreground = T.S(T.Hi);
            PaintFrame();
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_armed)
            {
                _timer.Stop();
                return;
            }

            double t = _watch.ElapsedMilliseconds / (double)_holdMs;
            if (t >= 1.0)
            {
                Complete();
                return;
            }

            _fillScale.ScaleX = t;
            _barScale.ScaleX = t;
            double travel = Math.Max(0, ActualWidth - 4);
            _edgeShift.X = travel * t;
            _edge.Visibility = t > 0.015 ? Visibility.Visible : Visibility.Collapsed;

            int tenth = (int)Math.Ceiling((1.0 - t) * _holdMs / 100.0);
            if (tenth <= 0) tenth = 0;
            if (tenth != _lastTenth)
            {
                _lastTenth = tenth;
                _text.Text = Ui.Spaced((tenth / 10.0).ToString("0.0") + "s");
            }
        }

        private void Complete()
        {
            _timer.Stop();
            _watch.Stop();
            _armed = false;
            _keyArm = false;
            _firing = true;

            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _fillScale.ScaleX = 1;
            _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _barScale.ScaleX = 1;
            _edge.Visibility = Visibility.Collapsed;
            _fill.Background = FillOk;
            _barFill.Background = T.S(T.Ok);
            _text.Text = Ui.Spaced("已确认");
            _text.Foreground = T.S(T.Ok);
            _frame.Background = FrameOk;
            _frame.BorderBrush = LineOk;

            if (IsMouseCaptured) ReleaseMouseCapture();
            if (Confirmed != null) Confirmed(this, EventArgs.Empty);

            _reset.Interval = TimeSpan.FromMilliseconds(900);
            _reset.Start();
        }

        private void Cancel()
        {
            if (!_armed) return;
            _armed = false;
            _keyArm = false;
            _timer.Stop();
            _watch.Stop();
            long held = _watch.ElapsedMilliseconds;

            if (IsMouseCaptured) ReleaseMouseCapture();

            double from = Math.Max(0, _fillScale.ScaleX);
            _fill.Background = T.S(_tint);
            _barFill.Background = _barBrush;
            _edge.Visibility = Visibility.Collapsed;
            if (Ui.Animate)
            {
                _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _fillScale.ScaleX = from;
                _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _barScale.ScaleX = from;
                DoubleAnimation a = new DoubleAnimation(from, 0, new Duration(TimeSpan.FromMilliseconds(120)));
                a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                a.Completed += delegate
                {
                    _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    _fillScale.ScaleX = 0;
                    _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    _barScale.ScaleX = 0;
                };
                _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            }
            else
            {
                _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _fillScale.ScaleX = 0;
                _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _barScale.ScaleX = 0;
            }

            _frame.Background = FrameBase;
            _frame.BorderBrush = LineBase;

            if (held >= 160)
            {
                _text.Text = Ui.Spaced("已取消");
                _text.Foreground = T.S(T.Dim);
                _reset.Interval = TimeSpan.FromMilliseconds(520);
                _reset.Start();
            }
            else
            {
                _text.Text = Ui.Spaced(_label);
                _text.Foreground = T.S(T.Bad);
            }

            if (!_firing) PaintFrame();
        }

        private void OnReset(object sender, EventArgs e)
        {
            _reset.Stop();
            _firing = false;
            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _fillScale.ScaleX = 0;
            _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _barScale.ScaleX = 0;
            _edge.Visibility = Visibility.Collapsed;
            _fill.Background = T.S(_tint);
            _barFill.Background = _barBrush;
            _text.Text = Ui.Spaced(_label);
            _text.Foreground = T.S(T.Bad);
            PaintFrame();
        }

        private void PaintFrame()
        {
            if (_firing)
            {
                _frame.Background = FrameOk;
                _frame.BorderBrush = LineOk;
                return;
            }
            if (_armed)
            {
                _frame.Background = FrameHold;
                _frame.BorderBrush = LineHold;
                return;
            }
            _frame.Background = _hover ? FrameHover : FrameBase;
            _frame.BorderBrush = _hover ? LineHover : LineBase;
        }

        private void UpdateRing()
        {
            bool show = IsKeyboardFocused && Hit.LastWasKeyboard;
            _ring.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ========================================================================
    //  Switch — 视觉 36×20，命中外框 44×30（透明底罩，视觉不变）
    //  支持 hover / pressed / 键盘 Space·Enter / 焦点环。
    // ========================================================================
}
