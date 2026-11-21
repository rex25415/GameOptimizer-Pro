// ============================================================================
//  Motion.cs — 微动效引擎（克制的机械感，不是花哨）
//  设计准则：
//    · 只动合成层属性：Opacity / RenderTransform / LayoutTransform。
//      绝不改 Width/Height/Margin，因此永远不触发 measure/arrange。
//    · 一个共享的 60fps 驱动，N 条补间；没有补间时驱动自动停机。
//    · 补间对象与委托全部复用，稳态下每帧零分配。
//    · 系统要求减少动效（辅助功能 / 远程桌面）时，所有补间立即落到终点。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Go
{
    public static class Motion
    {
        // ---- 缓动 ----------------------------------------------------------
        /// <summary>三次缓出：起步快、收尾稳，机械感来自"到位不弹"。</summary>
        public static double EaseOutCubic(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double u = 1 - t;
            return 1 - u * u * u;
        }

        // ---- 补间池 --------------------------------------------------------
        private sealed class TweenSlot
        {
            public double From, To;
            public int StartMs, DurMs;
            public Action<double> Apply;
            public bool Live;
        }

        private const int PoolSize = 64;
        private static readonly TweenSlot[] _pool = new TweenSlot[PoolSize];
        private static int _live;
        private static DispatcherTimer _driver;
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private static void EnsurePool()
        {
            if (_pool[0] != null) return;
            for (int i = 0; i < PoolSize; i++) _pool[i] = new TweenSlot();
        }

        /// <summary>
        /// 起一条补间。apply 会被反复调用（首帧、中间帧、末帧各一次至少）。
        /// 调用方应缓存委托实例，避免每次起补间就分配一个闭包。
        /// </summary>
        public static void Tween(double from, double to, int ms, Action<double> apply)
        {
            if (apply == null) return;

            if (!Ui.Animate || Rs.ReduceMotion || ms <= 0)
            {
                apply(to);
                return;
            }
            if (Math.Abs(to - from) < 1e-9) { apply(to); return; }

            EnsurePool();
            TweenSlot slot = null;
            for (int i = 0; i < PoolSize; i++)
            {
                if (_pool[i].Live) continue;
                slot = _pool[i];
                break;
            }
            if (slot == null)
            {
                // 池满：直接落到终点，宁可少一条动效也不排队
                apply(to);
                return;
            }

            slot.From = from;
            slot.To = to;
            slot.StartMs = (int)_clock.ElapsedMilliseconds;
            slot.DurMs = ms;
            slot.Apply = apply;
            slot.Live = true;
            _live++;
            EnsureDriver();
        }

        public static bool Busy { get { return _live > 0; } }
        public static int LiveCount { get { return _live; } }

        // 订阅/退订必须用同一个委托实例：旧代码 -= delegate { } 退的是另一个实例，
        // 等于什么都没摘；而每次补间结束又把 _driver 置空，下一秒再新建一个。
        private static readonly EventHandler _tickHandler = delegate { Step(); };

        private static void EnsureDriver()
        {
            if (_driver == null)
            {
                _driver = new DispatcherTimer(DispatcherPriority.Render);
                _driver.Interval = TimeSpan.FromMilliseconds(16);
                _driver.Tick += _tickHandler;
            }
            if (!_driver.IsEnabled) _driver.Start();
        }

        private static void StopDriver()
        {
            if (_driver == null) return;
            _driver.Stop();     // 保留实例，下次起补间直接复用
        }

        /// <summary>推进一帧。公开出来是为了让自检可以无窗口地驱动它。</summary>
        public static void Step()
        {
            if (_live <= 0) { StopDriver(); return; }
            int now = (int)_clock.ElapsedMilliseconds;
            for (int i = 0; i < PoolSize; i++)
            {
                TweenSlot t = _pool[i];
                if (!t.Live) continue;
                double p = (now - t.StartMs) / (double)t.DurMs;
                if (p >= 1)
                {
                    t.Live = false;
                    _live--;
                    // 释放补间委托：闭包往往捕获整棵视觉树（托盘菜单行/页面控件）。
                    // 旧实现只置 Live=false 不清 Apply，32 个槽位会长期持有已关闭菜单的视觉树。
                    Action<double> ap = t.Apply;
                    t.Apply = null;
                    if (ap != null) ap(t.To);
                    continue;
                }
                double k = EaseOutCubic(p);
                t.Apply(t.From + (t.To - t.From) * k);
            }
            if (_live <= 0) StopDriver();
        }

        /// <summary>自检用：等待所有补间结束（无窗口时手动泵）。</summary>
        public static void FlushForTest(int maxMs)
        {
            int spent = 0;
            while (_live > 0 && spent < maxMs)
            {
                Step();
                System.Threading.Thread.Sleep(16);
                spent += 16;
            }
        }

        // ---- 元素级配方 ----------------------------------------------------

        /// <summary>入场：从 (dx,dy) 阻尼滑入 + 淡入。只碰 RenderTransform / Opacity。</summary>
        /// <summary>
        /// 页面切换：轻微缩放 + 位移 + 淡入（Rev79）。
        /// 只有缩放和位移都在合成层上，才叫"换页"；只做淡入看起来像加载。
        /// 0.994 → 1 这个幅度是刻意的：再大就有"弹窗感"，仪器不该花哨。
        /// </summary>
        public static void PageIn(UIElement el, int dir)
        {
            if (!Ui.Animate || Rs.ReduceMotion) { el.Opacity = 1; return; }

            TransformGroup tg = el.RenderTransform as TransformGroup;
            if (tg == null || tg.Children.Count < 2)
            {
                ScaleTransform s0 = new ScaleTransform(1, 1);
                TranslateTransform t0 = new TranslateTransform(0, 0);
                tg = new TransformGroup();
                tg.Children.Add(s0);
                tg.Children.Add(t0);
                el.RenderTransform = tg;
            }
            ScaleTransform sc = tg.Children[0] as ScaleTransform;
            TranslateTransform tr = tg.Children[1] as TranslateTransform;
            if (sc == null || tr == null) { el.Opacity = 1; return; }

            el.RenderTransformOrigin = new Point(0.5, 0.42);
            Duration d = new Duration(TimeSpan.FromMilliseconds(T.DurSlow));
            QuinticEase ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

            sc.ScaleX = 0.992; sc.ScaleY = 0.992;
            DoubleAnimation ax = new DoubleAnimation(1.0, d); ax.EasingFunction = ease;
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
            DoubleAnimation ay = new DoubleAnimation(1.0, d); ay.EasingFunction = ease;
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, ay);

            tr.X = 0;
            tr.Y = dir >= 0 ? 16 : -16;
            DoubleAnimation ty = new DoubleAnimation(0.0, d); ty.EasingFunction = ease;
            tr.BeginAnimation(TranslateTransform.YProperty, ty);

            el.Opacity = 0;
            DoubleAnimation op = new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(T.DurSlow - 80)));
            op.EasingFunction = ease;
            el.BeginAnimation(UIElement.OpacityProperty, op);
        }

        public static void SlideIn(UIElement el, double dx, double dy, int delayMs)
        {
            if (el == null) return;
            if (!Ui.Animate || Rs.ReduceMotion) { el.Opacity = 1; return; }

            TranslateTransform tt = el.RenderTransform as TranslateTransform;
            if (tt == null || !ReferenceEquals(el.RenderTransform, tt))
            {
                tt = new TranslateTransform(0, 0);
                el.RenderTransform = tt;
            }
            el.Opacity = 0;
            tt.X = dx;
            tt.Y = dy;

            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;

            DoubleAnimation fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)));
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            fade.EasingFunction = ease;
            el.BeginAnimation(UIElement.OpacityProperty, fade);

            DoubleAnimation moveX = new DoubleAnimation(dx, 0, new Duration(TimeSpan.FromMilliseconds(240)));
            moveX.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            moveX.EasingFunction = ease;
            tt.BeginAnimation(TranslateTransform.XProperty, moveX);

            DoubleAnimation moveY = new DoubleAnimation(dy, 0, new Duration(TimeSpan.FromMilliseconds(240)));
            moveY.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            moveY.EasingFunction = ease;
            tt.BeginAnimation(TranslateTransform.YProperty, moveY);
        }

        /// <summary>
        /// 呼吸光晕：守护命中时状态点缓慢明灭。
        /// 用 WPF 自带的重复动画，运行在合成层，UI 线程零回调。
        /// </summary>
        public static void Breathe(UIElement el, double min, double max, int periodMs)
        {
            if (el == null) return;
            if (!Ui.Animate || Rs.ReduceMotion) { el.Opacity = 1; return; }
            DoubleAnimation a = new DoubleAnimation(min, max, new Duration(TimeSpan.FromMilliseconds(periodMs)));
            a.AutoReverse = true;
            a.RepeatBehavior = RepeatBehavior.Forever;
            SineEase ease = new SineEase();
            ease.EasingMode = EasingMode.EaseInOut;
            a.EasingFunction = ease;
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }

        public static void StopBreathe(UIElement el)
        {
            if (el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
        }
    }
}
