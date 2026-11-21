// ============================================================================
//  Sparkline.cs — 微型趋势带（标题栏活体脉搏）
//
//  它存在的理由：这个工具的全部价值是"系统现在的状态"，
//  而状态是一个**形状**，不是一个数字。22% 这个读数不告诉你它是刚从 80% 掉下来
//  还是一直平稳；一条 18px 高的趋势带会在一瞥之间说清楚。
//
//  零分配：只用 DrawLine + DrawEllipse，不建几何、不建字符串。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Media;

namespace Go
{
    public class Sparkline : FrameworkElement
    {
        private readonly double[] _buf;
        private int _n;
        private int _head;

        public Sparkline(int capacity)
        {
            if (capacity < 4) capacity = 4;
            _buf = new double[capacity];
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        private uint _tint = T.Accent;
        public uint Tint
        {
            get { return _tint; }
            set { if (_tint == value) return; _tint = value; InvalidateVisual(); }
        }

        /// <summary>压入一个 0..1 的样本。</summary>
        public void Push(double v)
        {
            if (double.IsNaN(v)) v = 0;
            if (v < 0) v = 0; else if (v > 1) v = 1;
            _buf[_head] = v;
            _head = (_head + 1) % _buf.Length;
            if (_n < _buf.Length) _n++;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 8 || h < 4) return;

            // 底线：没有它，空图看起来像"控件没加载"
            dc.DrawLine(Gfx.P(0x14FFFFFF, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));

            if (_n < 2) return;

            double step = w / (_buf.Length - 1);
            int start = (_n == _buf.Length) ? _head : 0;
            int firstX = _buf.Length - _n;
            Pen line = Gfx.P(_tint, 1.4);
            Pen hot = Gfx.P(((uint)0x38 << 24) | (_tint & 0x00FFFFFFu), 3.6);

            double px = firstX * step, py = h - _buf[start] * (h - 2) - 1;
            double lastX = px, lastY = py;

            for (int i = 1; i < _n; i++)
            {
                int idx = (start + i) % _buf.Length;
                double v = _buf[idx];
                double x = (firstX + i) * step;
                double y = h - v * (h - 2) - 1;
                dc.DrawLine(hot, new Point(px, py), new Point(x, y));
                dc.DrawLine(line, new Point(px, py), new Point(x, y));
                px = x; py = y;
                lastX = x; lastY = y;
            }

            // 前沿光学脉冲点
            dc.DrawEllipse(T.S(((uint)0x3A << 24) | (_tint & 0x00FFFFFFu)), null, new Point(lastX, lastY), 4.6, 4.6);
            dc.DrawEllipse(T.S(0xFF070C17), null, new Point(lastX, lastY), 2.8, 2.8);
            dc.DrawEllipse(T.S(_tint), null, new Point(lastX, lastY), 1.8, 1.8);
            dc.DrawEllipse(T.S(0xE8FFFFFF), null, new Point(lastX, lastY), 0.8, 0.8);
        }
    }
}
