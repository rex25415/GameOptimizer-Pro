// ============================================================================
//  Scheduler.cs — 03 调度与定时器：读数带 / 硅片矩阵 / 定时器与量子 / 参数审计
//
//  Rev86 重心：这一页原来是"三块各自为政的面板"——
//  矩阵自带一个 60 号 hero、定时器与量子另起一块、审计表再起一块，
//  三处面板头三种节奏，读者扫一眼不知道该先读哪个数。
//  现在顶部先给一条四格读数带（颗粒 / 掩码 / 活动核数 / 租约），
//  它们回答"现在是什么状态"；矩阵回答"这些状态长在哪几台处理器上"；
//  下排两块回答"为什么"和"还能怎么调"。
//
//  开销纪律不变：500ms 热路径只碰内存里的 Snapshot；注册表 5s 一次 +
//  动作后立即刷新；目标进程只在 pid 变化或动作后重新 OpenProcess。
//
//  文件布局：本文件放页根（字段 / Build / 页头 / 生命周期 / 断点 / 小工具）；
//  读数带与刷新链在 Scheduler.Readout.cs，矩阵在 Scheduler.Matrix.cs，
//  掩码读写、定时器、审计各在自己的分片里。
// ============================================================================
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Scheduler : Page
    {
        public override string Key { get { return "scheduler"; } }
        public override string Idx { get { return "03"; } }
        public override string Title { get { return "调度与定时器"; } }
        public override string En { get { return "Scheduler"; } }
        public override string Ico { get { return Glyph.Cpu; } }

        // ---- 尺度 ----------------------------------------------------------
        private const double CcdGap = 26;        // 必须与 SchedMatrix.ClusterGap 一致
        private const int SlowEvery = 10;        // 500ms × 10 = 5s（注册表 / 掩码的慢拍）
        private const int SparkSec = 60;         // 迷你走势窗口（秒；容量 = 60s / 0.5s）
        private const double SplitLeft = 420;    // 下排左栏（定时器与量子）的固定宽
        private const double SplitGap = 14;      // 区块间距：紧凑均衡

        private static readonly int SelfPid = Process.GetCurrentProcess().Id;

        // ---- 读数带 --------------------------------------------------------
        private TextBlock _bandTimer, _bandMask, _bandCores, _bandLease;
        private double _maskFont = T.FsNumL;   // 掩码读数的基准字号（64 位机器上更小）
        private TextBlock _bandTimerNote, _bandMaskNote, _bandCoresNote, _bandLeaseNote;
        private MaskStrip _maskStrip;
        private SegBar _coresBar, _leaseBar;
        private Sparkline _spTimer;
        private DeltaChip _dTimer;

        // 颗粒度的变化量基准：9 拍（约 4.5 秒）前。比 1 拍稳，又比 30 拍贴近当下。
        private const int TrendN = 24;
        private readonly double[] _hTimer = new double[TrendN];
        private int _hHead, _hCount;
        // 整数刻度缓存：只有刻度变了才拼字符串、才碰 TextBlock。
        private int _timerShown = int.MinValue;
        private int _deltaShown = int.MinValue;
        private string _deltaBody = "";
        private int _dpcShown = int.MinValue;
        private int _leaseShown = int.MinValue;
        private int _jitterP99 = int.MinValue;
        private int _jitterAvg = int.MinValue;
        private int _rangeFinestShown = int.MinValue;
        private int _rangeCoarsestShown = int.MinValue;

        // ---- 矩阵 ----------------------------------------------------------
        private SchedMatrix _matrix;
        private TextBlock _maskLine;
        private string _maskLineKey;
        private Badge _maskBadge;
        private TextBlock _allowText, _denyText;
        private Btn _btnPhys, _btnCcd0, _btnCcd1, _btnFree;

        // ---- 页头（DPC / ISR）-----------------------------------------------
        private MiniBar _dpcBar;
        private TextBlock _dpcVal;

        // ---- 定时器与量子 / 前台调度直调 ------------------------------------
        private TextBlock _leaseMeta;
        private Switch _swTimer;
        private TextBlock _tRange, _tJitter;
        private Run _prioRun;
        private TextBlock _prioHex, _prioNote;
        private TextBlock _procSchedTarget, _ecoStateText;
        private Badge _prioCurBadge;
        private Btn _btnPrioNormal, _btnPrioAbove, _btnPrioHigh;
        private Switch _swEco;
        private uint _targetPrio;
        private bool _targetEco;
        private bool _targetEcoKnown;
        private bool _ecoSuppress;

        // ---- 审计 ----------------------------------------------------------
        private TextBlock _auditMeta;
        private AuditRow[] _rows;

        // ---- 每簇读数 -------------------------------------------------------
        private Run[] _ccdRun;
        private TextBlock[] _ccdSub;
        private Sparkline[] _spark;
        private int[] _sparkCount;
        private int[] _sparkShown;
        private int[] _ccdShown;

        // ---- 版面（下排两栏；窄窗改上下两段）--------------------------------
        private Grid _split;
        private FrameworkElement _splitLeft, _splitRight;
        private bool _stacked;

        private DispatcherTimer _beat;
        private int _slowTick;

        // ---- 目标进程（读到的才是事实）--------------------------------------
        private int _seenForeign = -1;   // Core.LastForeignPid 上一次的取值
        private int _targetPid;          // 最近一次真正读了掩码的 pid
        private long _targetMask;        // 实测掩码；0 = 读不到
        private bool _targetKnown;
        private bool _targetSelf;        // 没有前台外部窗口时，目标是本工具自身
        private string _targetName = "";
        private DateTime _targetAt = DateTime.MinValue;

        // ====================================================================
        //  构建
        // ====================================================================
        public override UIElement Build(Core c)
        {
            if (_beat != null) _beat.Stop();
            _slowTick = 0;
            _hHead = 0;
            _hCount = 0;
            _timerShown = int.MinValue;
            _deltaShown = int.MinValue;
            _deltaBody = "";
            _dpcShown = int.MinValue;
            _leaseShown = int.MinValue;
            _jitterP99 = int.MinValue;
            _jitterAvg = int.MinValue;
            _rangeFinestShown = int.MinValue;
            _rangeCoarsestShown = int.MinValue;
            _maskLineKey = null;
            _stacked = false;
            _seenForeign = -1;
            _targetPid = 0;
            _targetMask = 0;
            _targetKnown = false;
            _targetSelf = false;
            _targetName = "";
            _targetAt = DateTime.MinValue;
            _targetPrio = 0;
            _targetEco = false;
            _targetEcoKnown = false;
            _ecoSuppress = false;

            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitGap) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Rev130：硅晶拓扑 C 位非对称舞台（左 1.56* 巨幅交互式 32 核硅晶矩阵 / 右 1* 2×2 四象限遥测卡）
            Grid heroStage = new Grid();
            heroStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.56, GridUnitType.Star) });
            heroStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitGap) });
            heroStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            UIElement matrix = BuildMatrixPanel(c);
            heroStage.Children.Add(matrix);

            UIElement band = BuildBand(c);
            Grid.SetColumn(band, 2);
            heroStage.Children.Add(band);
            body.Children.Add(heroStage);

            UIElement bottom = BuildBottom(c);
            Grid.SetRow(bottom, 2);
            body.Children.Add(bottom);

            // 断点：矩阵高度随宽度收放，下排两栏在窄窗改为上下两段。
            Rs.Watch(body, delegate (Bp bp) { ApplyBp(bp); });

            return Ui.PageRoot(BuildHeader(c), body);
        }

        /// <summary>下排：左＝定时器与量子（可操作），右＝参数审计（参考事实）。</summary>
        private UIElement BuildBottom(Core c)
        {
            _split = new Grid();
            _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitLeft) });
            _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitGap) });
            _split.ColumnDefinitions.Add(new ColumnDefinition());
            _split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _splitLeft = BuildTimerQuantum(c);
            Grid.SetColumn(_splitLeft, 0);
            _split.Children.Add(_splitLeft);

            _splitRight = BuildAudit(c);
            Grid.SetColumn(_splitRight, 2);
            _split.Children.Add(_splitRight);
            return _split;
        }

        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Bottom;
            right.Margin = new Thickness(0, 0, 0, 2);

            // 拓扑是一条常量读数：它不会每秒变，所以放在页头一次成型。
            string topo = Math.Max(1, c.Topo.Ccds) + " CCD · " + Math.Max(1, c.Topo.CoresPerCcd)
                        + "C / " + Math.Max(1, c.Topo.CoresPerCcd * c.Topo.ThreadsPerCore) + "T · "
                        + c.Topo.Logical + " 逻辑处理器";
            TextBlock t = Ui.Mono(topo, T.FsNano, T.Mute);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(t);

            // DPC / ISR：Snapshot 一直在算这个数（Perf.cs 的 DpcPercent），此前只有页头在显示。
            StackPanel dpc = new StackPanel();
            dpc.Orientation = Orientation.Horizontal;
            dpc.HorizontalAlignment = HorizontalAlignment.Right;
            dpc.Margin = new Thickness(0, 4, 0, 0);
            TextBlock dl = Ui.Label("DPC / ISR", T.Mute);
            dl.Margin = new Thickness(0, 0, 8, 0);
            dpc.Children.Add(dl);
            _dpcBar = new MiniBar(5.0);          // 量程 0–5%，超过就顶格（数值仍照实显示）
            _dpcBar.Width = 86;
            _dpcBar.Height = 4;
            _dpcBar.VerticalAlignment = VerticalAlignment.Center;
            _dpcBar.Margin = new Thickness(0, 0, 8, 0);
            dpc.Children.Add(_dpcBar);
            _dpcVal = Ui.Mono("—", T.FsNano, T.Dim);
            dpc.Children.Add(_dpcVal);
            Tip.Attach(dpc, "DPC / ISR 占整机 CPU 时间的比例（NtQuerySystemInformation class 8 的 DpcTime 差分）。\r\n量程 0–5%，超过就顶格；数值始终照实显示。这个数持续偏高通常指向驱动或外设中断问题。");
            right.Children.Add(dpc);

            return Ui.PageHeader("MODULE / 03", "调度与定时器",
                "CCD LOAD · FOREGROUND AFFINITY MASK · NtSetTimerResolution MEASURED", right);
        }

        // ====================================================================
        //  刷新：快拍（内存 Snapshot）与慢拍（注册表 / 掩码）
        // ====================================================================
        public override void OnEnter()
        {
            if (C != null && C.Perf != null) C.Perf.JitterHot = true;   // 本页显示抖动 P99
            SyncTarget(true);   // 进页立即复核掩码 / 优先级 / EcoQoS 真值
            RefreshSlow();      // 进页立即读一次注册表与掩码
            RefreshFast();
            if (_beat == null)
            {
                _beat = new DispatcherTimer();
                _beat.Interval = TimeSpan.FromMilliseconds(500);
                _beat.Tick += delegate { OnBeat(); };
                _slowTick = 0;
            }
            _beat.Start();
        }

        public override void OnLeave()
        {
            if (C != null && C.Perf != null) C.Perf.JitterHot = false;
            if (_beat != null) _beat.Stop();
            // 定时器租约是会话级设置，离开本页不撤销：
            // 旧实现里这里会"顺手"释放，把其它页面已经申请的精度一起打掉。
        }

        public override void Tick(Snapshot s) { }

        private void OnBeat()
        {
            RefreshFast();
            // 注册表不再跟着 500ms 热路径走：10 拍（5 秒）读一次，
            // 用户点动作后另有立即刷新。实测稳态 1.2 次/秒打开，而不是 5.4 次/秒。
            _slowTick++;
            if (_slowTick >= SlowEvery) { _slowTick = 0; RefreshSlow(); }
        }

        private static void Set(TextBlock t, string s)
        {
            if (t != null && t.Text != s) t.Text = s;
        }

        private static void SetRun(Run r, string s)
        {
            if (r != null && r.Text != s) r.Text = s;
        }

        // ---- 断点 ------------------------------------------------------------
        private void ApplyBp(Bp bp)
        {
            if (_matrix != null)
                _matrix.Height = bp == Bp.Compact ? 96 : (bp == Bp.Regular ? 102 : 108);

            // 掩码是 10~18 个字符的十六进制：窄窗里 32 号字会溢出到下一格，
            // 所以两档收窄只降这一格的字号——其余三格的读数长度是固定的。
            if (_bandMask != null)
            {
                double want = (bp == Bp.Compact || bp == Bp.Regular) ? T.FsTitle : _maskFont;
                if (Math.Abs(_bandMask.FontSize - want) > 0.1) _bandMask.FontSize = want;
            }

            if (_split == null) return;
            // Regular（900 以下）就上下叠：420 的左栏 + 被挤到 440 的审计表，
            // 后者的"参数 / 当前值 / 期望值 / 结论"四列会互相压字。
            bool stack = bp == Bp.Compact || bp == Bp.Regular;
            if (stack == _stacked) return;
            _stacked = stack;

            _split.ColumnDefinitions.Clear();
            _split.RowDefinitions.Clear();
            if (stack)
            {
                _split.ColumnDefinitions.Add(new ColumnDefinition());
                _split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitGap) });
                _split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(_splitLeft, 0); Grid.SetRow(_splitLeft, 0);
                Grid.SetColumn(_splitRight, 0); Grid.SetRow(_splitRight, 2);
                _splitLeft.Margin = new Thickness(0);
                _splitRight.Margin = new Thickness(0);
            }
            else
            {
                _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitLeft) });
                _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitGap) });
                _split.ColumnDefinitions.Add(new ColumnDefinition());
                _split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(_splitLeft, 0); Grid.SetRow(_splitLeft, 0);
                Grid.SetColumn(_splitRight, 2); Grid.SetRow(_splitRight, 0);
                _splitLeft.Margin = new Thickness(0);
                _splitRight.Margin = new Thickness(0);
            }
        }
    }
}
