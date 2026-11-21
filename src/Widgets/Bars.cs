// ============================================================================
//  Bars.cs — 分段负载条 / 迷你趋势 / 内存构成缎带
//  零分配：裁剪几何按尺寸缓存，画刷全部来自冻结缓存，渐变按色调复用。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Go
{
    /// <summary>分段负载条：28 段，亮段按色调染色。</summary>
    public class SegBar : FrameworkElement
    {
        private double _v;
        public int Segments = 28;
        public uint Tint = T.Accent;
        public double SegGap = 2;

        public double Value
        {
            get { return _v; }
            set
            {
                double v = Math.Max(0, Math.Min(1, value));
                if (v == _v) return;
                _v = v;
                InvalidateVisual();
            }
        }

        public SegBar(double v) { _v = v; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 2) return;

            // Rev79：从"28 段小方块"改为"连续量条 + 刻度 + 游标"。
            // 理由不是审美偏好，是读法：分段在低值（0.8%）时只剩 0~1 个亮块，
            // 读者看到的是"一堆暗方块里有一个亮的"，第一反应是数据缺失或占位符。
            // 连续条的填充长度本身就是量，不需要解释。
            double r = Math.Min(h / 2, 2.5);

            // 深空玻璃轨道
            dc.DrawRoundedRectangle(T.S(0x1CCFE6FF), null, new Rect(0, 0, w, h), r, r);

            // 刻度：微刻尺线
            if (Segments > 1)
            {
                Pen tick = Gfx.P(0x10CFE6FF, 1);
                double segW = w / Segments;
                for (int i = 1; i < Segments; i++)
                {
                    double x = i * segW;
                    dc.DrawLine(tick, new Point(x, h * 0.25), new Point(x, h * 0.75));
                }
            }

            if (_v <= 0.0005) return;

            double fw = w * _v;
            if (fw < 2) fw = 2;
            dc.DrawRoundedRectangle(T.S(((uint)0x30 << 24) | (Tint & 0x00FFFFFFu)), null, new Rect(0, -1, fw, h + 2), r + 1, r + 1);
            dc.DrawRoundedRectangle(T.S(Tint), null, new Rect(0, 0, fw, h), r, r);

            // 发光白炽游标头
            if (_v < 0.995 && h >= 3)
            {
                Pen head = Gfx.P(0xF0FFFFFF, 1.4);
                double hx = fw - 0.7;
                dc.DrawLine(head, new Point(hx, 0), new Point(hx, h));
            }

            // 顶面折光高光
            if (h >= 3.5)
            {
                Pen spec = Gfx.P(0x58FFFFFF, 1);
                dc.DrawLine(spec, new Point(r, 0.5), new Point(Math.Max(r, fw - r), 0.5));
            }
        }
    }

    /// <summary>内存构成缎带：按比例切分，圆角收边；悬停时高亮分段并显示容量与占比（未悬停零分配）。</summary>
    public class MemRibbon : FrameworkElement
    {
        public double[] Parts = new double[0];
        public uint[] Colors = new uint[0];
        public string[] Labels = new string[0];
        public string Unit = "MB";
        public double Gap = 2;
        public double Radius = 2;

        private double _hoverX = -1;

        public MemRibbon()
        {
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                _hoverX = e.GetPosition(this).X;
                InvalidateVisual();
            };
            MouseLeave += delegate
            {
                if (_hoverX >= 0) { _hoverX = -1; InvalidateVisual(); }
            };
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 10 || h < 4) return;
            int n = Parts.Length;
            if (n == 0) return;
            double totalGap = Gap * (n - 1);
            double totalPart = 0;
            for (int i = 0; i < n; i++) totalPart += Math.Max(0, Parts[i]);
            if (totalPart <= 0) return;

            int hoverIdx = -1;
            double hoverMid = 0;
            if (_hoverX >= 0 && _hoverX <= w)
            {
                double scanX = 0;
                for (int i = 0; i < n; i++)
                {
                    double sw = Math.Max(0, (w - totalGap) * (Math.Max(0, Parts[i]) / totalPart));
                    if (_hoverX >= scanX && _hoverX <= scanX + sw) { hoverIdx = i; hoverMid = scanX + sw * 0.5; break; }
                    scanX += sw + Gap;
                }
            }

            double x = 0;
            int cn = Colors.Length;
            for (int i = 0; i < n; i++)
            {
                double frac = Math.Max(0, Parts[i]) / totalPart;
                double sw = Math.Max(0, (w - totalGap) * frac);
                if (sw > 0.5 && i < cn)
                {
                    uint col = Colors[i];
                    if (hoverIdx >= 0 && hoverIdx != i)
                        col = ((uint)0x55 << 24) | (col & 0x00FFFFFFu);
                    Rect seg = new Rect(x, 0, sw, h);
                    double rad = Math.Max(Radius, Math.Min(5, h * 0.28));
                    dc.DrawRoundedRectangle(T.S(col), null, seg, rad, rad);
                    if (h >= 8 && (hoverIdx < 0 || hoverIdx == i))
                    {
                        // 3D 立体玻璃能量条：上半光泽折射带 + 底部深邃压暗 + 锐利顶棱
                        dc.DrawRoundedRectangle(T.S(hoverIdx == i ? 0x42FFFFFFu : 0x26FFFFFFu), null,
                            new Rect(x + 0.5, 0.5, Math.Max(0, sw - 1), h * 0.40), rad * 0.7, rad * 0.7);
                        dc.DrawRoundedRectangle(T.S(0x34000000u), null,
                            new Rect(x + 0.5, h * 0.65, Math.Max(0, sw - 1), h * 0.35 - 0.5), rad * 0.6, rad * 0.6);
                        dc.DrawLine(Gfx.P(hoverIdx == i ? 0xB8FFFFFFu : 0x4CFFFFFFu, 1),
                            new Point(x + rad, 0.5), new Point(Math.Max(x + rad, x + sw - rad), 0.5));
                    }
                }
                x += sw + Gap;
            }

            // 仅鼠标悬停且有标签时渲染 HUD 读数（非悬停路径严格零分配）
            if (hoverIdx >= 0 && Labels != null && hoverIdx < Labels.Length && !string.IsNullOrEmpty(Labels[hoverIdx]))
            {
                double val = Math.Max(0, Parts[hoverIdx]);
                double pct = val / totalPart * 100.0;
                string valText = val >= 1024 ? ((val / 1024.0).ToString("0.0") + " GB") : (val.ToString("0") + " " + Unit);
                string txt = Labels[hoverIdx] + " · " + valText + " (" + pct.ToString("0.0") + "%)";
                double dpi = Gfx.Dpi(this);
                FormattedText ft = Gfx.Text(txt, T.FsNano, T.Hi, 0, dpi);
                double bw = ft.Width + 12;
                double bh = Math.Min(h, ft.Height + 4);
                double bx = hoverMid - bw * 0.5;
                if (bx < 0) bx = 0;
                if (bx + bw > w) bx = Math.Max(0, w - bw);
                double by = (h - bh) * 0.5;
                uint segCol = hoverIdx < cn ? Colors[hoverIdx] : T.Accent;
                dc.DrawRoundedRectangle(T.S(0xEB0A0F16), Gfx.P(((uint)0x90 << 24) | (segCol & 0x00FFFFFFu), 1), new Rect(bx, by, bw, bh), 3, 3);
                dc.DrawText(ft, new Point(bx + 6, by + (bh - ft.Height) * 0.5));
            }
        }
    }
}
