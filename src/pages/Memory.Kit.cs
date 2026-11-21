// ============================================================================
//  Memory.Kit.cs — 04 内存与提交页的量具零件（Rev80 · INSTRUMENT GRADE）
//
//  为什么单独造两个量具：
//  通用 SegBar 画不出「没有读数」与「读数为 0」的差别。这一页的每个数字都必须
//  可追溯，所以缺测时量具只留轨道和刻度，绝不落一根零长的填充条 —— 那条零长度
//  的填充在读者眼里就是「0」，而 0 是一个测量结论。
//
//  两个类都只画矩形与线：零几何、零字符串分配（刻度文字走 Gfx.Text 缓存）。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Media;

namespace Go.Pages
{
    /// <summary>
    /// 细量条：轨道 + 填充 + 右端游标。Known=false 时只画轨道。
    /// 高度由调用方给；Value 是 0..1 的比例，不是绝对值。
    /// </summary>
    public class MeterBar : FrameworkElement
    {
        private double _v;
        private bool _known;
        private uint _tint = T.Accent;
        private uint _track = 0x16FFFFFF;

        public MeterBar()
        {
            Height = 5;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        public double Value
        {
            get { return _v; }
            set
            {
                double v = value;
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                if (v == _v) return;
                _v = v;
                InvalidateVisual();
            }
        }

        public bool Known
        {
            get { return _known; }
            set { if (_known == value) return; _known = value; InvalidateVisual(); }
        }

        public uint Tint
        {
            get { return _tint; }
            set { if (_tint == value) return; _tint = value; InvalidateVisual(); }
        }

        public uint Track
        {
            get { return _track; }
            set { if (_track == value) return; _track = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 6 || h < 2) return;

            double r = Math.Min(h / 2, 2);
            dc.DrawRoundedRectangle(T.S(_track), null, new Rect(0, 0, w, h), r, r);
            if (!_known || _v <= 0) return;

            double fw = w * _v;
            if (fw < 1.5) fw = 1.5;
            if (fw > w) fw = w;
            dc.DrawRoundedRectangle(T.S(_tint), null, new Rect(0, 0, fw, h), r, r);

            // 游标：条长本身已经是量，游标标出的是「正好到这里」的精度。
            if (fw < w - 1)
                dc.DrawRectangle(T.S(0xCC000000u | (_tint & 0x00FFFFFFu)), null, new Rect(fw - 1, 0, 1, h));
        }
    }

    /// <summary>
    /// 0 / 25 / 50 / 75 / 100% 刻度的比例尺。
    /// 填充分段与刻度分开画：刻度是尺，填充是读数 —— 两者都不能少。
    /// </summary>
    public class RatioRuler : FrameworkElement
    {
        private double _v;
        private bool _known;
        private uint _tint = T.Accent;

        public RatioRuler()
        {
            Height = 34;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        public double Value
        {
            get { return _v; }
            set
            {
                double v = value;
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                if (v == _v) return;
                _v = v;
                InvalidateVisual();
            }
        }

        public bool Known
        {
            get { return _known; }
            set { if (_known == value) return; _known = value; InvalidateVisual(); }
        }

        public uint Tint
        {
            get { return _tint; }
            set { if (_tint == value) return; _tint = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 60 || h < 20) return;

            const double trackH = 6;
            double y = 2;
            double r = 3;

            // 轨道：比条本身更暗，读作「尺子上的槽」。
            dc.DrawRoundedRectangle(T.S(0x18FFFFFF), null, new Rect(0, y, w, trackH), r, r);

            if (_known && _v > 0)
            {
                double fw = w * _v;
                if (fw < 3) fw = 3;
                dc.DrawRoundedRectangle(T.S(_tint), null, new Rect(0, y, fw, trackH), r, r);
                // 读数游标：上下都露头，读者一眼看到「现在停在这一格」。
                dc.DrawRectangle(T.S(0xF0000000u | (_tint & 0x00FFFFFFu)), null,
                    new Rect(Math.Min(w - 1.6, fw - 1), y - 4, 1.6, trackH + 8));
            }

            double dpi = Gfx.Dpi(this);
            Pen tick = Gfx.P(0x30FFFFFF, 1);
            for (int i = 0; i <= 4; i++)
            {
                double x = Math.Round(w * i / 4.0);
                dc.DrawLine(tick, new Point(x + 0.5, y + trackH), new Point(x + 0.5, y + trackH + 4));

                string s = (i * 25).ToString() + "%";
                FormattedText ft = Gfx.Text(s, 9, T.Mute, 0, dpi);
                double tx = i == 0 ? 0 : (i == 4 ? w - ft.Width : x - ft.Width / 2);
                dc.DrawText(ft, new Point(Math.Round(tx), y + trackH + 6));
            }
        }
    }
}
