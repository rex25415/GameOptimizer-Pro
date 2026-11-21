// ============================================================================
//  Scheduler.Kit.cs — 03 调度与定时器：本页私有控件（读数头 / 槽 / 徽标 / 掩码尺 / 硅片矩阵）
//
//  为什么单独一个文件：这些东西只服务这一页，放进 Ui.Kit（全应用共享层）
//  会把"调度页的排版决定"变成公共 API；而它们又都不是三五行能写完的
//  FrameworkElement，留在页面文件里会把 Build 逻辑淹掉。
//
//  Rev86：矩阵从"一条负载柱 + 一层斜线"改成三种可命名的状态——
//    未选中（不在前台掩码里：压暗 + 斜线）
//    选中（在掩码里但此刻空闲：强调色极低的底色，仍然"属于前台"）
//    被占用（有负载：强调色柱 + 液面亮帽）
//  三态都只用强调色与灰阶：负载高不是"系统坏了"，语义色（Ok/Warn/Bad）留给结论，不留给忙闲。
//
//  C# 5 / 无 XAML：全部手工绘制，OnRender 内零几何分配（斜线用线段裁剪计算，
//  不用 PushClip + RectangleGeometry —— 后者每帧 32 个几何对象）。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Scheduler
    {
        // ---- 读数：数字 + 单位（单位缩到 34%）--------------------------------
        // Ui.UnitNum 是静态文案一次成型的版本，这里需要之后只改数字，
        // 所以把 Run 交出去，调用方持有它就能零重建地改读数。
        private static TextBlock Number(string val, string unit, double size, uint col, out Run valueRun)
        {
            TextBlock t = new TextBlock();
            t.FontFamily = T.FDisplay;
            t.FontWeight = FontWeights.SemiBold;
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
            t.VerticalAlignment = VerticalAlignment.Center;

            valueRun = new Run(val);
            valueRun.FontSize = size;
            valueRun.Foreground = T.S(col);
            t.Inlines.Add(valueRun);

            if (!string.IsNullOrEmpty(unit))
            {
                Run u = new Run(unit);
                u.FontSize = Math.Round(size * 0.34);
                u.FontFamily = T.FMono;
                u.Foreground = T.S(T.Mute);
                t.Inlines.Add(u);
            }
            return t;
        }

        /// <summary>
        /// 读数头：数值 · 单位 · 变化量挤在同一行。
        /// 变化量另起一行会把整条读数带撑高 21px；仪表的惯例是"读数旁边挂一个偏差"，
        /// 不是"读数下面再起一行"。
        /// </summary>
        private static Grid Head(TextBlock val, string unit, DeltaChip chip)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.HorizontalAlignment = HorizontalAlignment.Left;
            // 读数头固定 42px：四格里有三格是 32 号数字、一格是十六进制（窄窗降档），
            // 高度不一的话，四行注脚就会落在四条不同的基线上。
            g.Height = 42;
            g.Children.Add(val);

            if (!string.IsNullOrEmpty(unit))
            {
                TextBlock u = Ui.Mono(unit, T.FsNano, T.Dim);
                u.VerticalAlignment = VerticalAlignment.Bottom;
                u.Margin = new Thickness(6, 0, 0, 5);
                u.IsHitTestVisible = false;
                Grid.SetColumn(u, 1);
                g.Children.Add(u);
            }
            if (chip != null)
            {
                chip.Margin = new Thickness(10, 0, 0, 8);
                chip.VerticalAlignment = VerticalAlignment.Bottom;
                Grid.SetColumn(chip, 2);
                g.Children.Add(chip);
            }
            return g;
        }

        /// <summary>
        /// 中段槽：固定 16px 高、内容压底。
        /// 读数带四格的中段是四种不同的东西（趋势带 / 掩码尺 / 量条 / 状态条），
        /// 高度不一样的话，四行注脚就会落在四条不同的基线上——那正是"四格"散架的来源。
        /// 槽把"中段多高"从内容里拿出来，交给版面统一决定。
        /// </summary>
        private static Grid Slot(UIElement child)
        {
            Grid g = new Grid();
            g.Height = 16;
            g.IsHitTestVisible = false;
            if (child != null)
            {
                FrameworkElement fe = child as FrameworkElement;
                if (fe != null) fe.VerticalAlignment = VerticalAlignment.Bottom;
                g.Children.Add(child);
            }
            return g;
        }

        // ---- 迷你条：DPC / ISR 开销 ------------------------------------------
        private sealed class MiniBar : FrameworkElement
        {
            private double _v;
            private bool _known;
            private readonly double _scale;

            public MiniBar(double scale)
            {
                _scale = scale <= 0 ? 1 : scale;
                IsHitTestVisible = false;
                SnapsToDevicePixels = true;
            }

            /// <summary>value 是原始读数（与 scale 同量纲）。known=false 时只画轨道，不画填充。</summary>
            public void Set(double value, bool known)
            {
                double v = value / _scale;
                if (v < 0) v = 0; else if (v > 1) v = 1;
                if (_known == known && Math.Abs(_v - v) < 0.0005) return;
                _v = v;
                _known = known;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w < 6 || h < 2) return;
                dc.DrawRectangle(T.S(0x0EFFFFFF), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(T.S(0x18FFFFFF), null, new Rect(0, h - 1, w, 1));
                if (!_known || _v <= 0) return;
                double fw = Math.Max(1, w * _v);
                dc.DrawRectangle(T.S(T.AccentA(0xCC)), null, new Rect(0, 0, fw, h));
                if (fw < w - 1)
                    dc.DrawRectangle(T.S(T.AccentA(0xF0)), null, new Rect(fw - 1, 0, 1, h));
            }
        }

        // ---- 结论徽标（可原位改字与配色）------------------------------------
        // Ui.Tag 是一次成型的 Border：掩码状态、审计结论每 5 秒可能换一次说法，
        // 每次都重建一枚 Tag 等于每 5 秒往视觉树里塞一批短命对象。
        // 这里把内部 TextBlock 留在自己手里，换状态只改字与两支画刷。
        private sealed class Badge : Border
        {
            private readonly TextBlock _label;

            public Badge(string text, uint tint)
            {
                CornerRadius = new CornerRadius(3);
                BorderThickness = new Thickness(1);
                Padding = new Thickness(5, 0, 5, 0);
                VerticalAlignment = VerticalAlignment.Center;
                HorizontalAlignment = HorizontalAlignment.Left;
                IsHitTestVisible = false;

                _label = new TextBlock();
                _label.FontSize = T.FsNano;
                _label.FontFamily = T.FDisplayReg;
                _label.VerticalAlignment = VerticalAlignment.Center;
                Child = _label;

                Set(text, tint);
            }

            public void Set(string text, uint tint)
            {
                _label.Text = Ui.Spaced(text);
                _label.Foreground = T.S(tint);
                BorderBrush = T.S(Ui.Wash(tint, 0x55));
                Background = T.S(Ui.Wash(tint, 0x16));
            }
        }

        // ---- 图例色块：选中 / 被占用 / 未选中 ---------------------------------
        // 三块色板就是矩阵的三种状态，尺寸与矩阵竖槽的顶部一致（11px）。
        private static FrameworkElement Swatch(int state)
        {
            Grid g = new Grid();
            g.Width = 11; g.Height = 11;
            g.VerticalAlignment = VerticalAlignment.Center;
            g.Margin = new Thickness(0, 0, 5, 0);

            Border baseB = new Border();
            baseB.CornerRadius = new CornerRadius(2);
            baseB.BorderThickness = new Thickness(1);
            baseB.BorderBrush = T.S(0x26FFFFFF);
            baseB.SnapsToDevicePixels = true;
            baseB.Background = T.S(state == 0 ? Ui.Wash(T.Accent, 0x1E) : (state == 1 ? T.AccentA(0xC0) : 0x0A0E14));
            g.Children.Add(baseB);

            if (state == 2)
            {
                Border hatch = new Border();
                hatch.CornerRadius = new CornerRadius(2);
                hatch.Background = HatchBrush();
                g.Children.Add(hatch);
            }
            else
            {
                // 选中态给一道 1px 亮帽，和矩阵里的竖槽是同一个语言
                Border cap = new Border();
                cap.Height = 1;
                cap.VerticalAlignment = VerticalAlignment.Top;
                cap.Margin = new Thickness(1, 1, 1, 0);
                cap.Background = T.S(T.AccentA(0xFF));
                g.Children.Add(cap);
            }
            return g;
        }

        private static DrawingBrush _hatch;

        /// <summary>4px 一格的斜线填充：排除态的"打斜线"在画布与图例上是同一张笔刷。</summary>
        private static DrawingBrush HatchBrush()
        {
            if (_hatch != null) return _hatch;

            GeometryGroup lines = new GeometryGroup();
            lines.Children.Add(new LineGeometry(new Point(-2, 2), new Point(2, -2)));
            DrawingGroup dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(null, T.P(0x66FFFFFF, 1), lines));
            dg.Freeze();

            DrawingBrush b = new DrawingBrush(dg);
            b.TileMode = TileMode.Tile;
            b.Viewport = new Rect(0, 0, 4, 4);
            b.ViewportUnits = BrushMappingMode.Absolute;
            b.Viewbox = new Rect(0, 0, 4, 4);
            b.ViewboxUnits = BrushMappingMode.Absolute;
            b.Stretch = Stretch.None;
            b.Freeze();
            _hatch = b;
            return b;
        }

        // ---- 掩码尺：读数带里的 1 位 1 格 ------------------------------------
        /// <summary>
        /// 把亲和掩码画成一条 32/64 格的位尺：亮格 = 允许，暗格 = 排除。
        /// 读数带上如果只写"0xFFFFFFFF"，读者还得自己在脑子里拆位；
        /// 位尺回答的是同一个问题的另一半——"这串十六进制到底长什么样"。
        /// </summary>
        private sealed class MaskStrip : FrameworkElement
        {
            private readonly SolidColorBrush _on = T.S(T.AccentA(0xC8));
            private readonly SolidColorBrush _off = T.S(0x1AFFFFFF);
            private long _mask;
            private bool _known;
            private int _n = 32;

            public MaskStrip() { IsHitTestVisible = false; SnapsToDevicePixels = true; Height = 9; }

            public void Set(long mask, bool known, int n)
            {
                if (n < 1) n = 1;
                if (_mask == mask && _known == known && _n == n) return;
                _mask = mask;
                _known = known;
                _n = n;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w < 8 || h < 2) return;
                double step = w / _n;
                double bw = Math.Max(1, step - 1.5);
                for (int i = 0; i < _n; i++)
                {
                    bool on = _known && i < Affinity.MaxBits && ((_mask >> i) & 1L) != 0;
                    dc.DrawRectangle(on ? _on : _off, null, new Rect(i * step, 0, bw, h));
                }
            }
        }

        // ---- 硅片矩阵 --------------------------------------------------------
        /// <summary>
        /// 每个逻辑处理器一条竖槽：凹槽 + 自下而上的负载 + 液面亮帽。
        /// 亲和性掩码直接画在这里，而且**先是状态、后是负载**：
        ///   未选中 → 压暗 + 斜线（这台处理器此刻不属于前台进程）
        ///   选中   → 强调色极低的底色（属于前台，但此刻空闲）
        ///   被占用 → 上面的底色再叠负载柱 + 亮帽
        /// 读者先看出"哪几台在用"，再看"用得多满"——顺序不能反。
        ///
        /// 与 Widgets/CoreViews.CoreMatrix 的区别（也是不复用它的原因）：
        ///   · CoreMatrix 只有负载；这里要叠加三态掩码；
        ///   · 这里簇间是物理间隙 + 括号标注，不是一条虚线分隔；
        ///   · 这里所有几何按窗口宽度重算，960px 下也不会被右栏挤爆。
        /// </summary>
        private sealed class SchedMatrix : FrameworkElement
        {
            private readonly double[] _load = new double[128];
            private long _mask;
            private bool _maskKnown;

            public int Ccds = 2;
            public int CoresPerCcd = 8;
            public int ThreadsPerCore = 2;
            public double ClusterGap = 26;

            public event Action<int> CoreClicked;
            public event Action<int> ClusterClicked;

            private int _hoverIdx = -1;
            private int _hoverCluster = -1;
            private double _hoverSlotX, _hoverSlotW;

            private readonly SolidColorBrush _track = T.S(0xFF0A1120);
            private readonly Pen _trackEdge = Gfx.P(0x1ECFE6FF, 1);
            private readonly SolidColorBrush _selWash = T.S(Ui.Wash(T.Accent, 0x16));
            private readonly Pen _selEdge = Gfx.P(T.AccentA(0x4C), 1);
            private readonly SolidColorBrush _scrim = T.S(0xBC050914);
            private readonly Pen _hatch = Gfx.P(0x22CFE6FF, 1);
            private readonly Pen _bracket = Gfx.P(T.AccentA(0x52), 1.2);
            private readonly Pen _tick = Gfx.P(0x56CFE6FF, 1);
            private readonly Pen _topRim = Gfx.P(0x1CCFE6FF, 1);
            private readonly Pen _capAccent = Gfx.P(T.Accent, 1.8);
            private readonly SolidColorBrush[] _bars = BuildBars();

            /// <summary>
            /// 负载填充的 9 档：同一强调色，仅透明度随负载上浮（0x66 → 0xE6）。
            /// 不用"冰蓝→琥珀→红"的热力色阶——那三档在本设计语言里是
            /// "优化到位 / 偏离 / 危险"的语义色，而一个核跑得满不等于系统出了问题。
            /// 负载由高度表达，颜色只负责让柱体看得见。
            /// 实例级构建（不是 static）：换主题时页面重建，才拿得到新的强调色。
            /// </summary>
            private static SolidColorBrush[] BuildBars()
            {
                SolidColorBrush[] b = new SolidColorBrush[9];
                for (int i = 0; i < 9; i++)
                {
                    byte a = (byte)(0x66 + (0xE6 - 0x66) * i / 8);
                    b[i] = new SolidColorBrush(T.Alpha(T.Accent, a));
                    b[i].Freeze();
                }
                return b;
            }

            public SchedMatrix()
            {
                IsHitTestVisible = true;
                Cursor = Cursors.Hand;
                SnapsToDevicePixels = true;
                MouseMove += OnMatrixMove;
                MouseLeave += delegate
                {
                    if (_hoverIdx >= 0 || _hoverCluster >= 0)
                    {
                        _hoverIdx = -1;
                        _hoverCluster = -1;
                        InvalidateVisual();
                    }
                };
                MouseLeftButtonUp += OnMatrixClick;
            }

            private bool HitTestSlot(Point p, out int hitIdx, out int hitCluster, out double slotX, out double slotW)
            {
                hitIdx = -1;
                hitCluster = -1;
                slotX = 0;
                slotW = 0;
                double w = ActualWidth, h = ActualHeight;
                if (w < 80 || h < 60) return false;

                int ccds = Math.Max(1, Ccds);
                int cores = Math.Max(1, CoresPerCcd);
                int threads = Math.Max(1, ThreadsPerCore);
                const double labelH = 30;
                double plotH = Math.Max(24, h - labelH);

                double clusterW = (w - ClusterGap * (ccds - 1)) / ccds;
                double coreGap = cores > 12 ? 4 : 5;
                double coreW = (clusterW - coreGap * (cores - 1)) / cores;
                double slotGap = threads > 1 ? 2.5 : 0;
                double sw = (coreW - slotGap * (threads - 1)) / threads;
                if (sw < 2) return false;

                for (int cc = 0; cc < ccds; cc++)
                {
                    double cx = cc * (clusterW + ClusterGap);
                    if (p.X < cx || p.X > cx + clusterW) continue;
                    if (p.Y > plotH)
                    {
                        hitCluster = cc;
                        return true;
                    }
                    for (int c = 0; c < cores; c++)
                    {
                        double coreX = cx + c * (coreW + coreGap);
                        for (int t = 0; t < threads; t++)
                        {
                            double sx = coreX + t * (sw + slotGap);
                            if (p.X >= sx - 1 && p.X <= sx + sw + 1 && p.Y >= 0 && p.Y <= plotH)
                            {
                                hitIdx = (cc * cores + c) * threads + t;
                                slotX = sx;
                                slotW = sw;
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            private void OnMatrixMove(object s, MouseEventArgs e)
            {
                int idx, cl;
                double sx, sw;
                HitTestSlot(e.GetPosition(this), out idx, out cl, out sx, out sw);
                if (idx != _hoverIdx || cl != _hoverCluster)
                {
                    _hoverIdx = idx;
                    _hoverCluster = cl;
                    _hoverSlotX = sx;
                    _hoverSlotW = sw;
                    InvalidateVisual();
                }
            }

            private void OnMatrixClick(object s, MouseButtonEventArgs e)
            {
                int idx, cl;
                double sx, sw;
                if (!HitTestSlot(e.GetPosition(this), out idx, out cl, out sx, out sw)) return;
                if (idx >= 0 && CoreClicked != null) CoreClicked(idx);
                else if (cl >= 0 && ClusterClicked != null) ClusterClicked(cl);
            }

            public void SetLoad(double[] src)
            {
                if (src == null) return;
                int n = Math.Min(src.Length, _load.Length);
                Array.Copy(src, _load, n);
                if (n < _load.Length) Array.Clear(_load, n, _load.Length - n);
                InvalidateVisual();
            }

            /// <summary>known=false 表示掩码没测到：这时一条斜线都不画（宁可空着，也不画错）。</summary>
            public void SetMask(long mask, bool known)
            {
                if (_maskKnown == known && _mask == mask) return;
                _mask = mask;
                _maskKnown = known;
                InvalidateVisual();
            }

            private bool Allowed(int idx)
            {
                if (!_maskKnown) return true;
                if (idx < 0 || idx >= Affinity.MaxBits) return true;
                return ((_mask >> idx) & 1L) != 0;
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w < 80 || h < 60) return;

                int ccds = Math.Max(1, Ccds);
                int cores = Math.Max(1, CoresPerCcd);
                int threads = Math.Max(1, ThreadsPerCore);

                const double labelH = 30;
                double plotH = Math.Max(24, h - labelH);

                double clusterW = (w - ClusterGap * (ccds - 1)) / ccds;
                double coreGap = cores > 12 ? 4 : 5;
                double coreW = (clusterW - coreGap * (cores - 1)) / cores;
                double slotGap = threads > 1 ? 2.5 : 0;
                double slotW = (coreW - slotGap * (threads - 1)) / threads;
                if (slotW < 2) return;

                dc.DrawLine(_topRim, new Point(0, 0.5), new Point(w, 0.5));

                for (int cc = 0; cc < ccds; cc++)
                {
                    double cx = cc * (clusterW + ClusterGap);
                    for (int c = 0; c < cores; c++)
                    {
                        double coreX = cx + c * (coreW + coreGap);
                        for (int t = 0; t < threads; t++)
                        {
                            int idx = (cc * cores + c) * threads + t;
                            DrawSlot(dc, coreX + t * (slotW + slotGap), slotW, plotH, idx);
                        }
                    }
                    DrawBracket(dc, cx, clusterW, plotH, cc, cores, threads);
                }

                // 鼠标悬停时渲染单核 HUD 浮标（未悬停零绘制、零分配）
                if (_hoverIdx >= 0)
                {
                    double dpi = Gfx.Dpi(this);
                    double lv = _hoverIdx < _load.Length ? Math.Max(0, Math.Min(1, _load[_hoverIdx])) : 0;
                    int perCcd = cores * threads;
                    int hCcd = perCcd > 0 ? (_hoverIdx / perCcd) : 0;
                    int hCore = (_hoverIdx / Math.Max(1, threads)) + 1;
                    bool isSmt = threads > 1 && (_hoverIdx % threads) != 0;
                    string stateStr = !_maskKnown ? "未测量" : (Allowed(_hoverIdx) ? "已允许 · 点此排除" : "已排除 · 点此启用");
                    string tag = "T" + _hoverIdx + " · " + CoreText.Ccd(hCcd) + " C" + hCore + (isSmt ? " SMT" : " 主核")
                               + " · " + ((int)Math.Round(lv * 100)) + "% · " + stateStr;
                    FormattedText ft = Gfx.Text(tag, T.FsNano, T.Hi, 0, dpi);
                    double bw = ft.Width + 14;
                    double bh = ft.Height + 6;
                    double bx = _hoverSlotX + _hoverSlotW * 0.5 - bw * 0.5;
                    if (bx < 2) bx = 2;
                    if (bx + bw > w - 2) bx = Math.Max(2, w - bw - 2);
                    double by = 6;
                    dc.DrawRoundedRectangle(T.S(0xF40B1119), Gfx.P(T.AccentA(0x88), 1), new Rect(bx, by, bw, bh), 4, 4);
                    dc.DrawRectangle(T.S(T.Accent), null, new Rect(bx + 1, by + 3, 2, bh - 6));
                    dc.DrawText(ft, new Point(bx + 8, by + 3));
                }
            }

            private void DrawSlot(DrawingContext dc, double x, double w, double plotH, int idx)
            {
                Rect r = new Rect(x, 2, w, plotH - 4);
                bool isHover = idx == _hoverIdx;
                dc.DrawRoundedRectangle(_track, isHover ? _capAccent : _trackEdge, r, 1.5, 1.5);
                // 槽口：每条通道顶端都有一道刻线，空槽因此仍然"存在"。
                dc.DrawLine(_topRim, new Point(r.X + 1, 3), new Point(r.Right - 1, 3));

                // 未选中：先压暗（真实负载仍隐约可见，读数不被掩盖），再打斜线。
                if (!Allowed(idx))
                {
                    dc.DrawRoundedRectangle(_scrim, isHover ? _capAccent : null, r, 1.5, 1.5);
                    Hatch(dc, r);
                    return;
                }

                // 选中：底色抬到强调色的极低透明度。空载槽因此也能被读成"属于前台"。
                dc.DrawRoundedRectangle(_selWash, isHover ? _capAccent : _selEdge, r, 1.5, 1.5);

                double load = (idx >= 0 && idx < _load.Length) ? _load[idx] : 0;
                if (load < 0) load = 0; else if (load > 1) load = 1;
                if (load <= 0.004) return;

                // 被占用：负载柱 + 液面亮帽。竖槽因此有"读数"，而不是一团从底部涨起的色块。
                double bh = Math.Max(2, (plotH - 6) * load);
                Rect fill = new Rect(r.X + 0.6, r.Bottom - 1.2 - bh, r.Width - 1.2, bh);

                int step = (int)Math.Round(load * 8);
                if (step < 0) step = 0; else if (step > 8) step = 8;
                dc.DrawRoundedRectangle(_bars[step], null, fill, 1.2, 1.2);
                dc.DrawLine(_capAccent, new Point(fill.X, fill.Top + 0.5), new Point(fill.Right, fill.Top + 0.5));
            }

            /// <summary>45° 斜线，按矩形裁剪。纯线段计算，不建几何、不 PushClip。</summary>
            private void Hatch(DrawingContext dc, Rect r)
            {
                const double step = 5.0;
                double b0 = r.Top - r.Right;   // y = x + b
                double b1 = r.Bottom - r.Left;
                for (double b = b0; b <= b1; b += step)
                {
                    double xa = Math.Max(r.Left, r.Top - b);
                    double xb = Math.Min(r.Right, r.Bottom - b);
                    if (xb - xa < 0.6) continue;
                    dc.DrawLine(_hatch, new Point(xa, xa + b), new Point(xb, xb + b));
                }
            }

            private void DrawBracket(DrawingContext dc, double cx, double cw, double plotH, int cc, int cores, int threads)
            {
                double y = plotH + 8;
                double x0 = cx + 2, x1 = cx + cw - 2;
                if (x1 - x0 < 10) return;

                dc.DrawLine(_bracket, new Point(x0, y + 0.5), new Point(x1, y + 0.5));
                dc.DrawLine(_tick, new Point(x0 + 0.5, y + 0.5), new Point(x0 + 0.5, y - 3.5));
                dc.DrawLine(_tick, new Point(x1 - 0.5, y + 0.5), new Point(x1 - 0.5, y - 3.5));

                double dpi = Gfx.Dpi(this);
                FormattedText name = Gfx.Text(CoreText.Ccd(cc), T.FsNano, T.Dim, 0, dpi);
                dc.DrawText(name, new Point(cx, y + 4));

                string spec = cores + "C/" + (cores * threads) + "T";
                FormattedText sp = Gfx.Text(spec, T.FsNano, T.Mute, 0, dpi);
                dc.DrawText(sp, new Point(cx + cw - sp.Width, y + 4));
            }
        }
    }
}
