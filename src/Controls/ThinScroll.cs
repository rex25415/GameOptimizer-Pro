// ============================================================================
//  ThinScroll.cs — 细滚动容器
//  隐藏系统滚动条、自带 3px 滑块的滚动视图
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
    public class ThinScroll : Grid
    {
        public readonly ScrollViewer View;
        private Border _thumb;
        private Border _track;
        private bool _dragging;
        private bool _hot;
        private bool _hasScroll;
        private double _dragStart;
        private double _dragOffset;
        private double _thumbTop;
        private TranslateTransform _bump;
        private FrameworkElement _bumpTarget;
        private DateTime _bumpStamp = DateTime.MinValue;

        public ThinScroll(UIElement content)
        {
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;

            View = new ScrollViewer();
            View.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            View.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            View.CanContentScroll = false;
            View.Focusable = false;
            View.PanningMode = PanningMode.VerticalOnly;
            View.Content = content;
            Children.Add(View);

            // 键盘可达：点击空白处把焦点交给容器，方向键/翻页键即可滚动；
            // 不进 Tab 序，避免给每个页面多加一个停靠点。
            Focusable = true;
            KeyboardNavigation.SetIsTabStop(this, false);
            MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (!e.Handled) Focus();
            };
            PreviewKeyDown += OnPreviewKeyDown;

            _track = new Border();
            _track.Width = 8;
            _track.HorizontalAlignment = HorizontalAlignment.Right;
            _track.Margin = new Thickness(0, 2, 0, 2);
            _track.CornerRadius = new CornerRadius(T.RadiusSm);
            _track.Background = Brushes.Transparent;
            _track.IsHitTestVisible = false; // 默认不吃右边缘点击
            Children.Add(_track);

            _thumb = new Border();
            _thumb.Width = 6;
            _thumb.CornerRadius = new CornerRadius(3);
            _thumb.Background = T.S(T.LineHi);
            _thumb.HorizontalAlignment = HorizontalAlignment.Right;
            // 必须显式 Top：只给 Margin 而让它 Stretch 时，WPF 会把"剩余空间"当成居中
            // 来分配，于是滑块的**真实位置 = margin + (轨道高 - 滑块高) / 2 的余量**，
            // 并不是 margin 本身 —— 顶部时它出现在轨道 1/3 处，往下滚时又追不上内容，
            // 到底才刚好对齐。内容越长偏得越厉害（短页几乎看不出来，所以这个 bug 一直没被发现）。
            _thumb.VerticalAlignment = VerticalAlignment.Top;
            _thumb.Margin = new Thickness(0, 2, 1, 2);
            _thumb.Cursor = Cursors.SizeNS;
            Children.Add(_thumb);

            View.ScrollChanged += delegate { LayoutThumb(); };
            SizeChanged += delegate { LayoutThumb(); };

            _track.MouseLeftButtonDown += OnTrackDown;
            _thumb.MouseLeftButtonDown += OnThumbDown;
            _thumb.MouseMove += OnThumbMove;
            _thumb.MouseLeftButtonUp += OnThumbUp;
            _thumb.LostMouseCapture += delegate
            {
                _dragging = false;
                PaintHover();
            };
            _thumb.MouseEnter += delegate { _thumb.Background = _dragging ? T.S(T.Accent) : CtlTint.Mix(T.Accent, 0xFFFFFFFF, 0.35); };
            _thumb.MouseLeave += delegate { PaintHover(); };

            MouseMove += OnMouseMove;
            MouseLeave += delegate
            {
                if (_dragging) return;
                _hot = false;
                _track.IsHitTestVisible = false;
                _track.Background = Brushes.Transparent;
                PaintHover();
            };

            View.PreviewMouseWheel += OnWheel;

            bool themeHooked = false;
            Loaded += delegate
            {
                if (!themeHooked) { themeHooked = true; T.ThemeChanged += PaintHover; }
                PaintHover();
            };
            Unloaded += delegate
            {
                if (themeHooked) { themeHooked = false; T.ThemeChanged -= PaintHover; }
            };
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging) return;
            Point p = e.GetPosition(this);
            bool hot = _hasScroll && p.X >= ActualWidth - 10;
            if (hot == _hot) return;
            _hot = hot;
            _track.IsHitTestVisible = hot;
            _track.Background = hot ? T.S(0x10FFFFFF) : Brushes.Transparent;
            PaintHover();
        }

        private void PaintHover()
        {
            if (_dragging) _thumb.Background = T.S(T.Accent);
            else if (_hot) _thumb.Background = CtlTint.Wash(T.Accent, 0xAA);
            else _thumb.Background = T.S(T.LineHi);
        }

        private void OnTrackDown(object sender, MouseButtonEventArgs e)
        {
            if (!_hasScroll) return;
            double y = e.GetPosition(this).Y;
            double page = Math.Max(24, View.ViewportHeight * 0.9);
            if (y < _thumbTop + Math.Max(1, _thumb.Height) * 0.5)
                View.ScrollToVerticalOffset(View.VerticalOffset - page);
            else
                View.ScrollToVerticalOffset(View.VerticalOffset + page);
            e.Handled = true;
        }

        private void OnThumbDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragStart = e.GetPosition(this).Y;
            _dragOffset = View.VerticalOffset;
            _thumb.CaptureMouse();
            _thumb.Background = T.S(T.Accent);
            e.Handled = true;
        }

        private void OnThumbMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            double dy = e.GetPosition(this).Y - _dragStart;
            double trackH = Math.Max(1, ActualHeight - 4);
            double scrollable = Math.Max(1, View.ExtentHeight - View.ViewportHeight);
            double thumbH = Math.Max(28, trackH * (View.ViewportHeight / Math.Max(1, View.ExtentHeight)));
            double ratio = scrollable / Math.Max(1, trackH - thumbH);
            View.ScrollToVerticalOffset(_dragOffset + dy * ratio);
            e.Handled = true;
        }

        private void OnThumbUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            if (_thumb.IsMouseCaptured) _thumb.ReleaseMouseCapture();
            PaintHover();
            e.Handled = true;
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_hasScroll) return;
            double max = Math.Max(1, View.ExtentHeight - View.ViewportHeight);
            if (e.Delta > 0 && View.VerticalOffset <= 0.6) Bump(1);
            else if (e.Delta < 0 && View.VerticalOffset >= max - 0.6) Bump(-1);
        }

        /// <summary>键盘滚动：焦点在容器内时，方向键/翻页/首尾键驱动滚动。
        /// 输入类控件自己消费方向键，这里不抢；到顶/到底复用撞墙反馈。</summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled) return;
            object src = e.OriginalSource;
            if (src is System.Windows.Controls.Primitives.TextBoxBase ||
                src is System.Windows.Controls.ComboBox ||
                src is System.Windows.Controls.Slider ||
                src is System.Windows.Controls.ListBox ||
                src is System.Windows.Controls.TreeView ||
                src is System.Windows.Controls.TabControl) return;
            if (ScrollByKey(e.Key)) e.Handled = true;
        }

        /// <summary>按导航键滚动一格/一页/到首尾；返回是否消费。供事件与自检共用。
        /// 以 extent/viewport 自守卫，不依赖滑块布局事件（无头环境同样可用）。</summary>
        internal bool ScrollByKey(Key key)
        {
            double max = Math.Max(0, View.ExtentHeight - View.ViewportHeight);
            if (max <= 0.5) return false;
            double line = 48;
            double page = Math.Max(24, View.ViewportHeight * 0.9);
            double to = View.VerticalOffset;
            bool up = false;
            switch (key)
            {
                case Key.Down: to += line; break;
                case Key.Up: to -= line; up = true; break;
                case Key.PageDown: to += page; break;
                case Key.PageUp: to -= page; up = true; break;
                case Key.Home: to = 0; up = true; break;
                case Key.End: to = max; break;
                default: return false;
            }
            if (to < 0) to = 0;
            if (to > max) to = max;
            if (Math.Abs(to - View.VerticalOffset) < 0.5)
            {
                Bump(up ? 1 : -1);
                return true;
            }
            View.ScrollToVerticalOffset(to);
            return true;
        }

        /// <summary>到顶/到底的 4px 撞墙反馈：先弹 4px，再 200ms 回位。</summary>
        private void Bump(double dir)
        {
            if (!Ui.Animate) return;
            DateTime now = DateTime.UtcNow;
            if (now < _bumpStamp) return;
            _bumpStamp = now.AddMilliseconds(150);

            FrameworkElement fe = View.Content as FrameworkElement;
            if (fe == null) return;
            if (_bumpTarget != fe)
            {
                if (fe.RenderTransform != null && !(fe.RenderTransform is TranslateTransform)) return;
                _bumpTarget = fe;
                _bump = fe.RenderTransform as TranslateTransform;
                if (_bump == null)
                {
                    _bump = new TranslateTransform(0, 0);
                    fe.RenderTransform = _bump;
                }
            }

            _bump.BeginAnimation(TranslateTransform.YProperty, null);
            _bump.Y = 0;
            DoubleAnimationUsingKeyFrames k = new DoubleAnimationUsingKeyFrames();
            k.Duration = new Duration(TimeSpan.FromMilliseconds(220));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(dir * 4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45))));
            EasingDoubleKeyFrame back = new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)));
            back.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            k.KeyFrames.Add(back);
            _bump.BeginAnimation(TranslateTransform.YProperty, k);
        }

        /// <summary>
        /// 内容不高于视口时，把内容的最小高度顶到视口高度。
        /// 这是"页面底部一大片黑"的根治办法：页根用 * 行分配高度，只有内容真的被
        /// 拉到视口高，那些 * 行才有空间可分。改之前 11 个页面里有 4 个只占了视口 6 成。
        /// </summary>
        private void FitContent()
        {
            FrameworkElement fe = View.Content as FrameworkElement;
            if (fe == null) return;
            double vp = ActualHeight;
            if (vp < 1) vp = View.ViewportHeight;
            if (vp < 1) return;
            double pad = fe.Margin.Top + fe.Margin.Bottom;
            double want = vp - pad;
            if (want < 1) want = 1;
            if (Math.Abs(fe.MinHeight - want) > 0.5) fe.MinHeight = want;
        }

        private void LayoutThumb()
        {
            FitContent();
            double ext = View.ExtentHeight;
            double vp = View.ViewportHeight;
            if (ext <= vp + 1 || vp <= 0 || ActualHeight <= 0)
            {
                _hasScroll = false;
                _hot = false;
                _track.IsHitTestVisible = false;
                _track.Visibility = Visibility.Collapsed;
                _thumb.Visibility = Visibility.Collapsed;
                return;
            }

            _hasScroll = true;
            _track.Visibility = Visibility.Visible;
            _thumb.Visibility = Visibility.Visible;

            double trackH = Math.Max(1, ActualHeight - 4);
            double thumbH = Math.Max(28, trackH * (vp / ext));
            _thumb.Height = thumbH;
            double scrollable = Math.Max(1, ext - vp);
            double maxTop = Math.Max(0, trackH - thumbH);
            double top = (View.VerticalOffset / scrollable) * maxTop;
            _thumbTop = 2 + top;
            _thumb.Margin = new Thickness(0, _thumbTop, 1, 2);
        }
    }

    /// <summary>
    /// 悬浮提示：主题化、限宽 360、自动换行、相对目标下方/上方自动避让，
    /// 320ms 延迟、20s 停留；不再使用系统默认的鼠标跟随气泡。
    /// </summary>
}
