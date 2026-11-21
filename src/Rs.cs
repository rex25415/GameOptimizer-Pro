// ============================================================================
//  Rs.cs — 自适应原语：断点 / 流式重排容器 / 动效降级
//  设计：容器按自身可用宽度决定列数，页面只声明"各断点下要几列"。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go
{
    public enum Bp { Compact = 0, Regular = 1, Wide = 2, Ultra = 3 }

    public static class Rs
    {
        /// <summary>按容器可用宽度判定断点（不是窗口宽度，因此侧栏折叠也成立）。</summary>
        public static Bp Of(double w)
        {
            // 阈值对齐本应用的真实内容宽度区间（侧栏折叠后约 840–1280）：
            // Compact 是防御档（未来更小窗口或页面内嵌），Regular 起步于窄窗，
            // Ultra 对应 1560+ 的大屏。
            if (w < 640) return Bp.Compact;
            if (w < 900) return Bp.Regular;
            if (w < 1200) return Bp.Wide;
            return Bp.Ultra;
        }

        /// <summary>系统是否允许动画（辅助功能 / 远程会话下自动降级）。</summary>
        public static bool ReduceMotion
        {
            get
            {
                try { return !SystemParameters.ClientAreaAnimation; }
                catch { /* 系统参数读不到时不降级（保持动画），因为读失败不代表用户关了动画 */ return false; }
            }
        }

        /// <summary>监听断点变化，立即回调一次。</summary>
        public static void Watch(FrameworkElement el, Action<Bp> onChange)
        {
            if (el == null || onChange == null) return;
            Bp last = (Bp)(-1);
            SizeChangedEventHandler h = delegate
            {
                Bp bp = Of(el.ActualWidth);
                if (bp == last) return;
                last = bp;
                onChange(bp);
            };
            el.SizeChanged += h;
            el.Loaded += delegate { h(el, null); };
        }
    }

    /// <summary>
    /// 流式重排容器：给定四档断点下的列数，自动计算行数并重新布局。
    /// 子元素按阅读顺序填充，行列间距由统一 gap 控制。
    /// </summary>
    public class Reflow : Grid
    {
        private readonly List<UIElement> _kids = new List<UIElement>();
        private readonly int[] _cols = new int[4];
        private readonly double _gap;
        private int _applied = -1;

        public Reflow(double gap, int compact, int regular, int wide, int ultra)
        {
            _gap = gap;
            _cols[0] = Math.Max(1, compact);
            _cols[1] = Math.Max(1, regular);
            _cols[2] = Math.Max(1, wide);
            _cols[3] = Math.Max(1, ultra);
            SizeChanged += delegate { Apply(); };
            Loaded += delegate { Apply(); };
        }

        public void Add(UIElement e)
        {
            if (e == null) return;
            _kids.Add(e);
            Children.Add(e);
            _applied = -1;
            Apply();
        }

        public void AddRange(params UIElement[] items)
        {
            foreach (UIElement e in items) Add(e);
        }

        public void Clear()
        {
            _kids.Clear();
            Children.Clear();
            ColumnDefinitions.Clear();
            RowDefinitions.Clear();
            _applied = -1;
        }

        public int Count { get { return _kids.Count; } }

        /// <summary>强制按当前宽度重排（内容增删后调用）。</summary>
        public void Apply()
        {
            if (_kids.Count == 0) return;
            double w = ActualWidth;
            Bp bp = w <= 0 ? Bp.Wide : Rs.Of(w);
            int n = _cols[(int)bp];
            if (n > _kids.Count) n = _kids.Count;
            if (n == _applied && RowDefinitions.Count > 0) return;
            _applied = n;

            ColumnDefinitions.Clear();
            RowDefinitions.Clear();
            for (int i = 0; i < n; i++) ColumnDefinitions.Add(new ColumnDefinition());
            int rows = (int)Math.Ceiling((double)_kids.Count / n);
            for (int i = 0; i < rows; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < _kids.Count; i++)
            {
                int c = i % n;
                int r = i / n;
                UIElement k = _kids[i];
                FrameworkElement fe = k as FrameworkElement;
                if (fe != null)
                    fe.Margin = new Thickness(c == 0 ? 0 : _gap, r == 0 ? 0 : _gap, 0, 0);
                SetColumn(k, c);
                SetRow(k, r);
            }
        }
    }
}
