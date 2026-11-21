// ============================================================================
//  Network.Chart.cs — 60 样本双线吞吐图 + 它的一行时间轴（Network 分片）
//
//  为什么不用 Waveform：它的 AutoScale 下限固定在 200（KB/s 语言），换成 Mbps 后
//  量程永远被顶到 200 Mbps，小流量贴地读成 0；而这里需要两条曲线、带单位的 Y 轴
//  与峰值标注。零伪影：没有样本时只画刻度与"待测"，不画任何假曲线。
//
//  Rev86 改动：横轴刻度从图里挪到图下面独立一行（−60 s … NOW），和总览页的
//  波形面板同一套语言；Y 轴补上单位 Mbps——没有量纲的刻度只是装饰线。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Network : Page
    {
        private NetChart _chart;
        private TextBlock _chartMeta, _chartHint;
        private int _chartSig = -1;

        // ---- 面板 -----------------------------------------------------------
        private UIElement BuildChart(Core c)
        {
            StackPanel body = new StackPanel();
            _chartMeta = Ui.Mono("等待采样", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("吞吐走势", "THROUGHPUT · Mbps · 60 SAMPLES", _chartMeta));
            body.Children.Add(Gap(12));

            _chart = new NetChart();
            Grid plot = new Grid();
            plot.Children.Add(Ui.Inset(_chart, 4));

            _chartHint = Ui.Mono("等待首个采样窗口（切到本页即开始）", T.FsNano, T.Mute);
            _chartHint.HorizontalAlignment = HorizontalAlignment.Center;
            _chartHint.VerticalAlignment = VerticalAlignment.Center;
            _chartHint.IsHitTestVisible = false;
            plot.Children.Add(_chartHint);
            body.Children.Add(plot);

            // 时间轴：5 个刻度对应图里 4 条纵向分格线，方向从旧到新（左 → 右）。
            Grid axis = new Grid();
            axis.Margin = new Thickness(0, 7, 0, 0);
            for (int i = 0; i < 5; i++)
            {
                axis.ColumnDefinitions.Add(new ColumnDefinition());
                string lab = i == 0 ? "\u221260 s" : i == 4 ? "NOW" : "\u2212" + (60 - i * 15) + " s";
                TextBlock ax = Ui.Mono(lab, T.FsNano, T.Mute);
                ax.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left
                                      : i == 4 ? HorizontalAlignment.Right
                                      : HorizontalAlignment.Center;
                Grid.SetColumn(ax, i);
                axis.Children.Add(ax);
            }
            body.Children.Add(axis);

            body.Children.Add(Gap(10));
            TextBlock note = Ui.Tx("两条线分别是全部活动接口的下行 / 上行；量程随 60 样本峰值向上取整，刻度单位 Mbps。",
                T.FsNano, T.Mute, T.FCn);
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            note.TextWrapping = TextWrapping.NoWrap;
            body.Children.Add(note);

            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>面板头读数：峰值 / 均值。按整数刻度比较，没变就不重拼。</summary>
        private void PaintChartMeta()
        {
            if (_chartMeta == null || _chart == null) return;
            int n = _chart.Count;
            int sig = n * 31 + ToCs(_chart.PeakRx) * 7 + ToCs(_chart.PeakTx) * 13
                    + ToCs(_chart.AvgRx) * 17 + ToCs(_chart.AvgTx) * 19;
            if (sig == _chartSig) return;
            _chartSig = sig;
            if (n == 0) { _chartMeta.Text = "等待采样"; return; }
            _chartMeta.Text = "峰值 ↓" + FmtMbps(_chart.PeakRx) + " ↑" + FmtMbps(_chart.PeakTx)
                            + " · 均值 ↓" + FmtMbps(_chart.AvgRx) + " ↑" + FmtMbps(_chart.AvgTx) + " Mbps";
        }
    }

    /// <summary>60 槽环形缓冲：最新样本贴右缘，满 60 槽恰好 60 样本。</summary>
    internal sealed class NetChart : FrameworkElement
    {
        private const int Cap = 60;
        private readonly double[] _rx = new double[Cap];
        private readonly double[] _tx = new double[Cap];
        private int _n, _head;
        private double _hoverX = -1;
        private RectangleGeometry _clip;
        private double _clipW, _clipH;
        private StreamGeometry _rxArea, _rxLine, _txLine;

        public double PeakRx, PeakTx, AvgRx, AvgTx;

        public NetChart()
        {
            Height = 224;
            IsHitTestVisible = true;
            SnapsToDevicePixels = true;
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

        public int Count { get { return _n; } }

        public void Reset()
        {
            _n = 0;
            _head = 0;
            PeakRx = PeakTx = AvgRx = AvgTx = 0;
            InvalidateVisual();
        }

        public void Push(double rx, double tx)
        {
            if (rx < 0) rx = 0;
            if (tx < 0) tx = 0;
            _rx[_head] = rx;
            _tx[_head] = tx;
            _head = (_head + 1) % Cap;
            if (_n < Cap) _n++;

            // 峰值/均值自己统计：面板不重复算，读数不会两处不同步。
            double pr = 0, pt = 0, sr = 0, st = 0;
            for (int i = 0; i < _n; i++)
            {
                int idx = BufIndex(i);
                if (_rx[idx] > pr) pr = _rx[idx];
                if (_tx[idx] > pt) pt = _tx[idx];
                sr += _rx[idx];
                st += _tx[idx];
            }
            PeakRx = pr; PeakTx = pt;
            AvgRx = _n == 0 ? 0 : sr / _n;
            AvgTx = _n == 0 ? 0 : st / _n;
            InvalidateVisual();
        }

        private int BufIndex(int i)
        {
            return (_head - _n + i + Cap * 2) % Cap;
        }

        private static uint TintA(uint tint, byte a)
        {
            return ((uint)a << 24) | (tint & 0x00FFFFFFu);
        }

        /// <summary>量程向上取整到 1/2/3/4/5/6/8×10^n 的四等分，刻度才像刻度。</summary>
        private static double NiceTop(double peak)
        {
            double hi = peak * 1.15;
            if (hi < 0.5) hi = 0.5;
            double raw = hi / 4.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            // 只有 1/2/5 三档时，18 Mbps 的峰值会被顶到 40 的量程里，曲线只剩半高。
            double step;
            if (norm <= 1) step = 1;
            else if (norm <= 2) step = 2;
            else if (norm <= 3) step = 3;
            else if (norm <= 4) step = 4;
            else if (norm <= 5) step = 5;
            else if (norm <= 6) step = 6;
            else if (norm <= 8) step = 8;
            else step = 10;
            return step * mag * 4;
        }

        private static string TickText(double v, double step)
        {
            if (step >= 1) return v.ToString("0");
            if (step >= 0.1) return v.ToString("0.0");
            return v.ToString("0.00");
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 60 || h < 40) return;

            // padR 要容得下 "PEAK ↓ 120.4"（峰值标注右栏）；padT 留 24 彻底消除 Y 轴顶刻度与 Mbps 单位重叠。
            double padL = 42, padR = 78, padT = 24, padB = 10;
            double cw = w - padL - padR;
            double ch = h - padT - padB;
            if (cw < 20 || ch < 20) return;
            double dpi = Gfx.Dpi(this);

            // 没有样本时给一个满量程刻度（仪器没信号也有表盘），但绝不画曲线。
            double top = _n == 0 ? 10 : NiceTop(Math.Max(PeakRx, PeakTx));
            double step = top / 4.0;

            Pen grid = Gfx.P(T.LineSoft, 1);
            Pen gridBase = Gfx.P(T.Line, 1);
            Pen gridV = Gfx.P(T.LineFaint, 1);
            Pen tick = Gfx.P(T.LineMid, 1);

            // 横向刻度 + Y 轴读数
            for (int i = 0; i <= 4; i++)
            {
                double y = padT + ch * i / 4.0;
                dc.DrawLine(i == 4 ? gridBase : grid, new Point(padL, y), new Point(w - padR, y));

                FormattedText ft = Gfx.Text(TickText(top - step * i, step), T.FsNano, T.Mute, 0, dpi);
                dc.DrawText(ft, new Point(padL - ft.Width - 7, y - ft.Height / 2));

                // 刻度短齿：让轴读起来是尺子，不是装饰线
                dc.DrawLine(tick, new Point(padL - 4, y), new Point(padL, y));
            }

            // Y 轴量纲：落在绘图区左上角上方 4px，与最顶部数字刻度干净错开绝不重叠
            FormattedText unit = Gfx.Text("Mbps", T.FsNano, T.Dim, 0, dpi);
            dc.DrawText(unit, new Point(padL, 3));

            // 纵向 15 样本分格
            for (int i = 0; i <= 4; i++)
            {
                double x = padL + cw * i / 4.0;
                dc.DrawLine(gridV, new Point(x, padT), new Point(x, padT + ch));
            }

            if (_n == 0) return;

            if (_clip == null || Math.Abs(_clipW - cw) > 0.5 || Math.Abs(_clipH - ch) > 0.5)
            {
                _clip = new RectangleGeometry(new Rect(padL - 1, padT - 1, cw + 2, ch + 2));
                _clip.Freeze();
                _clipW = cw;
                _clipH = ch;
            }
            dc.PushClip(_clip);
            try
            {
                DrawSeries(dc, _rx, T.Accent, top, padL, cw, padT, ch, true);
                DrawSeries(dc, _tx, T.Violet, top, padL, cw, padT, ch, false);
            }
            finally { dc.Pop(); }

            // 最新点：读者一眼定位"现在"在哪
            DrawHead(dc, _rx, T.Accent, top, padL, cw, padT, ch);
            DrawHead(dc, _tx, T.Violet, top, padL, cw, padT, ch);

            // 峰值标注：虚线 + 右栏读数，两条曲线各自一行
            double yRx = padT + ch - (PeakRx / top) * ch;
            double yTx = padT + ch - (PeakTx / top) * ch;
            if (Math.Abs(yRx - yTx) < 13)
            {
                double mid = (yRx + yTx) / 2;
                if (yRx <= yTx) { yRx = mid - 7; yTx = mid + 7; }
                else { yRx = mid + 7; yTx = mid - 7; }
            }
            if (PeakRx > 0) PeakLabel(dc, w, padL, padR, padT, ch, yRx, PeakRx, T.Accent, "PEAK ↓ ", dpi);
            if (PeakTx > 0) PeakLabel(dc, w, padL, padR, padT, ch, yTx, PeakTx, T.Violet, "PEAK ↑ ", dpi);

            // 鼠标悬停双通道十字准星读数（未悬停零绘制、零分配）
            if (_hoverX >= padL && _hoverX <= w - padR && _n >= 2)
            {
                double xStep = cw / (Cap - 1.0);
                int slot = (int)Math.Round((_hoverX - padL) / xStep);
                int firstSlot = Cap - _n;
                int li = slot - firstSlot;
                if (li < 0) li = 0;
                if (li >= _n) li = _n - 1;
                double hx = padL + (firstSlot + li) * xStep;
                double rxv = _rx[BufIndex(li)];
                double txv = _tx[BufIndex(li)];
                double ry = padT + ch - (rxv / top) * ch;
                double ty = padT + ch - (txv / top) * ch;

                dc.DrawLine(Gfx.Dash(0x3AFFFFFF, 1, 2), new Point(hx, padT), new Point(hx, padT + ch));
                dc.DrawEllipse(T.S(0xFF0A0F15), null, new Point(hx, ry), 4.0, 4.0);
                dc.DrawEllipse(T.S(T.Accent), null, new Point(hx, ry), 2.2, 2.2);
                dc.DrawEllipse(T.S(0xFF0A0F15), null, new Point(hx, ty), 4.0, 4.0);
                dc.DrawEllipse(T.S(T.Violet), null, new Point(hx, ty), 2.2, 2.2);

                int ago = (_n - 1) - li;
                string badge = "↓ " + Network.FmtMbps(rxv) + "  ↑ " + Network.FmtMbps(txv) + " Mbps  ·  " + (ago == 0 ? "NOW" : ("-" + ago + "s"));
                FormattedText rt = Gfx.Text(badge, T.FsNano, T.Hi, 0, dpi);
                double bw = rt.Width + 14;
                double bh = rt.Height + 6;
                double bx = hx + 10;
                if (bx + bw > w - padR) bx = hx - bw - 10;
                if (bx < padL) bx = padL;
                double by = padT + 4;
                dc.DrawRoundedRectangle(T.S(0xF20B1119), Gfx.P(TintA(T.Accent, 0x70), 1), new Rect(bx, by, bw, bh), 4, 4);
                dc.DrawRectangle(T.S(T.Accent), null, new Rect(bx + 1, by + 3, 2, bh - 6));
                dc.DrawText(rt, new Point(bx + 8, by + 3));
            }
        }

        private void DrawSeries(DrawingContext dc, double[] buf, uint tint, double top,
                                double padL, double cw, double padT, double ch, bool isRx)
        {
            if (_n < 2) return;
            double baseY = padT + ch;
            if (isRx)
            {
                if (_rxArea == null) _rxArea = new StreamGeometry();
                else _rxArea.Clear();
                using (StreamGeometryContext actx = _rxArea.Open())
                {
                    double x0 = padL + cw * (Cap - _n) / (Cap - 1.0);
                    actx.BeginFigure(new Point(x0, baseY), true, true);
                    for (int i = 0; i < _n; i++)
                    {
                        int slot = Cap - _n + i;
                        double v = buf[BufIndex(i)];
                        actx.LineTo(new Point(padL + cw * slot / (Cap - 1.0), baseY - (v / top) * ch), true, false);
                    }
                    actx.LineTo(new Point(padL + cw, baseY), true, false);
                }
                dc.DrawGeometry(Gfx.Grad(tint, 48, 3), null, _rxArea);
            }

            StreamGeometry geo = isRx ? _rxLine : _txLine;
            if (geo == null)
            {
                geo = new StreamGeometry();
                if (isRx) _rxLine = geo; else _txLine = geo;
            }
            else geo.Clear();

            using (StreamGeometryContext ctx = geo.Open())
            {
                for (int i = 0; i < _n; i++)
                {
                    int slot = Cap - _n + i;
                    double v = buf[BufIndex(i)];
                    Point pt = new Point(padL + cw * slot / (Cap - 1.0), baseY - (v / top) * ch);
                    if (i == 0) ctx.BeginFigure(pt, false, false);
                    else ctx.LineTo(pt, true, false);
                }
            }
            dc.DrawGeometry(null, Gfx.P(TintA(tint, 0x26), 4.8), geo);   // 外柔光
            dc.DrawGeometry(null, Gfx.P(TintA(tint, 0x48), 2.6), geo);   // 中辉光
            dc.DrawGeometry(null, Gfx.P(tint, 1.5), geo);                // 锐利主线
        }

        private void DrawHead(DrawingContext dc, double[] buf, uint tint, double top,
                              double padL, double cw, double padT, double ch)
        {
            if (_n == 0) return;
            double v = buf[BufIndex(_n - 1)];
            double x = padL + cw;
            double y = padT + ch - (v / top) * ch;
            dc.DrawEllipse(T.S(TintA(tint, 0x38)), null, new Point(x, y), 6.5, 6.5);
            dc.DrawEllipse(T.S(0xFF070A0F), null, new Point(x, y), 4.0, 4.0);
            dc.DrawEllipse(T.S(tint), null, new Point(x, y), 2.2, 2.2);
        }

        private void PeakLabel(DrawingContext dc, double w, double padL, double padR, double padT, double ch,
                               double y, double peak, uint tint, string prefix, double dpi)
        {
            double yy = y;
            if (yy < padT + 1) yy = padT + 1;
            if (yy > padT + ch - 1) yy = padT + ch - 1;

            // 虚线只划在有样本的区间上：空着的槽不该看起来像一条水平的实测值
            double cwPlot = w - padL - padR;
            double x0 = padL + cwPlot * (Cap - _n) / (Cap - 1.0);
            if (x0 < w - padR - 2)
                dc.DrawLine(Gfx.Dash(TintA(tint, 0x44), 1, 2), new Point(x0, yy), new Point(w - padR, yy));
            FormattedText ft = Gfx.Text(prefix + Network.FmtMbps(peak), T.FsNano, TintA(tint, 0xE6), 0, dpi);
            dc.DrawText(ft, new Point(w - padR + 6, yy - ft.Height / 2));
        }
    }
}