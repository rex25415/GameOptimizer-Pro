// ============================================================================
//  NavRow.cs — 导轨导航行：悬停底色 / 选中指示条 / 折叠为纯图标
//
//  【Rev64】交互交给 Pressable：按下才有反馈、抬起且在行内才跳转、
//  拖出去松手自动取消；键盘 Enter/Space 等价于点中；↑↓ 在导轨里上下移动焦点。
//  同时补了键盘焦点环——只靠鼠标的桌面程序是不完整的。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Go
{
    /// <summary>导轨中的一行导航项（支持折叠为纯图标）。</summary>
    public class NavRow : Grid
    {
        private Border _bg;
        private Border _hover;
        private Border _bar;
        private TranslateTransform _barShift;
        private Border _ring;
        private System.Windows.Shapes.Path _icon;
        private TextBlock _label;
        private TextBlock _idx;
        private string _key;
        private bool _active;
        private Shell _shell;

        // 补间委托缓存成字段：Motion 约定调用方不得每次起补间就分配闭包。
        private readonly Action<double> _applyHover;
        private readonly Action<double> _applyBarOp;
        private readonly Action<double> _applyBarX;
        private readonly Action<double> _applyRing;

        public NavRow(Page p, Shell shell)
        {
            _shell = shell;
            _key = p.Key;
            Height = Touch.Scale(34);   // 触屏 / 掌机上放大到 42
            Margin = new Thickness(12, 1.5, 12, 1.5);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Focusable = true;
            SnapsToDevicePixels = true;

            _bg = new Border();
            _bg.CornerRadius = new CornerRadius(T.RadiusMd);
            _bg.Background = Brushes.Transparent;
            _bg.BorderThickness = new Thickness(1);
            _bg.BorderBrush = Brushes.Transparent;
            _bg.IsHitTestVisible = false;
            Children.Add(_bg);

            // 悬停底色独立一层，用 Opacity 补间淡入淡出（合成层，不触发布局）。
            _hover = new Border();
            _hover.CornerRadius = new CornerRadius(T.RadiusMd);
            _hover.Background = T.S(T.LineFaint);
            _hover.BorderThickness = new Thickness(1);
            _hover.BorderBrush = T.S(T.LineSoft);
            _hover.Opacity = 0;
            _hover.IsHitTestVisible = false;
            Children.Add(_hover);

            _ring = new Border();
            _ring.CornerRadius = new CornerRadius(T.RadiusMd);
            _ring.BorderThickness = new Thickness(1);
            _ring.BorderBrush = T.S(T.AccentRing);
            _ring.Opacity = 0;
            _ring.IsHitTestVisible = false;
            Children.Add(_ring);

            _bar = new Border();
            _bar.Width = 3.5;
            _bar.Height = 18;
            _bar.CornerRadius = new CornerRadius(1.75);
            _bar.Background = T.AccentGrad();
            _bar.HorizontalAlignment = HorizontalAlignment.Left;
            _bar.Margin = new Thickness(0, 0, 0, 0);
            _bar.VerticalAlignment = VerticalAlignment.Center;
            _bar.Opacity = 0;
            _barShift = new TranslateTransform(-4, 0);
            _bar.RenderTransform = _barShift;
            _bar.IsHitTestVisible = false;
            Children.Add(_bar);

            _icon = Ui.Icon(p.Ico, 16, T.Dim, 1.45);
            _icon.HorizontalAlignment = HorizontalAlignment.Left;
            _icon.Margin = new Thickness(13, 0, 0, 0);
            _icon.IsHitTestVisible = false;
            Children.Add(_icon);

            _label = new TextBlock();
            _label.Text = p.Title;
            _label.FontSize = T.FsBody;
            _label.FontFamily = T.FCn;
            _label.Foreground = T.S(T.Text);
            _label.VerticalAlignment = VerticalAlignment.Center;
            _label.Margin = new Thickness(40, 0, 0, 0);
            _label.IsHitTestVisible = false;
            Children.Add(_label);

            _idx = Ui.Mono(p.Idx, T.FsNano, T.Mute);
            _idx.HorizontalAlignment = HorizontalAlignment.Right;
            _idx.Margin = new Thickness(0, 0, 12, 0);
            _idx.VerticalAlignment = VerticalAlignment.Center;
            _idx.IsHitTestVisible = false;
            Children.Add(_idx);

            Tip.Attach(this, p.Title + "  ·  " + p.En);

            Pressable.On(this,
                delegate { if (_shell != null) _shell.Go(_key); },
                delegate (bool down)
                {
                    // 按下必须和"已选中"区分开：选中是 shadow wash，按下是更亮的光晕
                    if (down) { _bg.Background = T.S(T.AccentGlow); return; }
                    Paint(false);
                });

            MouseEnter += delegate
            {
                if (!_active && !IsMouseCaptured) Motion.Tween(_hover.Opacity, 1, 120, _applyHover);
            };
            MouseLeave += delegate
            {
                if (!_active && !IsMouseCaptured) Motion.Tween(_hover.Opacity, 0, 140, _applyHover);
            };

            IsKeyboardFocusedChanged += delegate { RefreshRing(); };
            KeyDown += OnKeyDown;

            _applyHover = delegate(double v) { _hover.Opacity = v; };
            _applyBarOp = delegate(double v) { _bar.Opacity = v; };
            _applyBarX = delegate(double v) { _barShift.X = v; };
            _applyRing = delegate(double v) { _ring.Opacity = v; };
        }

        private void Paint(bool pressed)
        {
            if (_active)
            {
                _bg.Background = T.AccentWashGrad();
                _bg.BorderBrush = T.S(T.AccentA(0x58));
                return;
            }
            _bg.BorderBrush = Brushes.Transparent;
            if (pressed) { _bg.Background = T.S(T.AccentWash); return; }
            _bg.Background = Brushes.Transparent;
        }

        private void RefreshRing()
        {
            bool on = IsKeyboardFocused && Hit.LastWasKeyboard;
            Motion.Tween(_ring.Opacity, on ? 1 : 0, 120, _applyRing);
        }

        private void OnKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                if (e.IsRepeat) { e.Handled = true; return; }
                e.Handled = true;
                if (_shell != null) _shell.Go(_key);
                return;
            }
            if (e.Key == Key.Up) { e.Handled = true; if (_shell != null) _shell.MoveRailFocus(this, -1); return; }
            if (e.Key == Key.Down) { e.Handled = true; if (_shell != null) _shell.MoveRailFocus(this, 1); return; }
        }

        public void SetCompact(bool compact)
        {
            _label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _idx.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _icon.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            _icon.Margin = compact ? new Thickness(0) : new Thickness(13, 0, 0, 0);
        }

        public void SetActive(bool on)
        {
            _active = on;
            if (on)
            {
                _bg.Background = T.AccentWashGrad();
                _bg.BorderBrush = T.S(T.AccentA(0x58));
                _hover.Opacity = 0;
                _bar.Visibility = Visibility.Visible;
                Motion.Tween(_bar.Opacity, 1, 140, _applyBarOp);
                Motion.Tween(_barShift.X, 0, 160, _applyBarX);
                _icon.Stroke = T.S(T.Accent);
                _label.Foreground = T.S(T.Hi);
                _label.FontWeight = FontWeights.SemiBold;
                _idx.Foreground = T.S(T.Accent);
            }
            else
            {
                _bg.Background = Brushes.Transparent;
                _bg.BorderBrush = Brushes.Transparent;
                Motion.Tween(_bar.Opacity, 0, 120, _applyBarOp);
                _barShift.X = -4;
                _hover.Opacity = IsMouseOver ? 1 : 0;
                _icon.Stroke = T.S(T.Dim);
                _label.Foreground = T.S(T.Text);
                _label.FontWeight = FontWeights.Normal;
                _idx.Foreground = T.S(T.Mute);
            }
        }
    }
}
