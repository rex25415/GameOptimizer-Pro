// ============================================================================
//  Waveform.cs — 实时波形 2.0（唤醒抖动 / 帧时间 / 网络吞吐）
//  零分配：PathGeometry + PolyLineSegment 原地更新 Points（不再每帧 Open 流几何）；
//  画笔/渐变/文本全走 Gfx 缓存；hover 十字读数只在悬停时产生瞬时字符串。
//  视觉：渐变面积 + 双层辉光描线 + Y 轴刻度 + 阈值虚线 + hover 十字实时读数。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Go
{
    public class Waveform : FrameworkElement
    {
        private readonly double[] _buf;
        private int _head, _count;
        private double[] _ovBuf;
        private int _ovHead, _ovCount;
        public double Floor = 0, Ceil = 20;
        public double Threshold = 16.7;
        public string ThresholdText = "1.0ms";
        public uint Tint = T.Accent;
        public uint OverlayTint = T.SeriesA;
        public bool ShowThreshold = true;
        public bool ShowMeanLine = false;
        public bool SmoothCurve = true;
        public bool AutoScale = true;
        public bool ShowYAxis = true;
        public bool ShowOverlay = true;
        public string Unit = "µs";
        public double SampleStepSec = 0.2;

        private double _ceilSmooth;
        private RectangleGeometry _clip;
        private double _clipW, _clipH;

        // 复用流几何：Clear() 后重开，避免每帧 new + Freeze（实测 ~14 B/帧）。
        private StreamGeometry _area, _line, _ovLine;

        private double _hoverX = -1;
        private double _hoverY = -1;

        public Waveform(int capacity)
        {
            _buf = new double[capacity];
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                Point p = e.GetPosition(this);
                _hoverX = p.X;
                _hoverY = p.Y;
                InvalidateVisual();
            };
            MouseLeave += delegate { _hoverX = -1; _hoverY = -1; InvalidateVisual(); };
        }

        public bool HasOverlay { get { return ShowOverlay && _ovBuf != null && _ovCount >= 2; } }
        public double CurrentCeil { get { return _ceilSmooth; } }

        public void Push(double v)
        {
            _buf[_head] = v;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }

        /// <summary>写入可选第二对比序列单点（未调用前不分配任何缓冲）。</summary>
        public void PushOverlay(double v)
        {
            if (_ovBuf == null) _ovBuf = new double[_buf.Length];
            _ovBuf[_ovHead] = v;
            _ovHead = (_ovHead + 1) % _ovBuf.Length;
            if (_ovCount < _ovBuf.Length) _ovCount++;
        }

        /// <summary>批量写入对比序列；传 null 或 count &lt;= 0 清空。</summary>
        public void SetOverlay(double[] values, int count)
        {
            if (values == null || count <= 0)
            {
                ClearOverlay();
                return;
            }
            if (_ovBuf == null) _ovBuf = new double[_buf.Length];
            int n = Math.Min(count, Math.Min(values.Length, _ovBuf.Length));
            int start = Math.Max(0, count - n);
            for (int i = 0; i < n; i++) _ovBuf[i] = values[start + i];
            _ovCount = n;
            _ovHead = n % _ovBuf.Length;
            InvalidateVisual();
        }

        public void ClearOverlay()
        {
            _ovCount = 0;
            _ovHead = 0;
            InvalidateVisual();
        }

        public void Refresh() { InvalidateVisual(); }

        public double ValueAt(int logicalIndex)
        {
            int start = (_head - _count + _buf.Length) % _buf.Length;
            return _buf[(start + logicalIndex) % _buf.Length];
        }

        public double OverlayAt(int logicalIndex)
        {
            if (_ovBuf == null || _ovCount == 0) return 0;
            int start = (_ovHead - _ovCount + _ovBuf.Length) % _ovBuf.Length;
            return _ovBuf[(start + logicalIndex) % _ovBuf.Length];
        }

        private uint TintA(byte a) { return ((uint)a << 24) | (Tint & 0x00FFFFFFu); }

        private static string FmtUs(double v)
        {
            if (v <= 0) return "0";
            if (v >= 1000)
            {
                double k = v / 1000.0;
                if (Math.Abs(k - Math.Round(k)) < 0.01) return k.ToString("0") + "k";
                return k.ToString("0.#") + "k";
            }
            return v.ToString("0");
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 8 || h < 8) return;
            if (_clip == null || _clipW != w || _clipH != h)
            {
                _clip = new RectangleGeometry(new Rect(0, 0, w, h));
                _clip.Freeze();
                _clipW = w; _clipH = h;
            }
            dc.PushClip(_clip);
            try { RenderWave(dc, w, h); }
            finally { dc.Pop(); }
        }

        private void RenderWave(DrawingContext dc, double w, double h)
        {
            double padL = ShowYAxis ? 34 : 0, padR = 6, padT = 8, padB = 6;
            double cw = w - padL - padR, ch = h - padT - padB;
            if (cw < 16 || ch < 16) return;
            double dpi = Gfx.Dpi(this);

            // 底纹
            Pen gridHi = Gfx.P(0x1AFFFFFF, 1);
            Pen gridLo = Gfx.P(T.LineSoft, 1);
            for (int i = 0; i <= 4; i++)
            {
                double y = padT + ch * i / 4.0;
                dc.DrawLine(i == 4 ? gridHi : gridLo, new Point(padL, y), new Point(w - padR, y));
            }
            Pen gridV = Gfx.P(T.LineFaint, 1);
            for (int i = 1; i < 8; i++)
            {
                double x = padL + cw * i / 8.0;
                dc.DrawLine(gridV, new Point(x, padT), new Point(x, h - padB));
            }

            double xStep = _buf.Length > 1 ? cw / (_buf.Length - 1) : 0;
            int firstX = _buf.Length - _count;

            // 未采集区：窗口固定 30s，刚打开时左半边还没有数据。
            // 旧渲染只画右边那段线，左半边是空网格——读者第一反应是"图坏了"。
            // 现在把没数据的那一段压暗，并在采集起点画一道竖线：
            // 它说的是"我在等数据"，而不是"这里什么都没有"。
            if (firstX > 0 && xStep > 0)
            {
                double dataX = padL + firstX * xStep;
                if (dataX - padL > 2)
                {
                    // 压暗还没采到的那段，并在起点画一道竖线：它表示"我从这里开始有数据"。
                    dc.DrawRectangle(T.S(0x0B000000), null, new Rect(padL, padT, dataX - padL, ch));
                    dc.DrawLine(Gfx.Dash(0x33FFFFFF, 1, 1), new Point(dataX, padT), new Point(dataX, padT + ch));
                }
            }

            if (_count < 2) return;

            // 自动量程
            double hi = double.MinValue;
            for (int i = 0; i < _count; i++)
            {
                double v = ValueAt(i);
                if (v > hi) hi = v;
            }
            if (HasOverlay)
            {
                for (int i = 0; i < _ovCount; i++)
                {
                    double ov = OverlayAt(i);
                    if (ov > hi) hi = ov;
                }
            }
            if (!AutoScale) hi = Ceil;
            else
            {
                double minCeil = ShowThreshold ? Math.Max(Threshold * 1.25, 200) : 4.0;
                double target = Math.Max(hi * 1.15, minCeil);
                if (target > _ceilSmooth) _ceilSmooth = target;
                else _ceilSmooth += (target - _ceilSmooth) * 0.06;
                hi = _ceilSmooth;
            }
            double lo = AutoScale ? 0 : Floor;
            double range = Math.Max(0.001, hi - lo);
            double scaleY = ch / range;
            double baseY = padT + ch;

            // Y 轴刻度
            if (ShowYAxis)
            {
                for (int i = 0; i <= 4; i += 2)
                {
                    double v = lo + range * (1 - i / 4.0);
                    FormattedText ft = Gfx.Text(FmtUs(v), T.FsNano, T.Mute, 0, dpi);
                    double y = padT + ch * i / 4.0;
                    dc.DrawText(ft, new Point(padL - ft.Width - 5, y - ft.Height / 2));
                }
            }

            // 阈值虚线 + 玻璃胶囊标牌
            if (ShowThreshold)
            {
                double ty = padT + ch - (Threshold - lo) * scaleY;
                dc.DrawLine(Gfx.Dash(0x52FFB13C, 1, 1), new Point(padL, ty), new Point(w - padR, ty));
                FormattedText ft = Gfx.Text(ThresholdText, 8.5, 0xFFFFC86B, 0, dpi);
                double pw = ft.Width + 10, ph = ft.Height + 3;
                double px = w - padR - pw - 4;
                double py = ty - ph - 3;
                if (py < padT) py = ty + 3;
                dc.DrawRoundedRectangle(T.S(0xDC1C150A), Gfx.P(0x66FFB13C, 1), new Rect(px, py, pw, ph), ph * 0.5, ph * 0.5);
                dc.DrawText(ft, new Point(px + 5, py + 1.5));
            }

            // 可选实测均值参考虚线 + 胶囊标牌（ShowMeanLine=true 时启用，零数组分配）
            if (ShowMeanLine && _count >= 4)
            {
                double sum = 0;
                for (int i = 0; i < _count; i++) sum += ValueAt(i);
                double mean = sum / _count;
                double my = padT + ch - (mean - lo) * scaleY;
                if (my >= padT + 4 && my <= baseY - 4)
                {
                    double sx0 = padL + firstX * xStep;
                    dc.DrawLine(Gfx.Dash(TintA(0x58), 1, 2), new Point(sx0, my), new Point(w - padR, my));
                    FormattedText mf = Gfx.Text("AVG", 8.5, Tint, 0, dpi);
                    double pw = mf.Width + 10, ph = mf.Height + 3;
                    double ly = my - ph - 3;
                    if (ly < padT) ly = my + 3;
                    dc.DrawRoundedRectangle(T.S(0xDD0A1324), Gfx.P(TintA(0x70), 1), new Rect(sx0 + 6, ly, pw, ph), ph * 0.5, ph * 0.5);
                    dc.DrawText(mf, new Point(sx0 + 11, ly + 1.5));
                }
            }

            // 面积与曲线：流几何复用 + 单调三次平滑贝塞尔（Monotone Hermite，零数组分配）
            if (_area == null) _area = new StreamGeometry();
            else _area.Clear();
            using (StreamGeometryContext ctx = _area.Open())
            {
                double x0 = padL + firstX * xStep;
                double y0 = ClampY(padT + ch - (ValueAt(0) - lo) * scaleY, padT, baseY);
                ctx.BeginFigure(new Point(x0, baseY), true, true);
                ctx.LineTo(new Point(x0, y0), true, false);
                EmitCurve(ctx, firstX, xStep, padL, padT, ch, lo, scaleY, baseY, false);
                ctx.LineTo(new Point(padL + (_buf.Length - 1) * xStep, baseY), true, false);
            }
            dc.DrawGeometry(Gfx.Grad3(Tint, 118, 42, 4), null, _area);

            if (_line == null) _line = new StreamGeometry();
            else _line.Clear();
            using (StreamGeometryContext ctx = _line.Open())
            {
                double x0 = padL + firstX * xStep;
                double y0 = ClampY(padT + ch - (ValueAt(0) - lo) * scaleY, padT, baseY);
                ctx.BeginFigure(new Point(x0, y0), false, false);
                EmitCurve(ctx, firstX, xStep, padL, padT, ch, lo, scaleY, baseY, false);
            }
            // 四层光学示波描线：大气柔光 + 中辉光 + 饱和主芯 + 白炽高光丝
            dc.DrawGeometry(null, Gfx.P(TintA(0x1A), 9.0), _line);
            dc.DrawGeometry(null, Gfx.P(TintA(0x48), 4.2), _line);
            dc.DrawGeometry(null, Gfx.P(Tint, 1.8), _line);
            dc.DrawGeometry(null, Gfx.P(0x90FFFFFF, 0.8), _line);

            // 可选第二对比序列：未设置时完全跳过（零分配、零绘制）
            int ovFirstX = _ovBuf != null ? (_ovBuf.Length - _ovCount) : 0;
            if (HasOverlay)
            {
                if (_ovLine == null) _ovLine = new StreamGeometry();
                else _ovLine.Clear();
                using (StreamGeometryContext ctx = _ovLine.Open())
                {
                    ctx.BeginFigure(new Point(padL + ovFirstX * xStep, ClampY(padT + ch - (OverlayAt(0) - lo) * scaleY, padT, baseY)), false, false);
                    EmitCurve(ctx, ovFirstX, xStep, padL, padT, ch, lo, scaleY, baseY, true);
                }
                dc.DrawGeometry(null, Gfx.Dash(((uint)0xCC << 24) | (OverlayTint & 0x00FFFFFFu), 1.2, 1), _ovLine);
            }

            // 最新点：四层光学脉冲头
            double lastX = padL + (_buf.Length - 1) * xStep;
            double lastY = ClampY(padT + ch - (ValueAt(_count - 1) - lo) * scaleY, padT, baseY);
            Point lastPt = new Point(lastX, lastY);
            dc.DrawEllipse(T.S(TintA(0x22)), null, lastPt, 10.5, 10.5);
            dc.DrawEllipse(T.S(TintA(0x46)), null, lastPt, 6.4, 6.4);
            dc.DrawEllipse(T.S(0xFF0A0F15), null, lastPt, 4.4, 4.4);
            dc.DrawEllipse(T.S(Tint), null, lastPt, 2.5, 2.5);
            dc.DrawEllipse(T.S(0xE6FFFFFF), null, lastPt, 1.0, 1.0);

            // hover 十字准星 + 带底板数值/时间偏移徽标 + 追踪圆点
            if (_hoverX >= padL && _hoverX <= w - padR && xStep > 0)
            {
                int slot = (int)Math.Round((_hoverX - padL) / xStep);
                int li = slot - firstX;
                if (li < 0) li = 0;
                if (li > _count - 1) li = _count - 1;
                double hx = padL + (firstX + li) * xStep;
                double hv = ValueAt(li);
                double hy = padT + ch - (hv - lo) * scaleY;

                Pen crossPen = Gfx.Dash(0x3AFFFFFF, 1, 2);
                dc.DrawLine(crossPen, new Point(hx, padT), new Point(hx, baseY));
                dc.DrawLine(crossPen, new Point(padL, hy), new Point(w - padR, hy));

                dc.DrawEllipse(T.S(TintA(0x38)), null, new Point(hx, hy), 7, 7);
                dc.DrawEllipse(T.S(0xFF0A0F15), null, new Point(hx, hy), 4.2, 4.2);
                dc.DrawEllipse(T.S(T.Hi), null, new Point(hx, hy), 2.2, 2.2);

                int ago = (_count - 1) - li;
                string timeTag = ago == 0 ? "NOW" : ("-" + (ago * SampleStepSec).ToString("0.0") + "s");
                string badge = FmtUs(hv) + " " + Unit + "  ·  " + timeTag;

                if (HasOverlay)
                {
                    int ovi = (firstX + li) - ovFirstX;
                    if (ovi >= 0 && ovi < _ovCount)
                    {
                        double ov = OverlayAt(ovi);
                        double oy = padT + ch - (ov - lo) * scaleY;
                        dc.DrawEllipse(T.S(0xFF0A0F15), null, new Point(hx, oy), 4.2, 4.2);
                        dc.DrawEllipse(T.S(OverlayTint), null, new Point(hx, oy), 2.2, 2.2);
                        badge += "  /  " + FmtUs(ov) + " " + Unit;
                    }
                }

                FormattedText rt = Gfx.Text(badge, T.FsNano, T.Hi, 0, dpi);
                double bw = rt.Width + 12;
                double bh = rt.Height + 6;
                double bx = hx + 10;
                if (bx + bw > w - padR) bx = hx - bw - 10;
                if (bx < padL) bx = padL;
                double by = hy - bh - 8;
                if (by < padT) by = Math.Min(baseY - bh, hy + 10);

                Rect pill = new Rect(bx, by, bw, bh);
                dc.DrawRoundedRectangle(T.S(0xF20B1119), Gfx.P(TintA(0x78), 1), pill, 4, 4);
                dc.DrawRectangle(T.S(Tint), null, new Rect(bx + 1, by + 3, 2, bh - 6));
                dc.DrawText(rt, new Point(bx + 7, by + 3));
            }
        }

        private static double ClampY(double y, double top, double bottom)
        {
            if (y < top) return top;
            if (y > bottom) return bottom;
            return y;
        }

        /// <summary>
        /// 单调三次 Hermite 贝塞尔曲线发射器（Fritsch-Carlson 斜率钳制）。
        /// 纯标量局部计算，零托管数组分配；在平滑波形的同时严格保证任意区间内不出现虚假过冲或负值。
        /// </summary>
        private void EmitCurve(StreamGeometryContext ctx, int firstX, double xStep, double padL,
                               double padT, double ch, double lo, double scaleY, double baseY, bool overlay)
        {
            int n = overlay ? _ovCount : _count;
            if (n < 2) return;
            if (!SmoothCurve || n < 3 || xStep < 1.2)
            {
                for (int i = 1; i < n; i++)
                {
                    double v = overlay ? OverlayAt(i) : ValueAt(i);
                    double y = ClampY(padT + ch - (v - lo) * scaleY, padT, baseY);
                    ctx.LineTo(new Point(padL + (firstX + i) * xStep, y), true, false);
                }
                return;
            }

            double third = xStep / 3.0;
            for (int i = 0; i < n - 1; i++)
            {
                double x1 = padL + (firstX + i) * xStep;
                double x2 = x1 + xStep;
                double y0 = ClampY(padT + ch - ((overlay ? OverlayAt(i > 0 ? i - 1 : 0) : ValueAt(i > 0 ? i - 1 : 0)) - lo) * scaleY, padT, baseY);
                double y1 = ClampY(padT + ch - ((overlay ? OverlayAt(i) : ValueAt(i)) - lo) * scaleY, padT, baseY);
                double y2 = ClampY(padT + ch - ((overlay ? OverlayAt(i + 1) : ValueAt(i + 1)) - lo) * scaleY, padT, baseY);
                double y3 = ClampY(padT + ch - ((overlay ? OverlayAt(i + 2 < n ? i + 2 : n - 1) : ValueAt(i + 2 < n ? i + 2 : n - 1)) - lo) * scaleY, padT, baseY);

                double d0 = y1 - y0;
                double d1 = y2 - y1;
                double d2 = y3 - y2;

                // 单调斜率：相邻段异号（局部极值）时切线斜率归零，绝不产生过冲
                double m1 = (i == 0) ? d1 : ((d0 * d1 <= 0) ? 0 : 0.5 * (d0 + d1));
                double m2 = (i == n - 2) ? d1 : ((d1 * d2 <= 0) ? 0 : 0.5 * (d1 + d2));

                double cy1 = ClampY(y1 + m1 * (1.0 / 3.0), Math.Min(y1, y2), Math.Max(y1, y2));
                double cy2 = ClampY(y2 - m2 * (1.0 / 3.0), Math.Min(y1, y2), Math.Max(y1, y2));
                ctx.BezierTo(new Point(x1 + third, cy1), new Point(x2 - third, cy2), new Point(x2, y2), true, false);
            }
        }
    }
}