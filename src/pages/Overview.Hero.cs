// ============================================================================
//  Overview.Hero.cs — 01 态势总览：主仪表 / 四格读数 / 工况条（Overview 分片）
//  来源：Overview.cs（862 行单体）按职责拆分，partial 共享字段，行为零变化。
//  仪表、四个读数与权限/守护工况条共用同一句"这个分数怎么来的"，拆开就没法核对解释了。
//
//  Rev86 重心：从"四个大数字"改成"四台小仪表"。
//  原来的四格只有 值 + 单位 + 一条量条：读者知道现在是多少，不知道"正在变好还是变坏"、
//  "这算不算高"。上限 40 号的数字摆在那里，却没有任何东西回答"然后呢"。
//  现在每格补齐三样：与 9 拍前比的变化量、32 拍趋势带、以及一句人话注脚。
//  同一个格子里，"现在"（数字）、"走势"（趋势）、"变化"（箭头）、"解释"（注脚）四层齐全，
//  读者不需要去第二个面板拼答案。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Go.Pages
{
    public partial class Overview : Page
    {
        private Sparkline _spCpu, _spMem, _spJit, _spTmr;
        private DeltaChip _dCpu, _dMem, _dJit, _dTmr;
        private ColumnDefinition _heroLeftCol;
        private Grid _heroMetricsGrid;
        private Grid _rdBreakGrid;
        private FrameworkElement[] _heroCells;
        private TextBlock[] _heroUnits;
        private bool _heroNarrow;

        // 变化量基准：9 拍（约 9 秒）前的读数。24 格环形足够覆盖一屏趋势带，
        // 且不随页面存活时间增长——旧实现若用 List 记历史，开一天的窗口会攒 8 万个点。
        private const int TrendN = 24;
        private readonly double[] _hCpu = new double[TrendN];
        private readonly double[] _hMem = new double[TrendN];
        private readonly double[] _hJit = new double[TrendN];
        private readonly double[] _hTmr = new double[TrendN];
        private int _hHead, _hCount;

        // ---- Rev130 非对称 Bento 驾驶舱中枢（左 Mission Core + 右 2×2 宽幅遥测矩阵 + 四维健康条） ----
        private UIElement BuildHero(Core c)
        {
            Grid deck = new Grid();
            _heroLeftCol = new ColumnDefinition { Width = new GridLength(338) };
            deck.ColumnDefinitions.Add(_heroLeftCol);
            deck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            deck.ColumnDefinitions.Add(new ColumnDefinition());

            // ---- 左侧 Mission Core（大表盘 + 四维评级 + 2×2 快控胶囊 + 瓶颈诊断） ----
            StackPanel leftCore = new StackPanel();
            leftCore.Children.Add(BuildMissionHeader());

            _gauge = new ArcGauge(0, "\u2014", "READINESS", "系统就绪度");
            _gauge.Width = 224;
            _gauge.Height = 180;
            _gauge.ValueText.FontSize = 46;
            _gauge.HorizontalAlignment = HorizontalAlignment.Center;
            _gauge.VerticalAlignment = VerticalAlignment.Center;
            _gauge.Margin = new Thickness(0, 4, 0, 2);
            Tip.Attach(_gauge,
                "系统就绪度 = 100 \u2212 抖动惩罚 \u2212 CPU 惩罚 \u2212 内存惩罚 \u2212 定时器惩罚\n" +
                "\u00B7 抖动：P99 每 90\u00B5s 扣 1 分，上限 26\n" +
                "\u00B7 CPU：超过 45% 后每 1% 扣 0.34 分\n" +
                "\u00B7 内存：超过 55% 后每 1% 扣 0.52 分\n" +
                "\u00B7 定时器：颗粒度 > 4ms 扣 10 分\n" +
                "四项都是本机实测（NtQuery* / GlobalMemoryStatusEx），不是基准分。");
            leftCore.Children.Add(_gauge);
            leftCore.Children.Add(BuildReadinessBreakdown());
            leftCore.Children.Add(BuildBottleneckStrip());

            Grid missionCard = new Grid();
            missionCard.Children.Add(Ui.HeroPanel(leftCore, 16));
            missionCard.Children.Add(Ui.CornerTicks(T.AccentA(0x9A), 11, 1.5));
            deck.Children.Add(missionCard);

            // ---- 右侧：2×2 宽幅 Bento 遥测矩阵 + 四维健康条 + 状态脊线 ----
            Grid rightStage = new Grid();
            rightStage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightStage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            rightStage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightStage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            rightStage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(rightStage, 2);
            deck.Children.Add(rightStage);

            Grid metrics = new Grid();
            _heroMetricsGrid = metrics;
            metrics.ColumnDefinitions.Add(new ColumnDefinition());
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition());
            metrics.RowDefinitions.Add(new RowDefinition());
            metrics.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            metrics.RowDefinitions.Add(new RowDefinition());

            _cpuVal = Ui.Num("\u2014", T.FsNumXL, T.Hi);
            _cpuBar = new SegBar(0); _cpuBar.Height = 3; _cpuBar.Tint = T.Accent;
            _cpuNote = Ui.Mono("等待采样", T.FsNano, T.Mute);
            _memVal = Ui.Num("\u2014", T.FsNumXL, T.Hi);
            _memBar = new SegBar(0); _memBar.Height = 3; _memBar.Tint = T.Accent2;
            _memNote = Ui.Mono("等待采样", T.FsNano, T.Mute);
            _jitVal = Ui.Num("\u2014", T.FsNumXL, T.Hi);
            _jitBar = new SegBar(0); _jitBar.Height = 3; _jitBar.Tint = T.Warn;
            _jitNote = Ui.Mono("唤醒超调", T.FsNano, T.Mute);
            _tmrVal = Ui.Num("\u2014", T.FsNumXL, T.Hi);
            _tmrBar = new SegBar(0); _tmrBar.Height = 3; _tmrBar.Tint = T.Ok;
            _tmrNote = Ui.Mono("等待采样", T.FsNano, T.Mute);

            _cpuTick = new NumTicker(_cpuVal, delegate (double v) { return ((int)Math.Round(v)).ToString(); });
            _memTick = new NumTicker(_memVal, delegate (double v) { return v.ToString("0.0"); });
            _jitTick = new NumTicker(_jitVal, delegate (double v) { return v < 10000 ? v.ToString("0") : (v / 1000).ToString("0.0") + "k"; });
            _tmrTick = new NumTicker(_tmrVal, delegate (double v) { return v.ToString("0.0"); });

            _spCpu = Trend(T.Accent); _spCpu.Height = 22; _dCpu = new DeltaChip();
            _spMem = Trend(T.Accent2); _spMem.Height = 22; _dMem = new DeltaChip();
            _spJit = Trend(T.Warn); _spJit.Height = 22; _dJit = new DeltaChip();
            _spTmr = Trend(T.Ok); _spTmr.Height = 22; _dTmr = new DeltaChip();

            _heroUnits = new TextBlock[4];
            FrameworkElement[] cells = new FrameworkElement[4];
            _heroCells = cells;
            cells[0] = Ui.GlassTile(Ui.Stat("CPU LOAD", Head(_cpuVal, "%", _dCpu, out _heroUnits[0]), Trend(_spCpu, _cpuBar), Ui.Note(_cpuNote)), 13, T.Accent);
            cells[1] = Ui.GlassTile(Ui.Stat("MEMORY", Head(_memVal, "GB", _dMem, out _heroUnits[1]), Trend(_spMem, _memBar), Ui.Note(_memNote)), 13, T.Accent2);
            cells[2] = Ui.GlassTile(Ui.Stat("WAKE JITTER", Head(_jitVal, "\u00B5s P99", _dJit, out _heroUnits[2]), Trend(_spJit, _jitBar), Ui.Note(_jitNote)), 13, T.Warn);
            cells[3] = Ui.GlassTile(Ui.Stat("TIMER RES", Head(_tmrVal, "ms", _dTmr, out _heroUnits[3]), Trend(_spTmr, _tmrBar), Ui.Note(_tmrNote)), 13, T.Ok);

            Grid.SetColumn(cells[0], 0); Grid.SetRow(cells[0], 0);
            Grid.SetColumn(cells[1], 2); Grid.SetRow(cells[1], 0);
            Grid.SetColumn(cells[2], 0); Grid.SetRow(cells[2], 2);
            Grid.SetColumn(cells[3], 2); Grid.SetRow(cells[3], 2);
            for (int i = 0; i < 4; i++) metrics.Children.Add(cells[i]);
            rightStage.Children.Add(metrics);

            UIElement axisStrip = BuildFourAxisStrip();
            Grid.SetRow(axisStrip, 2);
            rightStage.Children.Add(axisStrip);

            // ---- 工况条：独立凹陷玻璃状态槽 ---------------------------------
            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border footBar = new Border();
            footBar.CornerRadius = new CornerRadius(T.RadiusMd);
            footBar.Background = T.S(0xAA080E1B);
            footBar.BorderBrush = T.S(T.LineSoft);
            footBar.BorderThickness = new Thickness(1);
            footBar.Padding = new Thickness(12, 7, 12, 7);
            footBar.Child = foot;
            Grid.SetRow(footBar, 4);
            rightStage.Children.Add(footBar);

            StackPanel spine = new StackPanel();
            spine.Orientation = Orientation.Horizontal;
            spine.VerticalAlignment = VerticalAlignment.Center;

            _permDot = SpineDot();
            spine.Children.Add(_permDot);
            TextBlock permKey = Ui.Mono("权限", T.FsNano, T.Mute);
            permKey.Margin = new Thickness(6, 0, 6, 0);
            spine.Children.Add(permKey);
            _permText = Ui.Mono("\u2014", T.FsNano, T.Mute);
            spine.Children.Add(_permText);

            TextBlock spineDiv = Ui.Mono("\u00B7", T.FsNano, T.Mute);
            spineDiv.Margin = new Thickness(12, 0, 12, 0);
            spine.Children.Add(spineDiv);

            _guardDot = SpineDot();
            spine.Children.Add(_guardDot);
            TextBlock guardKey = Ui.Mono("守护", T.FsNano, T.Mute);
            guardKey.Margin = new Thickness(6, 0, 6, 0);
            spine.Children.Add(guardKey);
            _guardText = Ui.Mono("\u2014", T.FsNano, T.Mute);
            spine.Children.Add(_guardText);

            TextBlock gpuDiv = Ui.Mono("\u00B7", T.FsNano, T.Mute);
            gpuDiv.Margin = new Thickness(12, 0, 12, 0);
            spine.Children.Add(gpuDiv);

            StackPanel gpuChip = new StackPanel { Orientation = Orientation.Horizontal, Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
            _gpuDot = SpineDot();
            gpuChip.Children.Add(_gpuDot);
            TextBlock gpuKey = Ui.Mono("GPU", T.FsNano, T.Mute);
            gpuKey.Margin = new Thickness(6, 0, 6, 0);
            gpuChip.Children.Add(gpuKey);
            _gpuText = Ui.Mono("\u2014", T.FsNano, T.Mute);
            gpuChip.Children.Add(_gpuText);
            Pressable.On(gpuChip, delegate { if (C != null && C.Shell != null) C.Shell.Go("graphics"); });
            Tip.Attach(gpuChip, "独立显卡实时引擎占用与显存使用（PDH 实测，未采样显示 —）；点击打开图形栈");
            spine.Children.Add(gpuChip);

            Tip.Attach(spine, "权限\uFF1D本进程能否写入系统；守护\uFF1D已建档游戏启动时是否自动挂载；GPU\uFF1D实时显卡占用。");
            foot.Children.Add(spine);

            TextBlock rate = Ui.Mono("1 Hz \u00B7 5 Hz \u00B7 30 s", T.FsNano, T.Mute);
            rate.HorizontalAlignment = HorizontalAlignment.Right;
            rate.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rate, 1);
            foot.Children.Add(rate);

            bool initNarrow = c != null && c.Shell != null && c.Shell.Width > 0 && c.Shell.Width < 1220;
            ApplyHeroBp(initNarrow);
            deck.SizeChanged += delegate (object sender, SizeChangedEventArgs e)
            {
                if (e.NewSize.Width <= 0) return;
                bool narrow = e.NewSize.Width < 940;
                if (narrow != _heroNarrow) ApplyHeroBp(narrow);
            };
            return deck;
        }

        private void ApplyHeroBp(bool narrow)
        {
            _heroNarrow = narrow;
            if (_heroLeftCol != null) _heroLeftCol.Width = new GridLength(narrow ? 286 : 338);
            if (_gauge != null)
            {
                _gauge.Width = narrow ? 196 : 224;
                _gauge.Height = narrow ? 162 : 180;
            }
            double fs = narrow ? 30 : T.FsNumXL;
            if (_cpuVal != null) _cpuVal.FontSize = fs;
            if (_memVal != null) _memVal.FontSize = fs;
            if (_jitVal != null) _jitVal.FontSize = fs;
            if (_tmrVal != null) _tmrVal.FontSize = fs;
            if (_heroUnits != null)
            {
                Thickness um = narrow ? new Thickness(4, 0, 0, 3) : new Thickness(6, 0, 0, 5);
                for (int i = 0; i < _heroUnits.Length; i++)
                    if (_heroUnits[i] != null) _heroUnits[i].Margin = um;
            }
            if (narrow && _dJit != null) _dJit.Visibility = Visibility.Collapsed;
            else if (_dJit != null)
            {
                _dJit.Visibility = Visibility.Visible;
                if (C != null && C.Perf != null && C.Perf.Current != null && C.Perf.Current.Tick > 0 && !C.Perf.Current.JitterLive)
                    _dJit.Set(0, "待机", false);
            }

            _cpuDpcTenth = int.MinValue;
            _memStandbyTenth = int.MinValue;
            _jitAvgShown = int.MinValue;
            if (C != null && C.Perf != null && C.Perf.Current != null && C.Perf.Current.Tick > 0)
            {
                Snapshot cur = C.Perf.Current;
                WriteCpuNote(cur, _mhz);
                WriteMemNote(cur);
                WriteJitNote(cur);
            }
        }

        private UIElement BuildReadinessBreakdown()
        {
            Grid g = new Grid();
            _rdBreakGrid = g;
            g.Margin = new Thickness(0, 8, 0, 0);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _rdTimerPill = MakeReadinessPill("定时器 —", "实测定时器颗粒度（>4.0ms 将扣减就绪度 10 分）· 点击打开调度页", out _rdTimerDot, out _rdTimerText, delegate { if (C != null && C.Shell != null) C.Shell.Go("scheduler"); });
            _rdTunePill = MakeReadinessPill("调优 —", "系统调优矩阵已应用覆盖率与第三方残留情况 · 点击打开调优矩阵", out _rdTuneDot, out _rdTuneText, delegate { if (C != null && C.Shell != null) C.Shell.Go("tuning"); });
            _rdPowerPill = MakeReadinessPill("供电 —", "当前供电来源（AC / 电池 / 节能模式）与电源计划 · 点击切换方案", out _rdPowerDot, out _rdPowerText, delegate { if (C != null) TogglePowerMenu(C); });
            _rdGuardPill = MakeReadinessPill("守护 —", "后台守护自动挂载与实时防护状态 · 点击打开战术档案", out _rdGuardDot, out _rdGuardText, delegate { if (C != null && C.Shell != null) C.Shell.Go("profiles"); });

            Grid.SetColumn(_rdTimerPill, 0); Grid.SetRow(_rdTimerPill, 0);
            Grid.SetColumn(_rdTunePill, 2); Grid.SetRow(_rdTunePill, 0);
            Grid.SetColumn(_rdPowerPill, 0); Grid.SetRow(_rdPowerPill, 2);
            Grid.SetColumn(_rdGuardPill, 2); Grid.SetRow(_rdGuardPill, 2);
            g.Children.Add(_rdTimerPill);
            g.Children.Add(_rdTunePill);
            g.Children.Add(_rdPowerPill);
            g.Children.Add(_rdGuardPill);
            return g;
        }

        private static FrameworkElement MakeReadinessPill(string text, string tip, out Border dot, out TextBlock label, Action onClick)
        {
            StackPanel dl = Ui.DotLabel(T.Mute, text, T.Dim);
            dot = dl.Children.Count > 0 ? dl.Children[0] as Border : null;
            label = dl.Children.Count > 1 ? dl.Children[1] as TextBlock : null;
            if (label != null) label.TextTrimming = TextTrimming.CharacterEllipsis;

            Border b = new Border();
            b.CornerRadius = new CornerRadius(T.RadiusMd);
            b.Background = T.SubCardBg();
            b.BorderBrush = T.S(T.LineSoft);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(8, 5, 8, 5);
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.Child = dl;
            b.MouseEnter += delegate { b.Background = T.S(T.Raised); b.BorderBrush = T.S(T.AccentA(0x78)); };
            b.MouseLeave += delegate { b.Background = T.SubCardBg(); b.BorderBrush = T.S(T.LineSoft); };
            if (onClick != null) Pressable.On(b, onClick);
            Tip.Attach(b, tip);
            return b;
        }

        private static void SetDotPill(Border dot, TextBlock label, string text, uint dotCol, uint textCol)
        {
            if (dot != null) dot.Background = T.S(dotCol);
            if (label != null)
            {
                if (label.Text != text) label.Text = text;
                label.Foreground = T.S(textCol);
            }
        }

        private void UpdateReadinessTimer(double timerMs)
        {
            if (_rdTimerText == null) return;
            int tenth = timerMs <= 0 ? -1 : (int)Math.Round(timerMs * 10.0);
            if (tenth == _rdTimerTenth) return;
            _rdTimerTenth = tenth;
            if (tenth < 0)
            {
                SetDotPill(_rdTimerDot, _rdTimerText, "定时器 \u2014", T.Mute, T.Mute);
                return;
            }
            string s = (tenth / 10.0).ToString("0.0") + "ms";
            if (tenth <= 15) SetDotPill(_rdTimerDot, _rdTimerText, "精度 " + s, T.Ok, T.Text);
            else if (tenth <= 40) SetDotPill(_rdTimerDot, _rdTimerText, "精度 " + s, T.AccentDim, T.Dim);
            else SetDotPill(_rdTimerDot, _rdTimerText, "精度 " + s, T.Warn, T.Warn);
        }

        private void RenderTuningCoverage()
        {
            if (_rdTuneText == null || C == null || C.Engine == null) return;
            List<ActionDef> all = C.Engine.Actions;
            int total = all.Count;
            int applied = 0;
            int remPending = 0;
            bool anyPending = false;
            for (int i = 0; i < total; i++)
            {
                ActionDef a = all[i];
                string cur0;
                AState st = C.Engine.ProbeForUi(a, out cur0);
                if (cur0 == Ops.PendingText) anyPending = true;
                if (st == AState.Applied) applied++;
                else if (a.Remediation) remPending++;
            }
            if (anyPending) C.Engine.RefreshPowerAcCacheAsync(null);
            if (applied == _rdTuneApplied && total == _rdTuneTotal && remPending == _rdTuneRem && anyPending == _rdTunePending) return;
            _rdTuneApplied = applied;
            _rdTuneTotal = total;
            _rdTuneRem = remPending;
            _rdTunePending = anyPending;

            if (total == 0)
            {
                SetDotPill(_rdTuneDot, _rdTuneText, "调优 \u2014", T.Mute, T.Mute);
                return;
            }
            uint dotCol = remPending > 0 ? T.Warn : (applied * 2 >= total ? T.Ok : (applied > 0 ? T.AccentDim : T.Mute));
            uint txtCol = remPending > 0 ? T.Warn : (applied > 0 ? T.Text : T.Dim);
            SetDotPill(_rdTuneDot, _rdTuneText, "调优 " + applied + "/" + total, dotCol, txtCol);
            if (_rdTunePill != null)
            {
                string tip = "调优覆盖：已应用 " + applied + "/" + total + " 项"
                           + (remPending > 0 ? ("；另有 " + remPending + " 项第三方残留待修复") : "；无异常残留")
                           + (anyPending ? "（电源项后台异步探测中）" : "");
                Tip.Attach(_rdTunePill, tip);
            }
        }

        private void RenderReadinessPower()
        {
            if (_rdPowerText == null || C == null) return;
            bool onAc, hasBat, saver;
            int pct;
            ReadPowerFlags(C, out onAc, out hasBat, out pct, out saver);
            string prof = C.Profile ?? "";
            string profEn = C.ProfileEn ?? "";
            string key = (onAc ? "1" : "0") + "|" + (hasBat ? "1" : "0") + "|" + (saver ? "1" : "0") + "|" + prof + "|" + profEn;
            if (key == _rdPowerKey) return;
            _rdPowerKey = key;

            if (saver)
            {
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 节能模式", T.Warn, T.Warn);
                return;
            }
            if (hasBat && !onAc)
            {
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 电池", T.Warn, T.Warn);
                return;
            }
            if (profEn.IndexOf("ULT", StringComparison.OrdinalIgnoreCase) >= 0 || prof.IndexOf("卓越", StringComparison.Ordinal) >= 0)
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 AC·卓越", T.Ok, T.Text);
            else if (profEn.IndexOf("HIGH", StringComparison.OrdinalIgnoreCase) >= 0 || prof.IndexOf("高", StringComparison.Ordinal) >= 0)
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 AC·高能", T.Ok, T.Text);
            else if (profEn.IndexOf("SAVER", StringComparison.OrdinalIgnoreCase) >= 0 || prof.IndexOf("节能", StringComparison.Ordinal) >= 0)
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 AC·节能", T.Warn, T.Warn);
            else if (profEn.IndexOf("BALANCED", StringComparison.OrdinalIgnoreCase) >= 0 || prof.IndexOf("平衡", StringComparison.Ordinal) >= 0)
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 AC·平衡", T.AccentDim, T.Dim);
            else if (prof.Length > 0)
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 AC·定制", T.Ok, T.Text);
            else
                SetDotPill(_rdPowerDot, _rdPowerText, "供电 AC 供电", T.AccentDim, T.Dim);
        }

        private void RenderLiveGpu()
        {
            if (_gpuText == null || _gpuDot == null) return;
            GpuSnapshot gs = (C != null && C.Gpu != null) ? C.Gpu.Current : null;
            if (gs == null || !gs.Available)
            {
                if (_gpuUtilShown != -1)
                {
                    _gpuUtilShown = -1;
                    _gpuVramTenth = -1;
                    _gpuDot.Fill = T.S(T.Mute);
                    _gpuText.Text = "\u2014";
                    _gpuText.Foreground = T.S(T.Mute);
                }
                return;
            }
            double pct = gs.UtilPct;
            long vramBytes = gs.MemKnown ? gs.LocalMemBytes : 0;
            long vramTotal = 0;
            for (int i = 0; i < gs.AdapterCount; i++)
            {
                GpuAdapterSample ad = gs.Adapters[i];
                if (ad != null && ad.Discrete && ad.Available)
                {
                    pct = ad.MaxUtilPct;
                    if (ad.MemKnown && ad.VramUsedBytes > 0) vramBytes = ad.VramUsedBytes;
                    if (ad.VramTotalBytes > 0) vramTotal = ad.VramTotalBytes;
                    break;
                }
            }
            int util = (int)Math.Round(pct);
            int vramTenth = vramBytes > 0 ? (int)Math.Round(vramBytes / (1024.0 * 1024.0 * 102.4)) : 0;
            if (util == _gpuUtilShown && vramTenth == _gpuVramTenth) return;
            _gpuUtilShown = util;
            _gpuVramTenth = vramTenth;
            uint col = util >= 90 ? T.Warn : T.Ok;
            _gpuDot.Fill = T.S(col);
            _gpuText.Foreground = T.S(util >= 90 ? T.Warn : T.Text);
            if (vramTenth > 0)
            {
                string vStr = (vramTenth / 10.0).ToString("0.0");
                if (vramTotal > 0) vStr += "/" + (vramTotal / (1024.0 * 1024.0 * 1024.0)).ToString("0") + "G";
                else vStr += " GB";
                _gpuText.Text = util + "% \u00B7 " + vStr;
            }
            else _gpuText.Text = util + "%";
        }

        private void RenderReadinessGuard()
        {
            if (_rdGuardText == null) return;
            Go.Daemon.DaemonHost d = C == null ? null : C.Daemon;
            GameProfile a = d == null ? null : d.Watcher.Active;
            bool on = d != null && d.Watcher.Enabled;
            string key = d == null ? "null" : (a != null ? ("run:" + a.Name) : (on ? "on" : "off"));
            if (key == _rdGuardKey) return;
            _rdGuardKey = key;

            if (d == null) SetDotPill(_rdGuardDot, _rdGuardText, "守护 未启动", T.Mute, T.Mute);
            else if (a != null) SetDotPill(_rdGuardDot, _rdGuardText, "守护 " + Ui.Trunc(a.Name, 5, ""), T.Ok, T.Text);
            else if (on) SetDotPill(_rdGuardDot, _rdGuardText, "守护 待命", T.AccentDim, T.Dim);
            else SetDotPill(_rdGuardDot, _rdGuardText, "守护 关闭", T.Dim, T.Dim);
        }

        /// <summary>
        /// 读数头：数值 · 单位 · 变化量 挤在同一行。
        /// 变化量原来是单独一行，把每格撑高了 21px——四格一起高，整张卡就多出一条空白带。
        /// 仪表的惯例本来就是"读数旁边挂一个偏差"，不是"读数下面另起一行"。
        /// </summary>
        private static Grid Head(TextBlock val, string unit, DeltaChip delta, out TextBlock unitTb)
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
            unitTb = u;
            Grid.SetColumn(u, 1);
            g.Children.Add(u);

            delta.Margin = new Thickness(10, 0, 0, 8);
            delta.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(delta, 2);
            g.Children.Add(delta);
            return g;
        }

        /// <summary>趋势带：同一条 0..1 归一化走势，四格共用同一高度与同一透明度。</summary>
        private static Sparkline Trend(uint tint)
        {
            Sparkline sp = new Sparkline(32);
            sp.Height = 16;
            sp.Tint = tint;
            sp.Opacity = 0.9;
            return sp;
        }

        /// <summary>格子中段：趋势带在上、量条压底，共用一条左对齐基线。</summary>
        private static StackPanel Trend(Sparkline sp, SegBar bar)
        {
            StackPanel box = new StackPanel();
            box.Children.Add(sp);

            if (bar != null)
            {
                bar.Margin = new Thickness(0, 7, 0, 0);
                box.Children.Add(bar);
            }
            return box;
        }

        /// <summary>
        /// 每拍刷新四格的"趋势 / 变化量"。数值本身由 NumTicker 补间，这里只喂趋势与基准。
        /// 基准取 9 拍前：比 1 拍前稳（不会因为一次调度抖动乱跳），又比 30 拍前贴近当下。
        /// </summary>
        private void PushTrends(Snapshot s)
        {
            if (_spCpu == null) return;

            double cpu = s.CpuTotal * 100.0;
            double mem = s.MemUsedMB / 1024.0;
            double jit = s.JitterLive ? s.JitterP99Us : 0;
            double tmr = s.TimerMs;

            int back = (_hHead - 9 + TrendN) % TrendN;
            bool has = _hCount >= 9;
            double pCpu = has ? _hCpu[back] : cpu;
            double pMem = has ? _hMem[back] : mem;
            double pJit = has ? _hJit[back] : jit;
            double pTmr = has ? _hTmr[back] : tmr;

            _hCpu[_hHead] = cpu; _hMem[_hHead] = mem; _hJit[_hHead] = jit; _hTmr[_hHead] = tmr;
            _hHead = (_hHead + 1) % TrendN;
            if (_hCount < TrendN) _hCount++;

            _spCpu.Push(s.CpuTotal);
            _spMem.Push(s.MemUsedPct);
            _spJit.Push(Math.Min(1, s.JitterP99Us / 6000.0));
            _spTmr.Push(Math.Min(1, s.TimerMs / 10.0));

            // 抖动探针没在跑时，"0 变化"是假读数：明确显示"未采样"而不是一个中性点；窄窗让位给 µs P99。
            if (_heroNarrow) _dJit.Set(0, "", false);
            else if (!s.JitterLive) _dJit.Set(0, "待机", false);
            else _dJit.Set(Dir(jit, pJit, 5), has ? FmtJit(Math.Abs(jit - pJit)) : "", false);

            // 前 9 拍没有基准：传空 body，芯片保持占位但不显示（不拿 0 冒充"没有变化"）。
            _dCpu.Set(Dir(cpu, pCpu, 0.35), has ? Math.Abs(cpu - pCpu).ToString("0.0") : "", false);
            _dMem.Set(Dir(mem, pMem, 0.05), has ? Math.Abs(mem - pMem).ToString("0.0") + (_heroNarrow ? "G" : " GB") : "", false);
            _dTmr.Set(Dir(tmr, pTmr, 0.05), has ? Math.Abs(tmr - pTmr).ToString("0.0") + (_heroNarrow ? "" : " ms") : "", false);
        }

        private static int Dir(double now, double prev, double eps)
        {
            double d = now - prev;
            if (d > eps) return 1;
            if (d < -eps) return -1;
            return 0;
        }

        private static string FmtJit(double v)
        {
            if (v < 1000) return v.ToString("0");
            return (v / 1000.0).ToString("0.0") + "k";
        }

        /// <summary>状态点：5px 圆点，语义色只由 Ok/Warn/Bad 承担。</summary>
        private static Ellipse SpineDot()
        {
            Ellipse e = new Ellipse();
            e.Width = 5;
            e.Height = 5;
            e.Fill = T.S(T.Mute);
            e.VerticalAlignment = VerticalAlignment.Center;
            e.IsHitTestVisible = false;
            return e;
        }

        // ---- 四格读数的写入：只在"整数刻度"变化时才拼字符串 ----------------
        // 每秒重拼 "DPC 1.4% \u00B7 233 PROC \u00B7 2.5 GHz" 要 3 次 ToString + 3 次串接,
        // 实测 18 \u00B5s / 400 B 一拍；可这几个整数一天也不会变几次。
        // 现在先比较整数刻度，没变就直接返回：稳态 0 分配、0 次文本重排。
        private int _cpuDpcTenth = int.MinValue, _cpuProcs = int.MinValue, _cpuGhzTenth = int.MinValue;
        private int _memStandbyTenth = int.MinValue, _memModifiedTenth = int.MinValue;
        private int _jitAvgShown = int.MinValue, _jitMaxShown = int.MinValue;

        /// <summary>CPU 说明行：DPC 百分比 / 进程数 / 频率。频率由 Tick 的慢拍提供。</summary>
        private void WriteCpuNote(Snapshot s, int mhz)
        {
            if (_cpuNote == null) return;
            int dpc = (int)Math.Round(s.DpcPercent * 10.0);          // 0.1% 刻度
            int ghz = mhz > 0 ? (int)Math.Round(mhz / 100.0) : 0;    // 0.1 GHz 刻度
            if (dpc == _cpuDpcTenth && s.ProcessCount == _cpuProcs && ghz == _cpuGhzTenth) return;
            _cpuDpcTenth = dpc;
            _cpuProcs = s.ProcessCount;
            _cpuGhzTenth = ghz;
            if (_heroNarrow)
            {
                _cpuNote.Text = "DPC " + (dpc / 10.0).ToString("0.0") + "% \u00B7 " + s.ProcessCount + "P"
                    + (ghz > 0 ? " \u00B7 " + (ghz / 10.0).ToString("0.0") + "GHz" : "")
                    + (_cpuThrottled ? " \u00B7 限频" : "");
            }
            else
            {
                _cpuNote.Text = "DPC " + (dpc / 10.0).ToString("0.0") + "% \u00B7 " + s.ProcessCount + " PROC"
                    + (ghz > 0 ? " \u00B7 " + (ghz / 10.0).ToString("0.0") + " GHz" : "")
                    + (_cpuThrottled ? " \u00B7 限频" : "");
            }
            _cpuNote.Foreground = T.S(_cpuThrottled ? T.Warn : T.Mute);
        }

        /// <summary>内存说明行：待机 / 已修改（GB，0.1 刻度）。</summary>
        private void WriteMemNote(Snapshot s)
        {
            if (_memNote == null) return;
            int standby = (int)Math.Round(s.MemStandbyMB / 102.4);   // 0.1 GB 刻度
            int modified = (int)Math.Round(s.MemModifiedMB / 102.4);
            if (standby == _memStandbyTenth && modified == _memModifiedTenth) return;
            _memStandbyTenth = standby;
            _memModifiedTenth = modified;
            if (_heroNarrow)
            {
                _memNote.Text = "待机 " + (standby / 10.0).ToString("0.0") + "G \u00B7 已改 " +
                                (modified / 10.0).ToString("0.0") + "G";
            }
            else
            {
                _memNote.Text = "待机 " + (standby / 10.0).ToString("0.0") + " GB \u00B7 已修改 " +
                                (modified / 10.0).ToString("0.0") + " GB";
            }
        }

        /// <summary>抖动说明行：AVG / MAX（\u00B5s，整数）。</summary>
        private void WriteJitNote(Snapshot s)
        {
            if (_jitNote == null) return;
            if (!s.JitterLive)
            {
                // 探针冷态：说明"没在测"，而不是显示 AVG 0 / MAX 0 冒充零抖动。
                if (_jitAvgShown != -1) { _jitAvgShown = -1; _jitMaxShown = -1; _jitNote.Text = _heroNarrow ? "探针待机" : "探针待机 \u00B7 切到本页即采样"; }
                return;
            }
            int avg = (int)Math.Round(s.JitterAvgUs);
            int max = (int)Math.Round(s.JitterMaxUs);
            if (avg == _jitAvgShown && max == _jitMaxShown) return;
            _jitAvgShown = avg;
            _jitMaxShown = max;
            if (_heroNarrow)
                _jitNote.Text = "AVG " + avg.ToString("0") + " \u00B7 MAX " + max.ToString("0") + " \u00B5s";
            else
                _jitNote.Text = "AVG " + avg.ToString("0") + " \u00B5s \u00B7 MAX " + max.ToString("0") + " \u00B5s";
        }

        // ---- 波形面板的三个统计标签：同一套变化检测 ------------------------
        // 前缀（含字距处理）在 Build 期算好一次，之后只有数字变了才拼一遍。
        private string _statAvgTag, _statP99Tag, _statMaxTag;
        private int _statAvgShown = int.MinValue, _statP99Shown = int.MinValue, _statMaxShown = int.MinValue;
        private bool _statP99Warn;

        private void WriteStatPills(Snapshot s)
        {
            if (_statAvg == null || s == null) return;

            if (!s.JitterLive)
            {
                // 冷态：三个统计标签显示 \u2014，不拿上一会话的冻值或 0 冒充实时。
                if (_statAvgShown != -1) { _statAvgShown = -1; _statAvg.Text = _statAvgTag + "\u2014"; }
                if (_statP99Shown != -1) { _statP99Shown = -1; _statP99.Text = _statP99Tag + "\u2014"; _statP99.Foreground = T.S(T.Dim); _statP99Warn = false; }
                if (_statMaxShown != -1) { _statMaxShown = -1; _statMax.Text = _statMaxTag + "\u2014"; }
                return;
            }

            int avg = (int)Math.Round(s.JitterAvgUs);
            if (avg != _statAvgShown)
            {
                _statAvgShown = avg;
                _statAvg.Text = _statAvgTag + avg.ToString("0");
            }

            int p99 = (int)Math.Round(s.JitterP99Us);
            if (p99 != _statP99Shown)
            {
                _statP99Shown = p99;
                _statP99.Text = _statP99Tag + p99.ToString("0");
                // 颜色只在跨过 1ms 阈值时改，不和文本一起每秒重写。
                bool warn = s.JitterP99Us > 1000;
                if (warn != _statP99Warn)
                {
                    _statP99Warn = warn;
                    _statP99.Foreground = T.S(warn ? T.Warn : T.Dim);
                }
            }

            int max = (int)Math.Round(s.JitterMaxUs);
            if (max != _statMaxShown)
            {
                _statMaxShown = max;
                _statMax.Text = _statMaxTag + max.ToString("0");
            }
        }

        // ---- 就绪度弧的颜色：三档，只在换档时写 ----------------------------
        // 以前每拍都写 Tint + Foreground：ArcRender 内部虽然会短路，但
        // TextBlock.Foreground 的 DP 写入每拍都要走一遍属性系统。
        private int _gaugeTier = -1;

        private void PaintGaugeTier(double score)
        {
            if (_gauge == null) return;
            int tier = score > 74 ? 2 : score > 48 ? 1 : 0;
            if (tier == _gaugeTier) return;
            _gaugeTier = tier;
            _gauge.Tint = tier == 2 ? T.Accent : tier == 1 ? T.Warn : T.Bad;
            _gauge.ValueText.Foreground = T.S(tier == 2 ? T.Hi : tier == 1 ? T.Warn : T.Bad);
        }

        private Action<double> _applyScore;
        private double _scoreShown = -1;

        private int _shownInt = -1;

        private void ApplyScore(double v)
        {
            _scoreShown = v;
            if (_gauge == null) return;
            _gauge.Value = v;
            // 46px 半粗中文/数字重新排版不便宜；补间每秒 17 帧，但数值只有整数会变。
            int iv = (int)Math.Round(v * 100);
            if (iv != _shownInt)
            {
                _shownInt = iv;
                _gauge.ValueText.Text = iv.ToString();
            }
        }

        private void ResetHeroCaches()
        {
            _heroNarrow = false;
            _hHead = 0;
            _hCount = 0;
            _cpuDpcTenth = int.MinValue;
            _cpuProcs = int.MinValue;
            _cpuGhzTenth = int.MinValue;
            _memStandbyTenth = int.MinValue;
            _memModifiedTenth = int.MinValue;
            _jitAvgShown = int.MinValue;
            _jitMaxShown = int.MinValue;
            _statAvgShown = int.MinValue;
            _statP99Shown = int.MinValue;
            _statMaxShown = int.MinValue;
            _statP99Warn = false;
            _gaugeTier = -1;
            _scoreShown = -1;
            _shownInt = -1;
        }
    }
}