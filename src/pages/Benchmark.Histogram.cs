// ============================================================================
//  Benchmark.Histogram.cs — 自适应分箱直方图（单次 OnRender 画完）
//
//  为什么不用几十个 Rectangle 拼：一个箱子就是一个元素，48 箱 = 48 次
//  measure + arrange + 命中测试 + 每个元素一条渲染指令。这条页面每 150ms
//  刷新一次，旧写法把 measure/arrange 预算全吃在图表上。这里跟 SegBar 一样：
//  数据只写进一个 int[48]，OnRender 里用缓存的画笔/画刷/FormattedText 一次画完，
//  稳态零堆分配（Rect/Point 是结构体，文本走 Gfx.Text 的缓存，字符串在设数据时
//  一次性生成）。
//
//  分箱：主图域 = [min, max] ±5% 跨度，48 个等宽箱，高度按最大箱归一。
//  样本 < 2 或零方差 → 空态（不画柱、不画标记线），因为一条竖线会被读成
//  "所有样本都落在这一个值上"，那是错的。
//
//  密集区放大：唤醒超调是长尾量 —— 一次窗口里只要有一个 8 ms 的样本，主图的
//  14.3–15.6 ms 主体就会被压到右侧 20% 的宽度里，看上去又成了"数据挤在右边"。
//  主图按 [min,max] 定域（不裁剪任何样本），同时在下半部画一条 P5–P95 的放大带，
//  让主体形状可读。放大带是同一个 X 轴量的另一个窗口，不是另一次测量。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Media;

namespace Go
{
    public class BenchmarkHistogram : FrameworkElement
    {
        /// <summary>固定 48 箱（任务要求 40–60）。箱数固定而非随宽度变化：
        /// 箱宽是读数的一部分，随窗口改变会让同一次运行在两次截图里长得不一样。</summary>
        public const int Bins = 48;

        private const double PadL = 50;
        private const double PadR = 16;
        private const double PadT = 32;
        // 底部留 30：刻度一行 + 轴名一行。轴名不是装饰，是"µs"这个量纲的出处。
        private const double PadB = 30;

        private readonly string[] _markLab = new string[4];
        private readonly RotateTransform _yRot;

        private readonly int[] _counts = new int[Bins];
        private readonly int[] _zcounts = new int[Bins];
        private readonly string[] _xLab = new string[5];
        private readonly string[] _yLab = new string[3];
        private readonly string[] _zLab = new string[2];
        private readonly int[] _order = new int[4];
        private readonly double[] _markVal = new double[4];
        private readonly double[] _markX = new double[4];
        private readonly Pen[] _markPen = new Pen[4];
        private readonly uint[] _markCol = new uint[4];

        private readonly Pen _gridPen;
        private readonly Pen _axisPen;
        private readonly SolidColorBrush _barFill;
        private readonly SolidColorBrush _barCap;
        private readonly SolidColorBrush _zero;

        private bool _ready;
        private int _total;
        private int _maxCount = 1;
        private double _lo, _hi, _span;
        private double _p50, _p95, _p99, _mean;
        private string _empty = "\u5C1A\u65E0\u6837\u672C";

        // ---- 密集区放大带 ---------------------------------------------------
        private bool _zoom;
        private int _zmax = 1;
        private int _zn;
        private double _zlo, _zhi, _zspan;

        /// <summary>落在主图分箱域之外而被夹进首/末箱的样本数。域按 [min,max] 生成，正常恒为 0。</summary>
        public int Clipped;

        public BenchmarkHistogram()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;

            _gridPen = Gfx.P(T.GridMinor, 1);
            _axisPen = Gfx.P(0x1FFFFFFF, 1);
            _barFill = T.S(Ui.Wash(T.Accent, 0x93));
            _barCap = T.S(T.Accent);
            _zero = T.S(0x0EFFFFFF);

            // 四条分位线只保留一条语义色：P99 是这一页的主指标，用 Warn（需要看）。
            // 其余三条退成灰阶——四条彩线并排时，没有一条是"重点"。
            _markCol[0] = 0x8FB6C2D1;
            _markCol[1] = 0xC4B6C2D1;
            _markCol[2] = T.Warn;
            _markCol[3] = 0x59FFFFFF;
            _markPen[0] = Gfx.Dash(_markCol[0], 1, 1);
            _markPen[1] = Gfx.Dash(_markCol[1], 1, 2);
            _markPen[2] = Gfx.Dash(_markCol[2], 1, 1);
            _markPen[3] = Gfx.Dash(_markCol[3], 1, 3);

            // 轴名竖排：静态变换对象冻结一次，OnRender 里只 Push/Pop，不建新对象。
            _yRot = new RotateTransform(-90);
            _yRot.Freeze();
        }

        public bool Ready { get { return _ready; } }
        public double Lo { get { return _lo; } }
        public double Hi { get { return _hi; } }
        public double BinWidth { get { return _span / Bins; } }
        public int Total { get { return _total; } }
        public int MaxCount { get { return _maxCount; } }
        public double P50 { get { return _p50; } }
        public double P95 { get { return _p95; } }
        public double P99 { get { return _p99; } }
        public double Mean { get { return _mean; } }

        /// <summary>第 i 箱的样本数（仅用于自检；渲染路径直接读 _counts，不走这里）。</summary>
        public int CountAt(int i)
        {
            if (i < 0 || i >= Bins) return 0;
            return _counts[i];
        }

        public bool ZoomActive { get { return _zoom; } }
        public double ZoomLo { get { return _zlo; } }
        public double ZoomHi { get { return _zhi; } }

        // ====================================================================
        //  数据
        // ====================================================================

        /// <summary>
        /// 用已排序样本重算箱子。sorted 由调用方的复用缓冲提供（不再拷贝）。
        /// 分位数由调用方传入 —— 它已经在同一次排序里算过了。
        /// </summary>
        public void SetData(double[] sorted, int n, double p50, double p95, double p99, double mean)
        {
            _p50 = p50; _p95 = p95; _p99 = p99; _mean = mean;
            _total = 0;
            _maxCount = 1;
            Clipped = 0;
            _zoom = false;
            for (int i = 0; i < Bins; i++) { _counts[i] = 0; _zcounts[i] = 0; }

            if (sorted == null || n < 2)
            {
                _empty = "\u6837\u672C\u4E0D\u8DB3 \u00B7 \u81F3\u5C11\u9700\u8981 2 \u4E2A\u4E0D\u540C\u6837\u672C";
                _ready = false;
                InvalidateVisual();
                return;
            }

            double lo = sorted[0];
            double hi = sorted[n - 1];
            double span = hi - lo;
            if (!(span > 0))
            {
                // 零方差：所有样本落在同一个值上。这不是分布，画出来是一根竖线。
                _empty = "\u6837\u672C\u65B9\u5DEE\u4E3A 0 \u00B7 \u6240\u6709\u6837\u672C\u843D\u5728\u540C\u4E00\u4E2A\u503C\u4E0A";
                _ready = false;
                InvalidateVisual();
                return;
            }

            double pad = span * 0.05;
            _lo = lo - pad;
            _hi = hi + pad;
            // 时间轴不允许出现负值：min 恰好是 0 时 ±5% 会把左端推到 -0.3 ms，
            // 截图上就是一条 "-301 µs" 的刻度——读者第一眼会以为仪表坏了。
            // 夹到 0 不会丢样本（样本按定义非负），真有负样本会落进首箱并由 Clipped 计数。
            if (_lo < 0) _lo = 0;
            _span = _hi - _lo;
            if (!(_span > 0))
            {
                _ready = false;
                InvalidateVisual();
                return;
            }

            int clipped = 0;
            int maxc = 1;
            for (int i = 0; i < n; i++)
            {
                int b = (int)((sorted[i] - _lo) / _span * Bins);
                if (b < 0) { b = 0; clipped++; }
                else if (b >= Bins) { b = Bins - 1; clipped++; }
                _counts[b]++;
                if (_counts[b] > maxc) maxc = _counts[b];
            }

            Clipped = clipped;
            _maxCount = maxc;
            _total = n;
            _ready = true;
            BuildZoom(sorted, n);
            BuildLabels();
            InvalidateVisual();
        }

        /// <summary>
        /// 主图域被一两个极端样本撑开时，P5–P95 的放大带让主体形状可读。
        /// 触发条件：中间 90% 的样本只占主图域宽度的 45% 以下。
        /// 放大带里的样本仍然全部计入主图，这里只是换一个窗口再看一遍。
        /// </summary>
        private void BuildZoom(double[] sorted, int n)
        {
            _zoom = false;
            _zn = 0;
            _zmax = 1;

            int i5 = (int)(n * 0.05);
            if (i5 < 0) i5 = 0;
            if (i5 > n - 1) i5 = n - 1;
            int i95 = (int)(n * 0.95);
            if (i95 < 0) i95 = 0;
            if (i95 > n - 1) i95 = n - 1;

            double a = sorted[i5];
            double b = sorted[i95];
            double core = b - a;
            if (!(core > 0)) return;
            if (core > _span * 0.45) return;   // 主体本来就舒展，不需要放大

            double pad = core * 0.10;
            _zlo = a - pad;
            if (_zlo < 0) _zlo = 0;
            _zhi = b + pad;
            _zspan = _zhi - _zlo;
            if (!(_zspan > 0)) return;

            for (int i = 0; i < n; i++)
            {
                double v = sorted[i];
                if (v < _zlo || v > _zhi) continue;
                int k = (int)((v - _zlo) / _zspan * Bins);
                if (k < 0) k = 0; else if (k >= Bins) k = Bins - 1;
                _zcounts[k]++;
                if (_zcounts[k] > _zmax) _zmax = _zcounts[k];
                _zn++;
            }
            _zoom = _zn > 1;
        }

        /// <summary>空态：只画刻度底 + 一句为什么空。</summary>
        public void SetEmpty(string text)
        {
            _ready = false;
            _zoom = false;
            _total = 0;
            _maxCount = 1;
            Clipped = 0;
            _empty = text == null ? "" : text;
            InvalidateVisual();
        }

        private void BuildLabels()
        {
            for (int i = 0; i < 5; i++)
            {
                double v = _lo + _span * i / 4.0;
                _xLab[i] = Num(v);
            }
            _yLab[0] = "0";
            _yLab[1] = (_maxCount / 2).ToString("#,##0");
            _yLab[2] = _maxCount.ToString("#,##0");

            // 标记标签带数值：读者不必再用眼睛把虚线对回 X 轴刻度。
            _markLab[0] = "P50 " + Num(_p50);
            _markLab[1] = "P95 " + Num(_p95);
            _markLab[2] = "P99 " + Num(_p99);
            _markLab[3] = "MEAN " + Num(_mean);

            _zLab[0] = Num(_zlo);
            _zLab[1] = Num(_zhi);
        }

        private static string Num(double v)
        {
            if (Math.Abs(v) >= 100000) return (v / 1000.0).ToString("0") + "k";
            return v.ToString("#,##0");
        }

        private double Pos(double v)
        {
            double t = (v - _lo) / _span;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return t;
        }

        // ====================================================================
        //  绘制
        // ====================================================================
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 80 || h < 60) return;

            double cw = w - PadL - PadR;
            double ch = h - PadT - PadB;
            if (cw < 40 || ch < 30) return;

            double dpi = Gfx.Dpi(this);

            // 放大带占下半部：主图 45%，中间 50px 是主轴刻度 + 轴名 + 放大带标题。
            double mainH = ch;
            // 有放大带时，主图只做"全量范围"的上下文，放大带才是读者真正读形状的地方，
            // 所以把更多高度留给放大带（45% / 55%）。
            if (_ready && _zoom) mainH = ch * 0.45;
            double baseY = PadT + mainH;

            // ---- 蓝图刻度底 ----
            for (int i = 0; i <= 4; i++)
            {
                double y = Math.Round(PadT + mainH * i / 4.0) + 0.5;
                dc.DrawLine(_gridPen, new Point(PadL, y), new Point(PadL + cw, y));
            }
            for (int i = 1; i < 4; i++)
            {
                double x = Math.Round(PadL + cw * i / 4.0) + 0.5;
                dc.DrawLine(_gridPen, new Point(x, PadT), new Point(x, baseY));
            }

            // ---- 轴名 ----------------------------------------------------
            // 图必须自带量纲：左边竖排"样本数"，右下写"唤醒超调 µs"。
            // 旧图只有一串裸数字，读者得回面板标题里去猜 X 是什么、Y 是什么。
            FormattedText xCap = Gfx.Text("\u5524\u9192\u8D85\u8C03 \u00B5s", 8.5, T.Mute, 2, dpi);
            double xcx = PadL + cw - xCap.Width;
            if (xcx < PadL) xcx = PadL;
            dc.DrawText(xCap, new Point(xcx, baseY + 16));

            FormattedText yCap = Gfx.Text("\u6837\u672C\u6570 n", 8.5, T.Mute, 2, dpi);
            double yTop = PadT + (mainH - yCap.Width) / 2;
            if (yTop < PadT) yTop = PadT;
            dc.PushTransform(_yRot);
            dc.DrawText(yCap, new Point(-(yTop + yCap.Width), 6));
            dc.Pop();

            // ---- 主图柱 ----
            if (_ready && _total > 0) DrawBins(dc, _counts, _maxCount, PadL, PadT, cw, mainH);

            // ---- 主图基线 ----
            dc.DrawLine(_axisPen, new Point(PadL, baseY + 0.5), new Point(PadL + cw, baseY + 0.5));

            if (_ready)
            {
                // ---- X 刻度：5 个真实数值（等宽小字）----
                for (int i = 0; i < 5; i++)
                {
                    FormattedText ft = Gfx.Text(_xLab[i], 8.5, T.Mute, 0, dpi);
                    double x = PadL + cw * i / 4.0;
                    if (i == 4) x -= ft.Width;
                    else if (i > 0) x -= ft.Width / 2;
                    if (x < 2) x = 2;
                    if (x + ft.Width > w - 2) x = w - 2 - ft.Width;
                    dc.DrawText(ft, new Point(x, baseY + 5));
                }

                // ---- Y 刻度：样本数（0 / 中位 / 最大箱）----
                for (int i = 0; i < 3; i++)
                {
                    FormattedText ft = Gfx.Text(_yLab[i], 8.5, T.Mute, 0, dpi);
                    double y = baseY - mainH * (i * 0.5);
                    dc.DrawText(ft, new Point(PadL - ft.Width - 7, y - ft.Height / 2));
                }

                // ---- P50 / P95 / P99 / MEAN 竖标记线 ----
                DrawMarkers(dc, dpi, w, cw, baseY, mainH);

                // ---- 密集区放大带 ----
                if (_zoom) DrawZoom(dc, dpi, cw, ch, mainH);
            }
            else
            {
                // ---- 空态：不画柱、不画标记线，只说明为什么空 ----
                FormattedText e = Gfx.Text(_empty, 10.5, T.Mute, 2, dpi);
                double ex = PadL + (cw - e.Width) / 2;
                if (ex < PadL) ex = PadL;
                dc.DrawText(e, new Point(ex, PadT + ch / 2 - e.Height / 2));
            }
        }

        /// <summary>柱：立体光学渐变频谱柱 + 峰值亮盖；空箱给 1px 底桩。</summary>
        private void DrawBins(DrawingContext dc, int[] counts, int maxCount, double x0, double y0, double cw, double chh)
        {
            double bw = cw / Bins;
            double barW = bw - (bw >= 4 ? 2.0 : 1.0);
            if (barW < 1.5) barW = 1.5;
            double baseY = y0 + chh;
            if (maxCount < 1) maxCount = 1;

            Brush gradBar = Gfx.Grad3(T.Accent, 224, 132, 42);
            Brush peakBar = Gfx.Grad3(T.Accent, 255, 190, 88);
            Brush peakCap = T.S(0xF0FFFFFF);
            Brush capBrush = T.S(T.Accent);

            for (int i = 0; i < Bins; i++)
            {
                double x = x0 + bw * i;
                int c = counts[i];
                if (c <= 0)
                {
                    dc.DrawRectangle(_zero, null, new Rect(x, baseY - 1, barW, 1));
                    continue;
                }
                double bh = chh * ((double)c / maxCount);
                if (bh < 2.5) bh = 2.5;
                double top = baseY - bh;
                bool isPeak = c == maxCount;
                dc.DrawRoundedRectangle(isPeak ? peakBar : gradBar, null, new Rect(x, top, barW, bh), 2, 2);
                dc.DrawRoundedRectangle(isPeak ? peakCap : capBrush, null, new Rect(x, top, barW, 2), 1, 1);
            }
        }

        private void DrawZoom(DrawingContext dc, double dpi, double cw, double ch, double mainH)
        {
            // 主图刻度 + 轴名 + 放大带标题各占一行，50 是这三行的总开销。
            double top = PadT + mainH + 50;
            double zh = ch - mainH - 50;
            if (zh < 24) return;

            // 标题行
            FormattedText cap = Gfx.Text("\u5BC6\u96C6\u533A\u653E\u5927 P5\u2013P95", 8.5, T.Dim, 2, dpi);
            dc.DrawText(cap, new Point(PadL, top - 15));

            FormattedText cnt = Gfx.Text("n " + _zn.ToString("#,##0") + " \u00B7 \u79BB\u7FA4\u6837\u672C\u4ECD\u8BA1\u5165\u4E0A\u56FE", 8.5, T.Mute, 2, dpi);
            dc.DrawText(cnt, new Point(PadL + cap.Width + 10, top - 15));

            // 底纹
            for (int i = 0; i <= 2; i++)
            {
                double y = Math.Round(top + zh * i / 2.0) + 0.5;
                dc.DrawLine(_gridPen, new Point(PadL, y), new Point(PadL + cw, y));
            }

            DrawBins(dc, _zcounts, _zmax, PadL, top, cw, zh);
            dc.DrawLine(_axisPen, new Point(PadL, top + zh + 0.5), new Point(PadL + cw, top + zh + 0.5));

            // 放大带的标签：只给两端数值，避免和主图刻度抢注意力
            FormattedText l0 = Gfx.Text(_zLab[0], 8, T.Mute, 0, dpi);
            dc.DrawText(l0, new Point(PadL, top + zh + 5));
            FormattedText l1 = Gfx.Text(_zLab[1] + " \u00B5s", 8, T.Mute, 0, dpi);
            double lx = PadL + cw - l1.Width;
            if (lx < PadL) lx = PadL;
            dc.DrawText(l1, new Point(lx, top + zh + 5));

            // 放大带上的分位线（P99 可能落在窗口外，夹到边缘）
            _markVal[0] = _p50; _markVal[1] = _p95; _markVal[2] = _p99; _markVal[3] = _mean;
            for (int i = 0; i < 4; i++)
            {
                double t = (_markVal[i] - _zlo) / _zspan;
                if (t < 0 || t > 1)
                {
                    // 落在放大窗口外：只在边缘留一小段同色记号。
                    // 画满高会让人以为这就是它的位置 —— 那条线的意义就没了。
                    double ex = t < 0 ? PadL + 0.5 : PadL + cw - 0.5;
                    dc.DrawLine(_markPen[i], new Point(ex, top + 2), new Point(ex, top + 9));
                    continue;
                }
                double x = Math.Round(PadL + t * cw) + 0.5;
                dc.DrawLine(_markPen[i], new Point(x, top), new Point(x, top + zh));
            }
        }

        private void DrawMarkers(DrawingContext dc, double dpi, double w, double cw, double baseY, double mainH)
        {
            _markVal[0] = _p50;
            _markVal[1] = _p95;
            _markVal[2] = _p99;
            _markVal[3] = _mean;

            for (int i = 0; i < 4; i++)
            {
                _markX[i] = PadL + Pos(_markVal[i]) * cw;
                _order[i] = i;
            }

            // 按 x 排序（4 个元素的插入排序，无分配）：标签从左往右摆，才谈得上避让。
            for (int i = 1; i < 4; i++)
            {
                int k = _order[i];
                double kx = _markX[k];
                int j = i - 1;
                while (j >= 0 && _markX[_order[j]] > kx)
                {
                    _order[j + 1] = _order[j];
                    j--;
                }
                _order[j + 1] = k;
            }

            double row0 = -1e9;
            double row1 = -1e9;
            double row2 = -1e9;

            for (int oi = 0; oi < 4; oi++)
            {
                int i = _order[oi];
                double x = Math.Round(_markX[i]) + 0.5;
                Pen pen = _markPen[i];

                dc.DrawLine(pen, new Point(x, PadT), new Point(x, baseY));
                dc.DrawLine(pen, new Point(x - 3, PadT), new Point(x + 3, PadT));

                FormattedText ft = Gfx.Text(_markLab[i], 8, _markCol[i], 0, dpi);
                double lx = x - ft.Width / 2;
                if (lx < 2) lx = 2;
                if (lx + ft.Width > w - 2) lx = w - 2 - ft.Width;

                // 两行避让：四个标签常常挤在同一段（P95/P99/MEAN 的间距可能小于字宽）。
                double ly;
                if (lx > row0 + 6)
                {
                    row0 = lx + ft.Width;
                    ly = 0;
                }
                else if (lx > row1 + 6)
                {
                    row1 = lx + ft.Width;
                    ly = 11;
                }
                else if (lx > row2 + 6)
                {
                    row2 = lx + ft.Width;
                    ly = 21;
                }
                else
                {
                    double minRight = Math.Min(row0, Math.Min(row1, row2));
                    double nx = minRight + 6;
                    double maxW = ActualWidth > 0 ? ActualWidth : 640;
                    if (nx + ft.Width <= maxW - 2) lx = nx;
                    if (minRight == row0) { row0 = lx + ft.Width; ly = 0; }
                    else if (minRight == row1) { row1 = lx + ft.Width; ly = 11; }
                    else { row2 = lx + ft.Width; ly = 21; }
                }

                if (i == 2)
                {
                    // P99 主指标专属发光琥珀胶囊标签
                    double pw = ft.Width + 10, ph = ft.Height + 2;
                    double px = lx - 5;
                    if (px < 2) px = 2;
                    if (px + pw > w - 2) px = w - 2 - pw;
                    dc.DrawRoundedRectangle(T.S(0xE8241908), Gfx.P(0x99FFB13C, 1), new Rect(px, Math.Max(0, ly - 1), pw, ph), ph * 0.5, ph * 0.5);
                    dc.DrawText(ft, new Point(px + 5, Math.Max(0, ly)));
                }
                else
                {
                    dc.DrawText(ft, new Point(lx, ly));
                }
            }
        }
    }
}