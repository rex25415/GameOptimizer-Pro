// ============================================================================
//  Network.Band.cs — 读数带：下行 / 上行 / 延迟 P50 / 丢包（Network 分片）
//
//  为什么四格同构：读者要在同一行里回答"现在多快 / 一个来回多久 / 掉了多少"，
//  不必滚动页面去拼三块面板。每格层次固定：标签 → 刻度 → 读数头 → 30 样本走势 → 注脚。
//
//  为什么下行/上行没有变化量芯片：吞吐没有好坏方向。给"上升"配一支绿色三角，
//  等于替读者宣布"流量变大是好事"——那不是读数，是评价。延迟与丢包相反，
//  越低越好，所以这两格带芯片（higherIsBetter = false）。
//
//  为什么不用 Sparkline：它没有"清空"语义。离开本页后采样停止，再回来时旧样本
//  还留在带上，读者会把十分钟前的形状读成现在。这里的 Trend 自带空态（压暗 +
//  采集起点线），和 Waveform 的空态语言一致：没数据就说没数据。
//
//  本文件只把样本变成像素：不碰网络 API、不开线程（采样在 Network.Sample.cs）。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Network : Page
    {
        private const int BandN = 30;

        private TextBlock _bandMeta, _gwText;
        private TextBlock _lanRttText, _wanRttText, _dualDiagText;
        private Border _dualBox;
        private DualPathLatencyReport _dualReport;
        private string _dualSig;
        private TextBlock _rxV, _txV, _rttV, _lossV;
        private TextBlock _rxNote, _txNote, _rttNote, _lossNote;
        private DeltaChip _dRtt, _dLoss;
        private Trend _trRx, _trTx, _trRtt, _trLoss;

        private readonly double[] _rx = new double[BandN];
        private readonly double[] _tx = new double[BandN];
        private readonly double[] _rtt = new double[BandN];   // <0 = 本次无回包
        private readonly double[] _p50Buf = new double[BandN];
        private int _tHead, _tCount;
        private int _pHead, _pCount;

        private int _rxCs = -1, _txCs = -1, _peakRxCs = -1, _peakTxCs = -1, _avgRxCs = -1, _avgTxCs = -1;
        private int _rttShown = -2, _lossShown = -1, _rttTier = -1, _lossTier = -1;
        private int _winCs = -1, _winShown = -2, _bandCount = -1;
        private string _gwShown;

        // ---- 骨架 -----------------------------------------------------------
        private UIElement BuildBand(Core c)
        {
            _bandMeta = Ui.Mono("采样 0/30", T.FsNano, T.Mute);

            StackPanel body = new StackPanel();
            body.Children.Add(Ui.PanelHead("实时读数", "DOWNLINK / UPLINK / RTT P50 / LOSS · 30 SAMPLES", _bandMeta));
            body.Children.Add(Gap(12));

            _rxV = Ui.Num("—", T.FsNumL, T.Mute);
            _txV = Ui.Num("—", T.FsNumL, T.Mute);
            _rttV = Ui.Num("—", T.FsNumL, T.Mute);
            _lossV = Ui.Num("—", T.FsNumL, T.Mute);

            _rxNote = Ui.Mono("等待首个窗口", T.FsNano, T.Mute);
            _txNote = Ui.Mono("等待首个窗口", T.FsNano, T.Mute);
            _rttNote = Ui.Mono("等待首个采样", T.FsNano, T.Mute);
            _lossNote = Ui.Mono("等待首个采样", T.FsNano, T.Mute);

            _dRtt = new DeltaChip();
            _dLoss = new DeltaChip();

            _trRx = new Trend(BandN); _trRx.Tint = T.Accent;
            _trTx = new Trend(BandN); _trTx.Tint = T.Violet;
            _trRtt = new Trend(BandN); _trRtt.Tint = T.Accent;
            _trLoss = new Trend(BandN); _trLoss.Tint = T.Warn;

            Grid cells = new Grid();
            cells.ColumnDefinitions.Add(new ColumnDefinition());
            cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            cells.ColumnDefinitions.Add(new ColumnDefinition());
            cells.RowDefinitions.Add(new RowDefinition());
            cells.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            cells.RowDefinitions.Add(new RowDefinition());

            FrameworkElement[] grid = new FrameworkElement[4];
            grid[0] = Ui.GlassTile(Ui.Stat("DOWNLINK", Head(_rxV, "Mbps", null), _trRx, Ui.Note(_rxNote)), 12, T.Accent);
            grid[1] = Ui.GlassTile(Ui.Stat("UPLINK", Head(_txV, "Mbps", null), _trTx, Ui.Note(_txNote)), 12, T.Violet);
            grid[2] = Ui.GlassTile(Ui.Stat("RTT P50", Head(_rttV, "ms", _dRtt), _trRtt, Ui.Note(_rttNote)), 12, T.Ok);
            grid[3] = Ui.GlassTile(Ui.Stat("LOSS", Head(_lossV, "%", _dLoss), _trLoss, Ui.Note(_lossNote)), 12, T.Warn);

            Grid.SetColumn(grid[0], 0); Grid.SetRow(grid[0], 0);
            Grid.SetColumn(grid[1], 2); Grid.SetRow(grid[1], 0);
            Grid.SetColumn(grid[2], 0); Grid.SetRow(grid[2], 2);
            Grid.SetColumn(grid[3], 2); Grid.SetRow(grid[3], 2);
            for (int i = 0; i < 4; i++) cells.Children.Add(grid[i]);
            body.Children.Add(cells);

            // ---- 双路径延迟对比（LAN 网关 vs WAN 公网）+ 铭牌 ---------------
            _dualBox = BuildDualPathStrip();
            _dualBox.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(_dualBox);

            Grid foot = new Grid();
            foot.Margin = new Thickness(0, 8, 0, 0);
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock rate = Ui.Mono("1 Hz · 30 样本 · ICMP", T.FsNano, T.Mute);
            foot.Children.Add(rate);

            _gwText = Ui.Mono("网关 —", T.FsNano, T.Dim);
            _gwText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_gwText, 1);
            foot.Children.Add(_gwText);
            Tip.Attach(_gwText, "网关取自活动接口的 GatewayAddresses（物理链路优先于 VPN 虚拟适配器）。" +
                                "ICMP 被拦截时这里只报无回包，不估算延迟。");
            body.Children.Add(foot);

            return Ui.Panel(body, 16);
        }

        /// <summary>读数头：数值 · 单位 · 变化量挤在同一行（同一条基线）。</summary>
        private static Grid Head(TextBlock val, string unit, DeltaChip delta)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.HorizontalAlignment = HorizontalAlignment.Left;
            g.Children.Add(val);

            TextBlock u = Ui.Mono(unit, T.FsNano, T.Dim);
            u.VerticalAlignment = VerticalAlignment.Bottom;
            u.Margin = new Thickness(6, 0, 0, 5);
            u.IsHitTestVisible = false;
            Grid.SetColumn(u, 1);
            g.Children.Add(u);

            if (delta != null)
            {
                delta.Margin = new Thickness(10, 0, 0, 8);
                delta.VerticalAlignment = VerticalAlignment.Bottom;
                Grid.SetColumn(delta, 2);
                g.Children.Add(delta);
            }
            return g;
        }

        // ---- 样本入口（采样器调用）-----------------------------------------
        /// <summary>1 Hz 吞吐样本：Mbps。只存原始值，注脚里的峰值/均值现算。</summary>
        private void PushThroughput(double rx, double tx)
        {
            _rx[_tHead] = rx;
            _tx[_tHead] = tx;
            _tHead = (_tHead + 1) % BandN;
            if (_tCount < BandN) _tCount++;

            double peak = 0.05;
            for (int i = 0; i < _tCount; i++)
            {
                int k = RingIndex(_tHead, _tCount, i);
                if (_rx[k] > peak) peak = _rx[k];
                if (_tx[k] > peak) peak = _tx[k];
            }
            _trRx.Push(_rx[_tHead == 0 ? BandN - 1 : _tHead - 1] / peak);
            _trTx.Push(_tx[_tHead == 0 ? BandN - 1 : _tHead - 1] / peak);

            PaintThroughput();
            PaintBandMeta();
        }

        /// <summary>1 Hz ICMP 样本：ms；小于 0 = 本次没有回包。</summary>
        private void PushPing(int ms)
        {
            _rtt[_pHead] = ms;
            _pHead = (_pHead + 1) % BandN;
            if (_pCount < BandN) _pCount++;

            double peak = 50;
            for (int i = 0; i < _pCount; i++)
            {
                int k = RingIndex(_pHead, _pCount, i);
                if (_rtt[k] > peak) peak = _rtt[k];
            }
            _trRtt.Push(ms < 0 ? 0 : ms / peak);
            _trLoss.Push(ms < 0 ? 1 : 0);

            PaintPing();
            PaintBandMeta();
            PaintGateway();
        }

        /// <summary>清空读数带：只在 Build（视觉树重建）时调用，重采样不丢历史。</summary>
        private void ResetBand()
        {
            _tHead = 0; _tCount = 0; _pHead = 0; _pCount = 0;
            _rxCs = _txCs = _peakRxCs = _peakTxCs = _avgRxCs = _avgTxCs = -1;
            _rttShown = -2; _lossShown = -1; _rttTier = -1; _lossTier = -1;
            _winCs = -1; _winShown = -2; _bandCount = -1; _gwShown = null;
            if (_trRx != null) { _trRx.Clear(); _trTx.Clear(); _trRtt.Clear(); _trLoss.Clear(); }

            if (_rxV != null) { _rxV.Text = "—"; _rxV.Foreground = T.S(T.Mute); }
            if (_txV != null) { _txV.Text = "—"; _txV.Foreground = T.S(T.Mute); }
            if (_rttV != null) { _rttV.Text = "—"; _rttV.Foreground = T.S(T.Mute); }
            if (_lossV != null) { _lossV.Text = "—"; _lossV.Foreground = T.S(T.Mute); }
            if (_rxNote != null) _rxNote.Text = "等待首个窗口";
            if (_txNote != null) _txNote.Text = "等待首个窗口";
            if (_rttNote != null) _rttNote.Text = "等待首个采样";
            if (_lossNote != null) _lossNote.Text = "等待首个采样";
            if (_dRtt != null) _dRtt.Set(0, "", false);
            if (_dLoss != null) _dLoss.Set(0, "", false);
            if (_bandMeta != null) _bandMeta.Text = "采样 0/30";
            if (_gwText != null) _gwText.Text = "网关 —";
            _dualReport = null;
            _dualSig = null;
            if (_lanRttText != null) { _lanRttText.Text = "\u2014"; _lanRttText.Foreground = T.S(T.Mute); }
            if (_wanRttText != null) { _wanRttText.Text = "\u2014"; _wanRttText.Foreground = T.S(T.Mute); }
            if (_dualDiagText != null) { _dualDiagText.Text = "双路径探针待测"; _dualDiagText.Foreground = T.S(T.Mute); }
        }

        private Border BuildDualPathStrip()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            StackPanel lanSide = new StackPanel();
            lanSide.Orientation = Orientation.Horizontal;
            lanSide.VerticalAlignment = VerticalAlignment.Center;
            lanSide.Children.Add(Ui.DotLabel(T.Accent, "LAN 网关", T.Dim));
            _lanRttText = Ui.Mono("\u2014", T.FsNano, T.Mute);
            _lanRttText.Margin = new Thickness(8, 0, 18, 0);
            _lanRttText.VerticalAlignment = VerticalAlignment.Center;
            lanSide.Children.Add(_lanRttText);
            g.Children.Add(lanSide);

            StackPanel wanSide = new StackPanel();
            wanSide.Orientation = Orientation.Horizontal;
            wanSide.VerticalAlignment = VerticalAlignment.Center;
            wanSide.Children.Add(Ui.DotLabel(T.Ok, "WAN 公网", T.Dim));
            _wanRttText = Ui.Mono("\u2014", T.FsNano, T.Mute);
            _wanRttText.Margin = new Thickness(8, 0, 14, 0);
            _wanRttText.VerticalAlignment = VerticalAlignment.Center;
            wanSide.Children.Add(_wanRttText);
            Grid.SetColumn(wanSide, 1);
            g.Children.Add(wanSide);

            _dualDiagText = Ui.Mono("双路径探针待测", T.FsNano, T.Mute);
            _dualDiagText.HorizontalAlignment = HorizontalAlignment.Right;
            _dualDiagText.VerticalAlignment = VerticalAlignment.Center;
            _dualDiagText.TextTrimming = TextTrimming.CharacterEllipsis;
            _dualDiagText.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(_dualDiagText, 2);
            g.Children.Add(_dualDiagText);

            Border box = new Border();
            box.Background = T.S(T.Inset);
            box.BorderBrush = T.S(T.LineSoft);
            box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(T.RadiusSm);
            box.Padding = new Thickness(10, 7, 10, 7);
            box.Child = g;
            Tip.Attach(box, "双路径异步探针（NetTrace.ProbeDualPathAsync）：\n" +
                             "· LAN 路径：本机到默认网关 ICMP 往返（反映 Wi-Fi / 路由 / 本地拥塞）\n" +
                             "· WAN 路径：223.5.5.5 / 114.114.114.114 ICMP/TCP 往返（反映外网链路波动）");
            return box;
        }

        private void ApplyDualPathReport(DualPathLatencyReport rep)
        {
            if (rep == null || _lanRttText == null) return;
            _dualReport = rep;

            string lanText = FormatPathResult(rep.Lan, rep.GatewayIp, "无网关");
            string wanText = FormatPathResult(rep.Wan, rep.Wan != null ? rep.Wan.Target : null, "不可达");
            string diag = string.IsNullOrEmpty(rep.Diagnosis) ? "已完成双路径采样" : rep.Diagnosis;
            if (rep.TunnelActive) diag += " · TUN 激活";

            string sig = lanText + "|" + wanText + "|" + diag;
            if (sig == _dualSig) return;
            _dualSig = sig;

            _lanRttText.Text = lanText;
            _lanRttText.Foreground = T.S(PathTint(rep.Lan));
            _wanRttText.Text = wanText;
            _wanRttText.Foreground = T.S(PathTint(rep.Wan));
            _dualDiagText.Text = diag;
            _dualDiagText.Foreground = T.S(rep.TunnelActive ? T.Warn : T.Dim);

            if (_dualBox != null)
            {
                string lanDetail = DetailLine("LAN 网关", rep.Lan, rep.GatewayIp);
                string wanDetail = DetailLine("WAN 公网", rep.Wan, rep.Wan != null ? rep.Wan.Target : "");
                Tip.Attach(_dualBox, lanDetail + "\n" + wanDetail + "\n诊断结论：" + diag);
            }
            UpdateRouteTopology(rep);
            if (_pCount > 0) PaintPing();
        }

        private static string FormatPathResult(PathProbeResult p, string target, string emptyReason)
        {
            if (p == null || !p.Reachable || p.RttMs < 0)
            {
                string why = (p != null && !string.IsNullOrEmpty(p.Error)) ? p.Error : emptyReason;
                return "\u2014 (" + why + ")";
            }
            string s = p.RttMs.ToString("0.#") + " ms";
            if (!string.IsNullOrEmpty(target)) s += " · " + target;
            if (!string.IsNullOrEmpty(p.Method) && p.Method != "ICMP") s += " (" + p.Method + ")";
            if (p.JitterMs > 0.05) s += " · 抖动 " + p.JitterMs.ToString("0.#") + "ms";
            return s;
        }

        private static uint PathTint(PathProbeResult p)
        {
            if (p == null || !p.Reachable || p.RttMs < 0) return T.Mute;
            if (p.RttMs > 80 || p.JitterMs > 25) return T.Warn;
            return T.Hi;
        }

        private static string DetailLine(string label, PathProbeResult p, string target)
        {
            if (p == null || !p.Reachable || p.RttMs < 0)
                return label + "（" + (string.IsNullOrEmpty(target) ? "\u2014" : target) + "）：\u2014" +
                       (p != null && !string.IsNullOrEmpty(p.Error) ? " · " + p.Error : "");
            return label + "（" + p.Target + " · " + p.Method + "）：" + p.RttMs.ToString("0.#") + " ms" +
                   " · min " + p.MinMs.ToString("0.#") + " / max " + p.MaxMs.ToString("0.#") +
                   " · 抖动 " + p.JitterMs.ToString("0.#") + " ms (" + p.Received + "/" + p.Sent + ")";
        }

        private static int RingIndex(int head, int count, int i)
        {
            return (head - count + i + BandN * 2) % BandN;
        }

        // ---- 渲染：吞吐两格 -------------------------------------------------
        private void PaintThroughput()
        {
            if (_rxV == null) return;

            double peakRx = 0, sumRx = 0, peakTx = 0, sumTx = 0;
            for (int i = 0; i < _tCount; i++)
            {
                int k = RingIndex(_tHead, _tCount, i);
                if (_rx[k] > peakRx) peakRx = _rx[k];
                if (_tx[k] > peakTx) peakTx = _tx[k];
                sumRx += _rx[k];
                sumTx += _tx[k];
            }
            double avgRx = _tCount == 0 ? 0 : sumRx / _tCount;
            double avgTx = _tCount == 0 ? 0 : sumTx / _tCount;
            double rx = _rx[(_tHead - 1 + BandN) % BandN];
            double tx = _tx[(_tHead - 1 + BandN) % BandN];

            int cs = ToCs(rx);
            if (cs != _rxCs)
            {
                _rxCs = cs;
                _rxV.Text = FmtMbps(rx);
                _rxV.Foreground = T.S(T.Hi);
            }
            cs = ToCs(tx);
            if (cs != _txCs)
            {
                _txCs = cs;
                _txV.Text = FmtMbps(tx);
                _txV.Foreground = T.S(T.Hi);
            }

            int p = ToCs(peakRx);
            if (p != _peakRxCs || ToCs(avgRx) != _avgRxCs)
            {
                _peakRxCs = p; _avgRxCs = ToCs(avgRx);
                _rxNote.Text = "30 次峰值 " + FmtMbps(peakRx) + " · 均值 " + FmtMbps(avgRx) + " Mbps";
            }
            p = ToCs(peakTx);
            if (p != _peakTxCs || ToCs(avgTx) != _avgTxCs)
            {
                _peakTxCs = p; _avgTxCs = ToCs(avgTx);
                _txNote.Text = "30 次峰值 " + FmtMbps(peakTx) + " · 均值 " + FmtMbps(avgTx) + " Mbps";
            }
        }

        // ---- 渲染：延迟与丢包 ----------------------------------------------
        private void PaintPing()
        {
            if (_rttV == null) return;
            if (_pCount == 0)
            {
                // 一次都还没采到：读数位保持 —，绝不写 0（0 是"测到了，正好为零"）。
                _rttV.Text = "—"; _rttV.Foreground = T.S(T.Mute); _rttShown = -2; _rttTier = 0;
                _lossV.Text = "—"; _lossV.Foreground = T.S(T.Mute); _lossShown = -1; _lossTier = -1;
                return;
            }

            int ok = 0, lost = 0;
            double sum = 0, min = 0, max = 0;
            for (int i = 0; i < _pCount; i++)
            {
                int k = RingIndex(_pHead, _pCount, i);
                double v = _rtt[k];
                if (v < 0) { lost++; continue; }
                if (ok == 0 || v < min) min = v;
                if (ok == 0 || v > max) max = v;
                sum += v;
                _p50Buf[ok] = v;
                ok++;
            }

            int p50 = -1;
            if (ok > 0)
            {
                Array.Sort(_p50Buf, 0, ok);
                p50 = (int)Math.Round(_p50Buf[ok / 2]);
            }
            if (p50 != _rttShown)
            {
                _rttShown = p50;
                _rttV.Text = p50 < 0 ? "—" : p50.ToString();
                int tier = p50 < 0 ? 0 : p50 > 50 ? 2 : 1;
                if (tier != _rttTier)
                {
                    _rttTier = tier;
                    _rttV.Foreground = T.S(tier == 2 ? T.Warn : tier == 1 ? T.Hi : T.Mute);
                }
            }

            int lossTenths = _pCount == 0 ? 0 : (int)Math.Round(lost * 1000.0 / _pCount);
            if (lossTenths != _lossShown)
            {
                _lossShown = lossTenths;
                _lossV.Text = (lossTenths / 10.0).ToString("0.0");
                int tier = lost == 0 ? 0 : lossTenths >= 50 ? 2 : 1;
                if (tier != _lossTier)
                {
                    _lossTier = tier;
                    _lossV.Foreground = T.S(tier == 2 ? T.Bad : tier == 1 ? T.Warn : T.Hi);
                }
            }

            if (_rttNote != null)
            {
                string rnote = ok == 0
                    ? "窗口内无回包"
                    : "LAN " + min.ToString("0") + "–" + max.ToString("0") + " ms · n=" + ok;
                if (_dualReport != null && _dualReport.Wan != null && _dualReport.Wan.Reachable && _dualReport.Wan.RttMs >= 0)
                    rnote += " · WAN " + _dualReport.Wan.RttMs.ToString("0") + " ms";
                _rttNote.Text = rnote;
            }
            if (_lossNote != null)
                _lossNote.Text = "最近 " + _pCount + " 次里 " + lost + " 次无回包";

            PaintDeltas(p50, lost, ok);
        }

        /// <summary>变化量基准取 9 次之前：比 1 次前稳，又比 30 次前贴近当下。</summary>
        private void PaintDeltas(int p50, int lost, int ok)
        {
            bool has = _pCount >= 9;
            int back = (_pHead - 9 + BandN) % BandN;

            if (!has || p50 < 0 || _rtt[back] < 0)
                _dRtt.Set(0, "", false);
            else
            {
                double prev = _rtt[back];
                _dRtt.Set(Dir(p50, prev, 0.5), Math.Abs(p50 - prev).ToString("0") + " ms", false);
            }

            if (!has)
                _dLoss.Set(0, "", false);
            else
            {
                double prevLoss = _rtt[back] < 0 ? 100.0 : 0.0;
                double nowLoss = _pCount == 0 ? 0 : lost * 100.0 / _pCount;
                _dLoss.Set(Dir(nowLoss, prevLoss, 0.05), Math.Abs(nowLoss - prevLoss).ToString("0.0") + " %", false);
            }
        }

        private void PaintBandMeta()
        {
            if (_bandMeta == null) return;
            int n = Math.Max(_tCount, _pCount);
            if (n == _bandCount && _winCs == _winShown) return;
            _bandCount = n;
            _winShown = _winCs;
            string win = _winCs < 0 ? "窗口 —" : "窗口 " + (_winCs / 100.0).ToString("0.00") + " s";
            _bandMeta.Text = "样本 " + n + "/30 · " + win;
        }

        /// <summary>采样窗口长度（秒）：首个窗口故意短，面板头如实写出来。</summary>
        private void SetBandWindow(double sec)
        {
            int cs = (int)Math.Round(sec * 100);
            if (cs == _winCs) return;
            _winCs = cs;
            _bandCount = -1;
            PaintBandMeta();
        }

        private void PaintGateway()
        {
            if (_gwText == null) return;
            string gw = _gwIp == null ? "网关 —" : "网关 " + _gwIp + (_gwNic == null ? "" : " · " + Ui.Trunc(_gwNic, 22, ""));
            if (gw == _gwShown) return;
            _gwShown = gw;
            _gwText.Text = gw;
        }

        /// <summary>找不到默认网关：只改说明，不推样本（没有目标 ≠ 丢包）。</summary>
        private void OnNoGateway()
        {
            if (_rttNote != null) _rttNote.Text = "未找到默认网关";
            if (_lossNote != null) _lossNote.Text = "无法测量 · 活动接口没有 GatewayAddresses";
            if (_gwText != null) _gwText.Text = "网关 —";
        }

        // ---- 数字格式 -------------------------------------------------------
        /// <summary>Mbps 读数：有效位随量级收放，低流量不会被读成 0.00。</summary>
        internal static string FmtMbps(double v)
        {
            if (v < 0) v = 0;
            if (v >= 100) return v.ToString("0");
            if (v >= 10) return v.ToString("0.0");
            return v.ToString("0.00");
        }

        /// <summary>百分之一刻度：字符串只在刻度变化时才拼（1 Hz × 4 格也要守住）。</summary>
        private static int ToCs(double v)
        {
            if (v < 0) v = 0;
            return (int)Math.Round(v * 100);
        }

        private static int Dir(double now, double prev, double eps)
        {
            double d = now - prev;
            if (d > eps) return 1;
            if (d < -eps) return -1;
            return 0;
        }

        // ====================================================================
        //  Trend — 30 样本走势带
        //  只画 0..1 归一化形状：它回答"在变好还是变坏"，不承担读数（读数是上面的数字）。
        //  没有样本时压暗并画采集起点线——空态必须解释自己，不能只是一条平线。
        //  零分配：矩形是值类型，画笔走 Gfx/T 的冻结缓存，不建几何、不拼字符串。
        // ====================================================================
        private sealed class Trend : FrameworkElement
        {
            private readonly double[] _buf;
            private int _head, _n;
            private uint _tint = T.Accent;

            public Trend(int cap)
            {
                _buf = new double[cap];
                Height = 16;
                IsHitTestVisible = false;
                SnapsToDevicePixels = true;
            }

            public uint Tint
            {
                set { if (_tint == value) return; _tint = value; InvalidateVisual(); }
            }

            public void Clear()
            {
                _head = 0; _n = 0;
                InvalidateVisual();
            }

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

                dc.DrawLine(Gfx.P(0x14FFFFFF, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));

                double step = _buf.Length > 1 ? w / (_buf.Length - 1.0) : w;
                int first = _buf.Length - _n;
                if (first > 0)
                {
                    // 还没采到的那一段：压暗 + 起点线。读者看到的是"在等数据"，不是"图坏了"。
                    double x0 = first * step;
                    if (x0 > 2)
                    {
                        dc.DrawRectangle(T.S(0x06000000), null, new Rect(0, 0, x0, h));
                        dc.DrawLine(Gfx.Dash(0x24FFFFFF, 1, 1), new Point(x0, 0), new Point(x0, h));
                    }
                }
                if (_n == 0) return;

                Pen line = Gfx.P(_tint, 1.2);
                double px = first * step;
                double py = h - _buf[0] * (h - 2) - 1;
                double lastX = px, lastY = py;
                for (int i = 1; i < _n; i++)
                {
                    double x = (first + i) * step;
                    double y = h - _buf[i] * (h - 2) - 1;
                    dc.DrawLine(line, new Point(px, py), new Point(x, y));
                    px = x; py = y;
                    lastX = x; lastY = y;
                }
                // 前沿光点：标出"现在"是哪一个样本
                dc.DrawEllipse(T.S(0xFF070A0F), null, new Point(lastX, lastY), 3.0, 3.0);
                dc.DrawEllipse(T.S(_tint), null, new Point(lastX, lastY), 1.6, 1.6);
            }
        }
    }
}