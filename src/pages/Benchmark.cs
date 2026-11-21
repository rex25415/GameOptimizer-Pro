// ============================================================================
//  Benchmark.cs — 09 基准测试：8.0 秒唤醒超调采样 / 自适应分箱直方图 / 同口径复测
//
//  Rev86 改的是"读法"，不是配色。
//  1) 这一页是流程型的（准备 → 运行 → 结果 → 存档），旧版把状态藏在一枚
//     "STEP 2/3" 的小字里：读者不知道现在在哪一步、还要多久、跑完的数据去了哪。
//     现在卡片第一行就是四步状态机（当前步高亮 / 已完成打勾 / 未到留灰），右侧
//     运行钟写"已用 / 预计剩余"；一个样本都没取到时写"计时中"，不画假进度。
//  2) 结果区只允许一个主指标：P99（FsHero）。下面三格 Ui.Stat 回答"为什么是这个数"：
//     P1 是地板、P50 是典型、σ 是离散；再下面是 48 箱直方图（X = 唤醒超调 µs，
//     Y = 样本数，P99 位置带数值标注）。
//  3) 读数与绘制分离：这一片只搭视觉树；状态到画面的映射在 Benchmark.Run / .Verdict。
//
//  REV80 的采样口径保持不变（按实测缺陷重做过，别改回去）：
//   旧直方图把 24 个桶钉在"整数档"量程上，本机样本稳定在 14.3–15.6 ms 时全部落进
//   最后两三箱，左边三分之二空着——读者看到的不是分布，是"数据坏了"。现在按
//   [min,max] ±5% 自适应定域、48 个等宽箱；样本 < 2 或零方差给空态，绝不画一条竖线。
//
//  数字纪律：所有读数来自实测样本或历史记录；没有就显示 "—"。
//  不裁剪异常值：分箱域覆盖 [min,max]，域外计数与 far-out 计数单列在图表脚注。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Benchmark : Page
    {
        public override string Key { get { return "bench"; } }
        public override string Idx { get { return "09"; } }
        public override string Title { get { return "\u57FA\u51C6\u6D4B\u8BD5"; } }
        public override string En { get { return "Benchmark"; } }
        public override string Ico { get { return Glyph.Benchmark; } }

        /// <summary>采样窗口（秒）。判定与对比都以这个窗口为准，不同窗口不可比。</summary>
        private const double WindowSec = 8.0;
        /// <summary>低于这个样本数视为本次不可用：分位数在更小的样本上没有意义。</summary>
        private const int MinSamples = 8;
        /// <summary>历史列表一次显示的最大条数。</summary>
        private const int ListMax = 8;
        /// <summary>噪声阈值取最近多少次运行（不含本次）。</summary>
        private const int NoiseWindow = 8;

        private enum Phase { Idle, Running, Done, Aborted, Failed }

        // ---- 流程状态机 -----------------------------------------------------
        // 四步是固定的：顺序、中文名、英文小标都只在这里写一次。
        // 索引即步骤，所以"第几步"不需要再存一个 int——它由 Phase 直接映射（见 PaintSteps）。
        private const int StepCount = 4;
        private const int StepFuture = 0;   // 还没到
        private const int StepCurrent = 1;  // 现在在这一步
        private const int StepDone = 2;     // 已经走完
        private static readonly string[] StepCn = new string[] { "\u51C6\u5907", "\u8FD0\u884C", "\u7ED3\u679C", "\u5B58\u6863" };
        private static readonly string[] StepEn = new string[] { "PREPARE", "MEASURE", "RESULT", "ARCHIVE" };

        // ====================================================================
        //  数据
        // ====================================================================

        /// <summary>一次采样窗口的统计摘要。全部字段由样本直接算出，没有估算值。</summary>
        private sealed class Stats
        {
            public int Count;
            public double Min, Max, Mean;
            public double P1, P25, P50, P75, P95, P99;
            public double Sigma;      // 窗口内总体标准差
            public int FarOut;        // 极端离群样本数（Tukey far-out：> P75 + 3×IQR）

            public void Reset()
            {
                Count = 0; Min = 0; Max = 0; Mean = 0;
                P1 = 0; P25 = 0; P50 = 0; P75 = 0; P95 = 0; P99 = 0;
                Sigma = 0; FarOut = 0;
            }

            public void CopyFrom(Stats o)
            {
                Count = o.Count; Min = o.Min; Max = o.Max; Mean = o.Mean;
                P1 = o.P1; P25 = o.P25; P50 = o.P50; P75 = o.P75; P95 = o.P95; P99 = o.P99;
                Sigma = o.Sigma; FarOut = o.FarOut;
            }
        }

        /// <summary>
        /// 一条可回看的运行。本次会话内跑出来的带完整样本明细（可重画直方图）；
        /// 历史档案只存聚合读数，明细为空 —— 这一点必须显示出来，不能假装能重画。
        /// </summary>
        private sealed class RunData
        {
            public string Time = "";
            public string Mode = "wake";
            public double P50, P99, Max, TimerMs;
            public double BandwidthMBs, MemLatNs;
            public int Count;
            public string Note = "";
            public double[] Sorted;   // null = 历史档案未保存样本明细
            public Stats St;          // 同上
            public MemoryBenchResult MemRes;
            public PingPongResult PpRes;
        }

        // ---- 实时状态 -------------------------------------------------------
        private readonly List<double> _samples = new List<double>();
        private readonly List<RunData> _runs = new List<RunData>();
        private readonly Stopwatch _sw = new Stopwatch();
        private readonly Stats _live = new Stats();
        private Stats _cur;               // 本次已完成的统计
        private RunData _curRun;          // 本次已完成的那一条
        private RunData _chartRun;        // 直方图当前显示的那一条
        private RunData _baseRun;         // 同口径 A/B 手动选中的对照基线
        private double[] _sortBuf = new double[2048];
        private int _sortN;
        private long _cursor;
        private Phase _phase = Phase.Idle;
        private bool _autoStarted;
        private int _sel = -1;            // 历史列表选中项（-1 = 跟随本次）
        private string _mode = "wake";
        private int _bgRunToken;

        // ---- 控件 -----------------------------------------------------------
        private Grid _pageRoot;
        private Btn _btnSample, _btnAbort, _btnClearBase;
        private Ui.SegTabs _modeTabs;
        private StackPanel _abHost, _modeVizHost;
        private DeltaChip _abChip;
        private TextBlock _abNote;
        private TextBlock _hdrKick, _hdrVal, _hdrUnit, _hdrDelta;
        private TextBlock _heroVal, _heroDelta, _heroDeltaNote, _heroTag;
        private Border _heroGradeBox;
        private TextBlock _heroGradeLetter, _heroGradeSub;
        private Run _heroValRun, _heroUnitRun;
        private TextBlock _stP1, _stP50, _stSigma;
        private TextBlock[] _stLbl, _stUnit, _stNote;
        private TextBlock _footRun, _footMeta;
        private BenchmarkHistogram _hist;
        private Border _histBed;
        private TextBlock _chartTitle, _chartSub, _chartMeta, _chartFoot;

        // 步骤条：四个方框 / 序号 / 中文名 / 英文名，以及两枚状态缓存（只在变化时写）。
        private Border[] _stepBox;
        private TextBlock[] _stepGlyph;
        private TextBlock[] _stepName;
        private TextBlock[] _stepEn;
        private int[] _stepShown;
        private int[] _stepNext;
        private uint _stepTint;
        private ColumnDefinition _clockCol;
        private TextBlock _clockUse, _clockEta;
        private SegBar _stepBar;
        private int _clockKey = int.MinValue;

        // ====================================================================
        //  构建
        // ====================================================================
        public override UIElement Build(Core c)
        {
            LoadRuns();
            ResetPaintCaches();

            // 区块间距统一 18（PageRoot 的页头间隔也是 18）：跨页换行时标题基线才不会跳。
            StackPanel body = new StackPanel();
            body.Children.Add(BuildModeBar());
            body.Children.Add(Gap(10));
            body.Children.Add(BuildHero(c));
            body.Children.Add(Gap(12));
            body.Children.Add(BuildChartPanel(c));
            body.Children.Add(Gap(12));
            body.Children.Add(BuildBottom(c));
            body.Children.Add(Gap(12));
            body.Children.Add(BuildTimerPanel(c));

            _pageRoot = Ui.PageRoot(BuildHeader(c), body);
            Rs.Watch(_pageRoot, OnBreakpoint);
            OnBreakpoint(Bp.Wide);

            UpdateModeChrome();
            UpdateRunButton();
            RefreshHistory();
            PaintAll();
            return _pageRoot;
        }

        /// <summary>断点：hero 字阶、运行钟宽度与图表高度随内容宽度收敛。</summary>
        private void OnBreakpoint(Bp bp)
        {
            if (_heroValRun != null)
            {
                double s = T.FsHero;
                if (bp == Bp.Compact) s = 40;
                else if (bp == Bp.Regular) s = 54;
                else if (bp == Bp.Wide) s = 64;
                _heroValRun.FontSize = s;
                _heroUnitRun.FontSize = Math.Round(s * 0.30);
            }
            if (_clockCol != null)
            {
                double w = bp == Bp.Compact ? 188 : (bp == Bp.Regular ? 220 : 280);
                if (Math.Abs(_clockCol.Width.Value - w) > 0.5) _clockCol.Width = new GridLength(w);
            }
            if (_hist != null)
            {
                double hgt = (bp == Bp.Ultra || bp == Bp.Wide) ? 196 : 168;
                if (Math.Abs(_hist.Height - hgt) > 0.5) _hist.Height = hgt;
            }
        }

        // ---- 页头 -----------------------------------------------------------
        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            // 页头右侧摘要：最近一次完成的 P99 + 与上一次的差。
            StackPanel sum = new StackPanel();
            sum.VerticalAlignment = VerticalAlignment.Center;
            sum.Margin = new Thickness(0, 0, 16, 0);

            _hdrKick = Ui.Label("LAST RUN / P99", T.Mute);
            _hdrKick.HorizontalAlignment = HorizontalAlignment.Right;
            sum.Children.Add(_hdrKick);

            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            line.HorizontalAlignment = HorizontalAlignment.Right;
            line.Margin = new Thickness(0, 4, 0, 0);

            _hdrVal = Ui.Mono("\u2014", T.FsTitle, T.Mute);
            line.Children.Add(_hdrVal);

            _hdrUnit = Ui.Mono(" \u00B5s", T.FsNano, T.Dim);
            _hdrUnit.VerticalAlignment = VerticalAlignment.Bottom;
            _hdrUnit.Margin = new Thickness(3, 0, 0, 3);
            line.Children.Add(_hdrUnit);

            _hdrDelta = Ui.Mono("", T.FsNano, T.Mute);
            _hdrDelta.VerticalAlignment = VerticalAlignment.Bottom;
            _hdrDelta.Margin = new Thickness(9, 0, 0, 3);
            line.Children.Add(_hdrDelta);
            sum.Children.Add(line);
            right.Children.Add(sum);

            _btnSample = new Btn("\u5F00\u59CB\u91C7\u6837", BtnKind.Primary);
            _btnSample.Height = T.BtnHLg;
            _btnSample.SetIcon(Glyph.Play, 12);
            _btnSample.VerticalAlignment = VerticalAlignment.Center;
            _btnSample.Click += delegate { StartSample(); };
            right.Children.Add(_btnSample);

            return Ui.PageHeader("MODULE / 09", "\u57FA\u51C6\u6D4B\u8BD5",
                "\u5524\u9192\u8D85\u8C03 \u00B7 \u5185\u5B58\u5E26\u5BBD\u4E0E\u5EF6\u8FDF \u00B7 \u7EBF\u7A0B\u8C03\u5EA6\u4E52\u4E53 \u00B7 \u540C\u53E3\u5F84 A/B \u5BF9\u7167", right);
        }

        private UIElement BuildModeBar()
        {
            Grid bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition());

            Ui.SegTabItem[] items = new Ui.SegTabItem[]
            {
                new Ui.SegTabItem("wake", "\u5524\u9192\u8D85\u8C03 (8.0s)", "8.0 \u79D2\u7CFB\u7EDF\u5524\u9192\u8D85\u8C03\u91C7\u6837\u4E0E 48 \u7BB1\u76F4\u65B9\u56FE"),
                new Ui.SegTabItem("mem", "\u5185\u5B58\u5E26\u5BBD\u4E0E\u5EF6\u8FDF", "\u987A\u5E8F\u5185\u5B58\u62F7\u8D1D\u541E\u5410 (MB/s) \u4E0E\u968F\u673A\u6307\u9488\u8FFD\u9010\u5EF6\u8FDF (ns)"),
                new Ui.SegTabItem("pingpong", "\u7EBF\u7A0B\u8C03\u5EA6\u4E52\u4E53", "\u53CC\u7EBF\u7A0B\u6838\u95F4\u5524\u9192\u5F80\u8FD4 P50 / P99 / Max \u5EF6\u8FDF (\u00B5s)")
            };
            _modeTabs = new Ui.SegTabs(items, ModeIndex(_mode));
            _modeTabs.Changed += delegate { SwitchMode(_modeTabs.SelectedKey); };
            bar.Children.Add(_modeTabs);

            _abHost = new StackPanel();
            _abHost.Orientation = Orientation.Horizontal;
            _abHost.HorizontalAlignment = HorizontalAlignment.Right;
            _abHost.VerticalAlignment = VerticalAlignment.Center;

            _abHost.Children.Add(Ui.Label("THIS vs BASE", T.Mute));

            _abChip = new DeltaChip();
            _abChip.Margin = new Thickness(8, 0, 0, 0);
            _abHost.Children.Add(_abChip);

            _abNote = Ui.Mono("\u70B9\u9009\u4E0B\u65B9\u5386\u53F2\u8BB0\u5F55\u53EF\u8BBE\u5BF9\u7167\u57FA\u7EBF", T.FsNano, T.Mute);
            _abNote.Margin = new Thickness(8, 0, 0, 0);
            _abNote.VerticalAlignment = VerticalAlignment.Center;
            _abNote.TextTrimming = TextTrimming.CharacterEllipsis;
            _abHost.Children.Add(_abNote);

            _btnClearBase = new Btn("\u6E05\u9664\u57FA\u7EBF", BtnKind.Ghost);
            _btnClearBase.Height = T.BtnHSm;
            _btnClearBase.Compact = true;
            _btnClearBase.Margin = new Thickness(8, 0, 0, 0);
            _btnClearBase.Visibility = Visibility.Collapsed;
            _btnClearBase.Click += delegate
            {
                _baseRun = null;
                _sel = -1;
                if (_mode == "wake") _chartRun = (_curRun != null && _curRun.Mode == "wake") ? _curRun : LastWithSamples();
                PaintAll();
                RefreshHistory();
            };
            _abHost.Children.Add(_btnClearBase);

            Grid.SetColumn(_abHost, 1);
            bar.Children.Add(_abHost);
            return bar;
        }

        // ---- 主卡：步骤条 / 主读数 / 注脚 ------------------------------------
        private UIElement BuildHero(Core c)
        {
            Grid inner = new Grid();
            for (int i = 0; i < 5; i++) inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            inner.Children.Add(BuildSteps());

            Border r1 = Ui.Hairline(12);
            Grid.SetRow(r1, 1);
            inner.Children.Add(r1);

            Grid main = BuildMain();
            Grid.SetRow(main, 2);
            inner.Children.Add(main);

            Border r2 = Ui.Hairline(12);
            Grid.SetRow(r2, 3);
            inner.Children.Add(r2);

            Grid foot = BuildFoot();
            Grid.SetRow(foot, 4);
            inner.Children.Add(foot);

            Grid hero = new Grid();
            hero.Children.Add(Ui.HeroPanel(inner, T.PadPanel));
            hero.Children.Add(Ui.CornerTicks(T.AccentA(0x80), 10, 1.5));
            return hero;
        }

        // ---- 步骤条：四步状态机 + 运行钟 -------------------------------------
        private Grid BuildSteps()
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            _clockCol = new ColumnDefinition { Width = new GridLength(280) };
            row.ColumnDefinitions.Add(_clockCol);

            row.Children.Add(BuildStepTrack());

            StackPanel clock = BuildClock();
            clock.Margin = new Thickness(24, 0, 0, 0);
            Grid.SetColumn(clock, 1);
            row.Children.Add(clock);
            return row;
        }

        /// <summary>
        /// 四个步骤按 2/1/2 的列宽比例排开：步骤格按内容自适应，连接线吃掉剩余宽度。
        /// 连接线不是装饰——它把"步骤"读成一条件，而不是四个互不相干的标签。
        /// </summary>
        private UIElement BuildStepTrack()
        {
            Grid track = new Grid();
            _stepBox = new Border[StepCount];
            _stepGlyph = new TextBlock[StepCount];
            _stepName = new TextBlock[StepCount];
            _stepEn = new TextBlock[StepCount];
            _stepShown = new int[StepCount];
            _stepNext = new int[StepCount];

            for (int i = 0; i < StepCount; i++)
            {
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                StackPanel cell = new StackPanel();
                cell.Orientation = Orientation.Horizontal;
                cell.VerticalAlignment = VerticalAlignment.Center;

                _stepBox[i] = new Border();
                _stepBox[i].Width = 20;
                _stepBox[i].Height = 20;
                _stepBox[i].CornerRadius = new CornerRadius(T.RadiusSm);
                _stepBox[i].BorderThickness = new Thickness(1);
                _stepBox[i].VerticalAlignment = VerticalAlignment.Center;

                _stepGlyph[i] = Ui.Mono((i + 1).ToString(), T.FsNano, T.Mute);
                _stepGlyph[i].HorizontalAlignment = HorizontalAlignment.Center;
                _stepGlyph[i].VerticalAlignment = VerticalAlignment.Center;
                _stepBox[i].Child = _stepGlyph[i];
                cell.Children.Add(_stepBox[i]);

                StackPanel txt = new StackPanel();
                txt.Margin = new Thickness(9, 0, 0, 0);
                txt.VerticalAlignment = VerticalAlignment.Center;

                _stepName[i] = Ui.Tx(StepCn[i], T.FsSmall, T.Mute, T.FCn);
                txt.Children.Add(_stepName[i]);

                _stepEn[i] = Ui.Mono(Ui.Spaced(StepEn[i]), T.FsNano, T.Mute);
                _stepEn[i].Margin = new Thickness(0, 2, 0, 0);
                _stepEn[i].Opacity = 0.75;
                txt.Children.Add(_stepEn[i]);
                cell.Children.Add(txt);

                Grid.SetColumn(cell, i * 2);
                track.Children.Add(cell);
                _stepShown[i] = -1;   // 首帧强制走一遍绘制

                if (i == StepCount - 1) break;
                track.ColumnDefinitions.Add(new ColumnDefinition());
                Border link = new Border();
                link.Height = 1;
                link.Background = T.S(T.LineSoft);
                link.VerticalAlignment = VerticalAlignment.Center;
                link.Margin = new Thickness(12, 0, 12, 0);
                Grid.SetColumn(link, i * 2 + 1);
                track.Children.Add(link);
            }
            return track;
        }

        /// <summary>运行钟：已用 / 预计剩余 / 进度 / 中止。运行中才出现，其余状态留空占位。</summary>
        private StackPanel BuildClock()
        {
            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            _clockUse = Ui.Mono("\u5F85\u91C7\u6837", T.FsTitle, T.Mute);
            _clockUse.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(_clockUse);

            _clockEta = Ui.Mono("\u7A97\u53E3 8.0 S \u00B7 \u81F3\u5C11 " + MinSamples + " \u6837\u672C", T.FsNano, T.Mute);
            _clockEta.Margin = new Thickness(0, 4, 0, 0);
            _clockEta.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(_clockEta);

            Grid barRow = new Grid();
            barRow.Margin = new Thickness(0, 9, 0, 0);
            barRow.ColumnDefinitions.Add(new ColumnDefinition());
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _stepBar = new SegBar(0);
            _stepBar.Height = 3;
            _stepBar.Segments = 24;
            _stepBar.Tint = T.Accent;
            _stepBar.VerticalAlignment = VerticalAlignment.Center;
            _stepBar.Visibility = Visibility.Hidden;
            barRow.Children.Add(_stepBar);

            _btnAbort = new Btn("\u4E2D\u6B62", BtnKind.Danger);
            _btnAbort.Width = 56;
            _btnAbort.Height = T.BtnH;
            _btnAbort.Compact = true;
            _btnAbort.Margin = new Thickness(12, 0, 0, 0);
            _btnAbort.VerticalAlignment = VerticalAlignment.Center;
            _btnAbort.Visibility = Visibility.Collapsed;
            _btnAbort.Click += delegate { AbortRun(); };
            Grid.SetColumn(_btnAbort, 1);
            barRow.Children.Add(_btnAbort);

            sp.Children.Add(barRow);
            return sp;
        }

        // ---- 主读数：P99 + 三格 ----------------------------------------------
        private Grid BuildMain()
        {
            Grid main = new Grid();
            main.Margin = new Thickness(0, 12, 0, 0);
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            main.ColumnDefinitions.Add(new ColumnDefinition());

            StackPanel left = new StackPanel();
            left.Margin = new Thickness(0, 0, 30, 0);
            left.VerticalAlignment = VerticalAlignment.Top;

            Grid tagRow = new Grid();
            tagRow.ColumnDefinitions.Add(new ColumnDefinition());
            tagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _heroTag = Ui.Label("THIS RUN / P99", T.AccentDim);
            _heroTag.VerticalAlignment = VerticalAlignment.Center;
            tagRow.Children.Add(_heroTag);

            // Rev130：竞技跑分主舞台评级徽章（S / A / B / C 实时联动）
            _heroGradeBox = new Border();
            _heroGradeBox.CornerRadius = new CornerRadius(T.RadiusMd);
            _heroGradeBox.BorderThickness = new Thickness(1);
            _heroGradeBox.BorderBrush = T.S(T.LineMid);
            _heroGradeBox.Background = T.S(T.Inset);
            _heroGradeBox.Padding = new Thickness(9, 2, 9, 2);
            _heroGradeBox.Margin = new Thickness(12, 0, 0, 0);
            StackPanel gsp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _heroGradeLetter = Ui.Num("\u2014", 15, T.Dim);
            _heroGradeLetter.VerticalAlignment = VerticalAlignment.Center;
            gsp.Children.Add(_heroGradeLetter);
            _heroGradeSub = Ui.Mono("", T.FsNano, T.Mute);
            _heroGradeSub.VerticalAlignment = VerticalAlignment.Center;
            _heroGradeSub.Margin = new Thickness(6, 0, 0, 0);
            gsp.Children.Add(_heroGradeSub);
            _heroGradeBox.Child = gsp;
            Grid.SetColumn(_heroGradeBox, 1);
            tagRow.Children.Add(_heroGradeBox);
            left.Children.Add(tagRow);

            // 主指标只有一个：P99 唤醒超调。字阶随断点收敛，数字用 Ideal 排版模式
            // （大字号下 Display 的整数像素对齐会把 "14,954" 的字距拉得不匀）。
            _heroVal = new TextBlock();
            _heroVal.FontFamily = T.FDisplay;
            _heroVal.FontWeight = FontWeights.SemiBold;
            TextOptions.SetTextFormattingMode(_heroVal, TextFormattingMode.Ideal);
            _heroVal.Margin = new Thickness(0, 4, 0, 0);

            _heroValRun = new Run("\u2014");
            _heroValRun.FontSize = T.FsHero;
            _heroValRun.Foreground = T.S(T.Mute);
            _heroVal.Inlines.Add(_heroValRun);

            _heroUnitRun = new Run(" \u00B5s");
            _heroUnitRun.FontSize = Math.Round(T.FsHero * 0.30);
            _heroUnitRun.FontFamily = T.FMono;
            _heroUnitRun.Foreground = T.S(T.Mute);
            _heroVal.Inlines.Add(_heroUnitRun);
            left.Children.Add(_heroVal);

            StackPanel drow = new StackPanel();
            drow.Orientation = Orientation.Horizontal;
            drow.Margin = new Thickness(0, 6, 0, 0);
            _heroDelta = Ui.Mono("", T.FsSmall, T.Mute);
            drow.Children.Add(_heroDelta);
            _heroDeltaNote = Ui.Tx("", T.FsMicro, T.Mute, T.FCn);
            _heroDeltaNote.Margin = new Thickness(10, 0, 0, 0);
            _heroDeltaNote.TextTrimming = TextTrimming.CharacterEllipsis;
            drow.Children.Add(_heroDeltaNote);
            left.Children.Add(drow);

            main.Children.Add(left);

            Rectangle div = new Rectangle();
            div.Fill = T.S(T.Line);
            div.Margin = new Thickness(0, 4, 0, 4);
            div.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(div, 1);
            main.Children.Add(div);

            Grid stats = new Grid();
            stats.VerticalAlignment = VerticalAlignment.Top;
            for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());

            _stP1 = Ui.Mono("\u2014", T.FsNumL, T.Mute);
            _stP50 = Ui.Mono("\u2014", T.FsNumL, T.Mute);
            _stSigma = Ui.Mono("\u2014", T.FsNumL, T.Mute);

            TextBlock[] vals = new TextBlock[] { _stP1, _stP50, _stSigma };
            string[] labels = new string[] { "P1", "P50", "JITTER" };
            string[] units = new string[] { "\u00B5s", "\u00B5s", "\u00B5s" };
            string[] notes = new string[]
            {
                "\u6700\u5FEB\u7684\u4E00\u6B21 \u00B7 \u672C\u6B21\u7A97\u53E3\u7684\u5730\u677F",
                "\u4E2D\u4F4D \u00B7 \u5178\u578B\u4E00\u6B21\u5524\u9192",
                "\u6807\u51C6\u5DEE \u03C3 \u00B7 \u8D8A\u5927\u8D8A\u4E0D\u7A33"
            };
            _stLbl = new TextBlock[3];
            _stUnit = new TextBlock[3];
            _stNote = new TextBlock[3];

            uint[] tileTints = new uint[] { T.Ok, T.Accent, T.Warn };
            for (int i = 0; i < 3; i++)
            {
                _stUnit[i] = Ui.Mono(units[i], T.FsNano, T.Dim);
                _stNote[i] = Ui.Note(notes[i]);
                StackPanel cell = Ui.Stat(labels[i], Ui.Readout(vals[i], _stUnit[i]), null, _stNote[i]);
                _stLbl[i] = cell.Children[0] as TextBlock;
                Border tile = Ui.GlassTile(cell, 13, tileTints[i]);
                if (i > 0) tile.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(tile, i);
                stats.Children.Add(tile);
            }
            stats.Margin = new Thickness(22, 0, 0, 0);
            Grid.SetColumn(stats, 2);
            main.Children.Add(stats);
            return main;
        }

        /// <summary>注脚：左边是这一次的派生读数，右边是这台仪器的口径。</summary>
        private Grid BuildFoot()
        {
            Grid foot = new Grid();
            foot.Margin = new Thickness(0, 8, 0, 0);
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _footRun = Ui.Mono("", T.FsNano, T.Mute);
            _footRun.TextTrimming = TextTrimming.CharacterEllipsis;
            foot.Children.Add(_footRun);

            _footMeta = Ui.Mono("\u5524\u9192\u8D85\u8C03 = Thread.Sleep(1) \u5B9E\u9645\u5524\u9192 \u2212 1 ms \u00B7 \u7A97\u53E3 8.0 S \u00B7 48 \u7BB1", T.FsNano, T.Mute);
            _footMeta.HorizontalAlignment = HorizontalAlignment.Right;
            _footMeta.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(_footMeta, 1);
            foot.Children.Add(_footMeta);
            return foot;
        }

        // ---- 图表面板（唤醒直方图 / 内存与乒乓对照趋势）---------------------
        private UIElement BuildChartPanel(Core c)
        {
            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel hl = new StackPanel();
            StackPanel trow = new StackPanel();
            trow.Orientation = Orientation.Horizontal;
            Border tick = new Border();
            tick.Width = 2; tick.Height = 13; tick.Background = T.S(T.Accent);
            tick.Margin = new Thickness(0, 0, 9, 1);
            tick.VerticalAlignment = VerticalAlignment.Bottom;
            trow.Children.Add(tick);
            _chartTitle = Ui.Tx("\u5524\u9192\u8D85\u8C03\u5206\u5E03", T.FsTitle, T.Hi, T.FCn);
            _chartTitle.FontWeight = FontWeights.SemiBold;
            trow.Children.Add(_chartTitle);
            hl.Children.Add(trow);

            _chartSub = Ui.Label("WAKE OVERSHOOT / MICROSECONDS \u00B7 48 BINS", T.Mute);
            _chartSub.Margin = new Thickness(11, 4, 0, 0);
            _chartSub.Opacity = 0.8;
            hl.Children.Add(_chartSub);
            head.Children.Add(hl);

            _chartMeta = Ui.Mono("", T.FsNano, T.Mute);
            _chartMeta.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_chartMeta, 1);
            head.Children.Add(_chartMeta);
            body.Children.Add(head);
            body.Children.Add(Gap(8));

            _hist = new BenchmarkHistogram();
            _hist.Height = 196;
            _histBed = Ui.Inset(_hist, 6);
            body.Children.Add(_histBed);

            _modeVizHost = new StackPanel();
            _modeVizHost.Visibility = Visibility.Collapsed;
            body.Children.Add(_modeVizHost);

            body.Children.Add(Gap(6));
            _chartFoot = Ui.Mono("", T.FsNano, T.Mute);
            _chartFoot.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(_chartFoot);
            return Ui.Panel(body, T.PadPanel);
        }
    }
}