// ============================================================================
//  CoreViews.cs — 核心矩阵（等化器）/ 核心拓扑（CCD 行）
//  零分配：负载色走 33 档热力 LUT，标签文本走 Gfx.Text 缓存，
//          Load 属性接收外部数组时复制进自有缓冲，避免与采样缓冲竞争。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Go
{
    /// <summary>物理核心 × 每核线程的等化器视图。</summary>
    public class CoreMatrix : FrameworkElement
    {
        private readonly double[] _load = new double[128];
        public int Ccds = 2;
        public int CoresPerCcd = 8;
        public int ThreadsPerCore = 2;
        public ulong AffinityMask;
        public bool ShowMask;

        public void SetAffinityMask(ulong mask, bool show)
        {
            if (AffinityMask == mask && ShowMask == show) return;
            AffinityMask = mask;
            ShowMask = show;
            InvalidateVisual();
        }

        public void SetAffinityMask(long mask, bool show)
        {
            SetAffinityMask(unchecked((ulong)mask), show);
        }

        /// <summary>写入负载（复制进自有缓冲；调用方无需再 InvalidateVisual）。</summary>
        public double[] Load
        {
            get { return _load; }
            set
            {
                if (value == null) return;
                int n = Math.Min(value.Length, _load.Length);
                Array.Copy(value, _load, n);
                if (n < _load.Length) Array.Clear(_load, n, _load.Length - n);
                InvalidateVisual();
            }
        }

        // 静态建一次+不订阅主题 = 换主题后柱体还是旧色（外壳变了、数据没变）。
        // 主题切换是低频动作，重建这 6 支冻结画刷的代价可以忽略。
        private static SolidColorBrush[] BarBrushes = BuildBars();
        static CoreMatrix()
        {
            T.ThemeChanged += delegate { BarBrushes = BuildBars(); };
        }

        private static SolidColorBrush[] BuildBars()
        {
            uint[] tint = new uint[] { T.Accent, T.Warn, T.Bad };
            SolidColorBrush[] b = new SolidColorBrush[6];
            for (int i = 0; i < 3; i++)
            {
                Color c190 = T.Alpha(tint[i], 190);
                b[i * 2] = new SolidColorBrush(c190);
                b[i * 2].Freeze();
                Color c235 = T.Alpha(tint[i], 235);
                b[i * 2 + 1] = new SolidColorBrush(c235);
                b[i * 2 + 1].Freeze();
            }
            return b;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 60 || h < 40) return;

            const double labelH = 13;
            bool showCluster = Ccds > 1 && h >= 58;
            double headH = showCluster ? 14 : 0;
            double plotTop = headH;
            double plotH = h - plotTop - labelH - 2;
            if (plotH < 10) return;

            const double groupGap = 20, coreGap = 5;
            int totalCores = Ccds * CoresPerCcd;
            if (totalCores <= 0) return;
            double totalGap = coreGap * (totalCores - 1) + groupGap * (Ccds - 1);
            double colW = (w - totalGap) / totalCores;
            double subW = Math.Max(2, (colW - 3) / ThreadsPerCore);

            Pen track = Gfx.P(0x0EFFFFFF, 1);
            Pen trackDim = Gfx.P(0x07FFFFFF, 1);
            Pen maskEdge = Gfx.P(T.AccentA(0xB8), 1);
            SolidColorBrush trackFill = T.S(T.LineFaint);
            double dpi = Gfx.Dpi(this);
            bool showLabels = colW > 13;

            double x = 0;
            for (int ccd = 0; ccd < Ccds; ccd++)
            {
                double ccdStartX = x;
                double ccdW = CoresPerCcd * colW + Math.Max(0, CoresPerCcd - 1) * coreGap;

                // 计算该 CCD 均值用于簇头均值条 + 百分比展示（字符串查表零分配）
                double sumLoad = 0;
                int nThreads = CoresPerCcd * ThreadsPerCore;
                for (int i = 0; i < nThreads; i++)
                {
                    int idx = ccd * nThreads + i;
                    double lv = idx < _load.Length ? Math.Max(0, Math.Min(1, _load[idx])) : 0;
                    sumLoad += lv;
                }
                double ccdAvg = nThreads > 0 ? sumLoad / nThreads : 0;

                if (showCluster)
                {
                    FormattedText ccdFt = Gfx.Text(CoreText.Ccd(ccd), T.FsNano, T.Dim, 0, dpi);
                    dc.DrawText(ccdFt, new Point(ccdStartX, 0));

                    double barX = ccdStartX + ccdFt.Width + 6;
                    FormattedText pctFt = Gfx.Text(CoreText.Pct5(ccdAvg), T.FsNano, T.Mute, 0, dpi);
                    double pctX = ccdStartX + ccdW - pctFt.Width;
                    double barW = Math.Max(12, pctX - barX - 6);
                    if (barW >= 12)
                    {
                        Rect avgTrack = new Rect(barX, 4.5, barW, 3);
                        dc.DrawRoundedRectangle(T.S(0x10FFFFFF), null, avgTrack, 1.5, 1.5);
                        if (ccdAvg > 0.004)
                        {
                            double fw = Math.Max(2, barW * ccdAvg);
                            dc.DrawRoundedRectangle(BarBrushes[ccdAvg > 0.72 ? 3 : 0], null,
                                new Rect(barX, 4.5, fw, 3), 1.5, 1.5);
                        }
                    }
                    if (pctX > ccdStartX + ccdFt.Width + 2)
                        dc.DrawText(pctFt, new Point(pctX, 0));
                    dc.DrawLine(Gfx.P(T.LineSoft, 1), new Point(ccdStartX, 11.5), new Point(ccdStartX + ccdW, 11.5));
                }

                for (int c = 0; c < CoresPerCcd; c++)
                {
                    int coreIdx = ccd * CoresPerCcd + c;
                    for (int t = 0; t < ThreadsPerCore; t++)
                    {
                        int idx = coreIdx * ThreadsPerCore + t;
                        double load = idx < _load.Length ? Math.Max(0, Math.Min(1, _load[idx])) : 0;
                        double sx = x + t * (subW + 3);
                        bool maskedOut = ShowMask && AffinityMask != 0 && idx < 64 && ((AffinityMask >> idx) & 1UL) == 0;
                        bool maskedIn = ShowMask && AffinityMask != 0 && idx < 64 && ((AffinityMask >> idx) & 1UL) != 0;

                        Rect cell = new Rect(sx, plotTop + 0.5, subW, plotH - 1);
                        dc.DrawRoundedRectangle(trackFill, maskedIn ? maskEdge : (maskedOut ? trackDim : track), cell, 1.5, 1.5);

                        if (maskedIn && subW >= 4)
                        {
                            dc.DrawLine(Gfx.P(T.Accent, 1.5), new Point(sx + 1, plotTop + 1.5), new Point(sx + subW - 1, plotTop + 1.5));
                        }

                        if (load > 0.005)
                        {
                            double bh = Math.Max(1.5, (plotH - 3) * load);
                            int tier = load > 0.92 ? 2 : load > 0.72 ? 1 : 0;
                            Rect fill = new Rect(sx + 0.5, plotTop + plotH - 1.5 - bh, subW - 1, bh);
                            dc.DrawRoundedRectangle(BarBrushes[tier * 2 + (load > 0.72 ? 1 : 0)], null, fill, 1.5, 1.5);

                            // 液面盖：一条 1px 亮线，零tier跟随当前强调色主题。
                            Pen cap = Gfx.P(tier == 2 ? 0xE6FF5C63 : (tier == 1 ? 0xE6FFB13C : T.AccentA(0xE6)), 1);
                            dc.DrawLine(cap, new Point(fill.X, fill.Top + 0.5), new Point(fill.Right, fill.Top + 0.5));
                        }
                    }

                    if (showLabels)
                    {
                        FormattedText ft = Gfx.Text(CoreText.Core(coreIdx + 1), T.FsNano, T.Mute, 0, dpi);
                        dc.DrawText(ft, new Point(x + (colW - ft.Width) / 2, plotTop + plotH + 2));
                    }

                    x += colW + coreGap;
                }
                if (ccd < Ccds - 1)
                {
                    double sepX = x - coreGap / 2 + (groupGap - coreGap) * 0.5;
                    Pen sep = Gfx.Dash(0x22FFFFFF, 1, 2);
                    dc.DrawLine(sep, new Point(sepX, 2), new Point(sepX, plotTop + plotH));
                    x += groupGap - coreGap;
                }
            }
        }
    }

    /// <summary>CCD 行视图：每行一个 CCD，格内底部压力条。</summary>
    /// <summary>
    /// 核/簇标签的一次性建表：之前每次重绘都 ToString()/拼接一次，
    /// 32 核 × 2Hz 就是一串稳定的小垃圾（实测 33.9 B/帧 的全部来源）。
    /// </summary>
    public static class CoreText
    {
        private static readonly string[] _core = Build(128);
        private static readonly string[] _ccd = { "CCD0", "CCD1", "CCD2", "CCD3", "CCD4", "CCD5", "CCD6", "CCD7" };
        private static readonly string[] _pct5 = BuildPct5();

        private static string[] Build(int n)
        {
            string[] a = new string[n];
            for (int i = 0; i < n; i++) a[i] = (i + 1).ToString();
            return a;
        }

        private static string[] BuildPct5()
        {
            string[] a = new string[21];
            for (int i = 0; i < 21; i++) a[i] = (i * 5).ToString() + "%";
            return a;
        }

        public static string Core(int idx)
        {
            return idx >= 1 && idx <= _core.Length ? _core[idx - 1] : idx.ToString();
        }

        public static string Ccd(int c)
        {
            return c >= 0 && c < _ccd.Length ? _ccd[c] : "CCD" + c.ToString();
        }

        /// <summary>按 5% 量化查表，保证活体热路径中簇均值读数零分配。</summary>
        public static string Pct5(double v01)
        {
            int q = (int)Math.Round(Math.Max(0, Math.Min(1, v01)) * 20.0);
            return _pct5[q];
        }
    }

    public class CoreGrid : FrameworkElement
    {
        private readonly double[] _load = new double[128];
        public int Ccds = 2;
        public int CoresPerCcd = 8;
        public int ThreadsPerCore = 2;
        public ulong AffinityMask;
        public bool ShowMask;

        public void SetAffinityMask(ulong mask, bool show)
        {
            if (AffinityMask == mask && ShowMask == show) return;
            AffinityMask = mask;
            ShowMask = show;
            InvalidateVisual();
        }

        public void SetAffinityMask(long mask, bool show)
        {
            SetAffinityMask(unchecked((ulong)mask), show);
        }
        public bool ShowLabels = true;
        public double LabelW = 48;

        private double _hoverX = -1;
        private double _hoverY = -1;

        public CoreGrid()
        {
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                Point p = e.GetPosition(this);
                _hoverX = p.X;
                _hoverY = p.Y;
                InvalidateVisual();
            };
            MouseLeave += delegate
            {
                if (_hoverX >= 0 || _hoverY >= 0)
                {
                    _hoverX = -1;
                    _hoverY = -1;
                    InvalidateVisual();
                }
            };
        }

        public double[] Load
        {
            get { return _load; }
            set
            {
                if (value == null) return;
                int n = Math.Min(value.Length, _load.Length);
                Array.Copy(value, _load, n);
                if (n < _load.Length) Array.Clear(_load, n, _load.Length - n);
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 20) return;
            int rows = Math.Max(1, Ccds);
            const double gapY = 10, gapX = 3;
            double rowH = (h - gapY * (rows - 1)) / rows;
            int cells = CoresPerCcd * ThreadsPerCore;
            if (cells <= 0) return;
            double left = ShowLabels ? LabelW : 0;
            double cellW = (w - left - gapX * (cells - 1)) / cells;
            double dpi = Gfx.Dpi(this);
            Pen edge = Gfx.P(0x20CFE6FF, 1);
            Pen edgeDim = Gfx.P(0x0ECFE6FF, 1);
            Pen maskEdge = Gfx.P(T.AccentA(0xCC), 1.1);

            for (int c = 0; c < rows; c++)
            {
                double y = c * (rowH + gapY);
                double cellH = Math.Max(8, rowH - 12);

                double sumLoad = 0;
                for (int t = 0; t < cells; t++)
                {
                    int idx = c * cells + t;
                    if (idx < _load.Length) sumLoad += Math.Max(0, Math.Min(1, _load[idx]));
                }
                double avg = cells > 0 ? sumLoad / cells : 0;

                if (c > 0)
                {
                    double sepY = y - gapY * 0.5;
                    dc.DrawLine(Gfx.P(T.LineSoft, 1), new Point(0, sepY), new Point(w, sepY));
                }

                if (ShowLabels)
                {
                    FormattedText ft = Gfx.Text(CoreText.Ccd(c), T.FsNano, T.Dim, 0, dpi);
                    double ly = y + Math.Max(0, (cellH - ft.Height - 13) / 2);
                    dc.DrawText(ft, new Point(0, ly));

                    // 左栏簇均值微指示条 + 量化百分比（查表零分配）：直观读出 CCD 级负载差
                    double bw = Math.Max(12, LabelW - 10);
                    double by = ly + ft.Height + 2;
                    if (by + 2.5 <= y + cellH + 2)
                    {
                        dc.DrawRoundedRectangle(T.S(0x14FFFFFF), null, new Rect(0, by, bw, 2.5), 1, 1);
                        if (avg > 0.005)
                        {
                            int avgTier = avg > 0.92 ? 2 : avg > 0.75 ? 1 : 0;
                            dc.DrawRoundedRectangle(Gfx.Heat(avg, avgTier), null,
                                new Rect(0, by, Math.Max(2, bw * avg), 2.5), 1, 1);
                        }
                    }
                    double py = by + 4;
                    if (py + 10 <= y + rowH + 2)
                    {
                        FormattedText pf = Gfx.Text(CoreText.Pct5(avg), 8.5, avg > 0.75 ? T.Warn : T.Mute, 0, dpi);
                        dc.DrawText(pf, new Point(0, py));
                    }
                }

                int hoverCcd = -1, hoverSlot = -1;
                Rect hoverRect = Rect.Empty;
                for (int t = 0; t < cells; t++)
                {
                    int idx = c * cells + t;
                    double load = idx < _load.Length ? Math.Max(0, Math.Min(1, _load[idx])) : 0;
                    double x = left + t * (cellW + gapX);
                    Rect r = new Rect(x, y, cellW, cellH);
                    int tier = load > 0.92 ? 2 : load > 0.75 ? 1 : 0;

                    bool hasMask = ShowMask && AffinityMask != 0 && idx < 64;
                    bool maskedIn = hasMask && ((AffinityMask >> idx) & 1UL) != 0;
                    bool maskedOut = hasMask && !maskedIn;
                    bool isHover = _hoverX >= r.Left && _hoverX <= r.Right && _hoverY >= r.Top && _hoverY <= r.Bottom;
                    if (isHover) { hoverCcd = c; hoverSlot = t; hoverRect = r; }

                    Pen borderPen = isHover ? Gfx.P(T.Accent, 1.4) : (maskedIn ? maskEdge : (maskedOut ? edgeDim : edge));
                    dc.DrawRoundedRectangle(T.S(0xFF0A1120), borderPen, r, 4, 4);

                    if (maskedIn && r.Width >= 4)
                    {
                        dc.DrawLine(Gfx.P(T.Accent, 1.5), new Point(r.X + 3, r.Y + 1.2), new Point(r.Right - 3, r.Y + 1.2));
                    }

                    if (load > 0.004)
                    {
                        double barH = Math.Max(3, (r.Height - 4) * load);
                        Rect fill = new Rect(r.X + 1.5, r.Bottom - 2.0 - barH, r.Width - 3, barH);
                        dc.DrawRoundedRectangle(Gfx.Heat(load, tier), null, fill, 2.5, 2.5);

                        // 发光液面盖：2px 高亮顶边，零档随主题强调色联动
                        Pen cap = Gfx.P(tier == 2 ? 0xFFFF6E75 : (tier == 1 ? 0xFFFFC566 : T.Accent), 1.6);
                        dc.DrawLine(cap, new Point(fill.X + 1, fill.Top + 0.8), new Point(fill.Right - 1, fill.Top + 0.8));
                    }
                }

                // 仅在鼠标悬停到某一格时渲染 HUD 探针浮标（未悬停零开销、零分配）
                if (hoverCcd >= 0 && hoverSlot >= 0)
                {
                    int hIdx = hoverCcd * cells + hoverSlot;
                    double hLoad = hIdx < _load.Length ? Math.Max(0, Math.Min(1, _load[hIdx])) : 0;
                    int coreNum = (hIdx / Math.Max(1, ThreadsPerCore)) + 1;
                    bool isSmt = ThreadsPerCore > 1 && (hIdx % ThreadsPerCore) != 0;
                    string tag = CoreText.Ccd(hoverCcd) + " · C" + coreNum + (isSmt ? " SMT" : " 主核") + " · T" + hIdx + " · " + ((int)Math.Round(hLoad * 100)) + "%";
                    FormattedText ht = Gfx.Text(tag, T.FsNano, T.Hi, 0, dpi);
                    double bw = ht.Width + 14;
                    double bh = ht.Height + 6;
                    double bx = hoverRect.X + hoverRect.Width / 2 - bw / 2;
                    if (bx + bw > w) bx = w - bw;
                    if (bx < left) bx = left;
                    double by = hoverRect.Top - bh - 4;
                    if (by < 0) by = hoverRect.Bottom + 4;
                    if (by + bh > h) by = Math.Max(0, h - bh);
                    dc.DrawRoundedRectangle(T.S(0xF40B1119), Gfx.P(T.AccentA(0x78), 1), new Rect(bx, by, bw, bh), 4, 4);
                    dc.DrawRectangle(T.S(T.Accent), null, new Rect(bx + 1, by + 3, 2, bh - 6));
                    dc.DrawText(ht, new Point(bx + 8, by + 3));
                }
            }
        }
    }
}
