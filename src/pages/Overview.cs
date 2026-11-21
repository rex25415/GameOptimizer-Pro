// ============================================================================
//  Overview.cs — 01 态势总览：类声明 / 页根 / 生命周期 / 1Hz 节拍（概览主片）
//  来源：Overview.cs（862 行单体）按职责拆分，partial 共享字段，行为零变化。
//  本片只放"页是谁、页怎么拼起来、页活多久、每秒那一拍做什么"；诊断条是页面级横幅，也留在页根。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
using Go.Daemon;

namespace Go.Pages
{
    public partial class Overview : Page
    {
        private static readonly int SelfPid = Process.GetCurrentProcess().Id;

        public override string Key { get { return "overview"; } }
        public override string Idx { get { return "01"; } }
        public override string Title { get { return "态势总览"; } }
        public override string En { get { return "Situation"; } }
        public override string Ico { get { return Glyph.Overview; } }

        private ArcGauge _gauge;
        private Waveform _wave;
        private TextBlock _cpuVal, _memVal, _jitVal, _tmrVal;
        private NumTicker _cpuTick, _memTick, _jitTick, _tmrTick;
        private TextBlock _cpuNote, _memNote, _jitNote, _tmrNote;
        private SegBar _cpuBar, _memBar, _jitBar, _tmrBar;
        private TextBlock _statAvg, _statP99, _statMax;
        private CoreGrid _coreGrid;
        private DispatcherTimer _pulse;
        private long _jcursor;
        private Switch _swTimer;
        private Switch _swGm;
        private Border _diagBox;
        private TextBlock _diagTitle;
        private TextBlock _ledgerLine;
        private int _diagCount;
        private int _diagBeat;
        private Border _diagGap;

        private Border _profBox;
        private TextBlock _profValue;
        private Popup _profPopup;
        private int _powerBeat;

        // ---- 慢拍：把"每次都要 P/Invoke + 堆"的读数从 1Hz 里挪出去 ----------
        // 实测（Stopwatch，20 拍均值，Release）：Power.SyncInto 450~510 µs/次、
        // Hardware.CpuCurrentMhz 30~600 µs/次。它们的变化周期以分钟计，
        // 每秒读一次只是把节拍预算烧在"没变"上。
        //   电源方案 5 秒一次：用户改方案的手速也没有 5 秒一改的。
        //   频率     4 秒一次：负载变了频率才变，肉眼能接受的滞后远大于 4 秒。
        // 切回本页时 OnEnter 会立刻复核这两项，读数不会带着旧值上屏。
        private const int PowerBeatSecs = 5;
        private const int MhzBeatSecs = 4;
        private int _mhzBeat;
        private int _mhz;
        private bool _cpuThrottled;
        private int _pendShown = -1;

        // ---- 总线读数（SPINE）：权限 / 守护 / 实时 GPU ------------------------
        private Ellipse _permDot;
        private TextBlock _permText;
        private Ellipse _guardDot;
        private TextBlock _guardText;
        private Ellipse _gpuDot;
        private TextBlock _gpuText;
        private int _gpuUtilShown = int.MinValue;
        private int _gpuVramTenth = int.MinValue;
        private int _elevShown = -1;
        private TextBlock _standbySub;
        private int _standbySubTenth = int.MinValue;

        // ---- Readiness 4 枚微分解指示（定时器 / 调优 / 供电 / 守护） ----------
        private Border _rdTimerDot, _rdTuneDot, _rdPowerDot, _rdGuardDot;
        private TextBlock _rdTimerText, _rdTuneText, _rdPowerText, _rdGuardText;
        private FrameworkElement _rdTimerPill, _rdTunePill, _rdPowerPill, _rdGuardPill;
        private int _rdTimerTenth = int.MinValue;
        private int _rdTuneApplied = int.MinValue, _rdTuneTotal = int.MinValue, _rdTuneRem = int.MinValue;
        private bool _rdTunePending;
        private string _rdPowerKey = "";
        private string _rdGuardKey = "";

        // ---- 页头供电徽标（AC / 电池 / 节能模式） ----------------------------
        private Border _pwrBadge, _pwrBadgeDot;
        private TextBlock _pwrBadgeText;
        private string _pwrBadgeKey = "";
        private bool _fbPowerOk, _fbOnAc = true, _fbHasBat, _fbSaver;
        private int _fbBatPct = -1;

        // ---- 一键游戏模式突出卡 + 当前前台进程建档 --------------------------
        private Border _gmCard, _gmBadge;
        private TextBlock _gmBadgeText;
        private int _fgPidShown = int.MinValue;
        private int _fgProfVer = int.MinValue;
        private uint _fgPrioShown = uint.MaxValue;
        private int _fgAliveShown = -1;
        private int _fgTargetPid;
        private long _fgTargetBorn;
        private string _fgTargetExe = "";
        private string _fgTargetFullExe = "";
        private string _fgTargetName = "";
        private volatile bool _fgProcsRefreshing;
        private int _fgBgGen;
        private TextBlock _fgTitle, _fgMeta;
        private Border _fgBadge;
        private TextBlock _fgBadgeText;
        private Btn _fgCreateBtn;

        public override UIElement Build(Core c)
        {
            if (_pulse != null) _pulse.Stop();
            if (_profPopup != null) { _profPopup.IsOpen = false; _profPopup = null; }
            _diagCount = 0;
            _diagBeat = 1;
            _powerBeat = 0;
            _mhzBeat = 0;
            _cpuThrottled = false;
            _pendShown = -1;
            _elevShown = -1;
            _gpuUtilShown = int.MinValue;
            _gpuVramTenth = int.MinValue;
            _standbySubTenth = int.MinValue;
            _rdTimerTenth = int.MinValue;
            _rdTuneApplied = int.MinValue;
            _rdTuneTotal = int.MinValue;
            _rdTuneRem = int.MinValue;
            _rdTunePending = false;
            _rdPowerKey = "";
            _rdGuardKey = "";
            _pwrBadgeKey = "";
            _fbPowerOk = false;
            _fgPidShown = int.MinValue;
            _fgProfVer = int.MinValue;
            _fgPrioShown = uint.MaxValue;
            _fgAliveShown = -1;
            _fgTargetPid = 0;
            _fgTargetBorn = 0;
            _fgTargetExe = "";
            _fgTargetFullExe = "";
            _fgTargetName = "";
            _fgBgGen++;
            ResetHeroCaches();
            ResetHealthUi();
            _coreN = -1;

            StackPanel page = new StackPanel();
            page.Margin = new Thickness(T.PadX, 16, T.PadX, 20);

            page.Children.Add(BuildHeader(c));

            List<ActionDef> issues = c.Engine.RemediationIssues();
            _diagCount = issues.Count;
            _diagBox = BuildDiagnosis(c, issues);
            _diagBox.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Border diagGap = Gap(12);
            diagGap.Visibility = _diagBox.Visibility;
            _diagGap = diagGap;
            page.Children.Add(_diagGap);
            page.Children.Add(_diagBox);

            page.Children.Add(Gap(12));
            page.Children.Add(BuildHero(c));
            page.Children.Add(Gap(12));
            page.Children.Add(BuildCockpitLower(c));

            // 视觉树建完 → 订阅总线 → 立刻按真值画一遍。
            // 顺序不能反：Watch 只登记回调，不会回放；先 Prime 一次才不会出现
            // "刚打开时账本显示 0，1 秒后才变成 8"的假读数。
            WireBus();
            PrimeBus();
            return page;
        }

        // ---- Rev130 下半区非对称舞台：左 1.38* 实时示波+硅晶核心 / 右 1* 战术快控与前台目标 ----
        private UIElement BuildCockpitLower(Core c)
        {
            Grid split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.38, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            StackPanel leftStage = new StackPanel();
            leftStage.Children.Add(BuildWavePanel(c));
            leftStage.Children.Add(Gap(12));
            leftStage.Children.Add(BuildTopology(c));
            split.Children.Add(leftStage);

            StackPanel rightDock = new StackPanel();
            rightDock.Children.Add(BuildQuick(c));
            rightDock.Children.Add(Gap(12));
            rightDock.Children.Add(BuildForegroundPanel(c));
            Grid.SetColumn(rightDock, 2);
            split.Children.Add(rightDock);
            return split;
        }

        // ---- 诊断条：第三方优化残留 -----------------------------------------
        private Border BuildDiagnosis(Core c, List<ActionDef> issues)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid icon = Ui.IconTile(Glyph.Warn, 34, T.Warn, 0x14FFB13C, 6);
            icon.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(icon);

            StackPanel mid = new StackPanel();
            mid.VerticalAlignment = VerticalAlignment.Center;
            TextBlock t = Ui.Tx("检测到 " + issues.Count + " 项第三方「优化脚本」残留", T.FsBody, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            mid.Children.Add(t);
            _diagTitle = t;
            TextBlock s = Ui.Tx("这些非默认值来自外部一键优化：关闭 MPO、固定时间片、禁用 SysMain 等，实测多为负优化。", T.FsSmall, T.Dim, T.FCn);
            s.Margin = new Thickness(0, 4, 0, 0);
            s.TextTrimming = TextTrimming.CharacterEllipsis;
            mid.Children.Add(s);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            StackPanel diagActs = new StackPanel();
            diagActs.Orientation = Orientation.Horizontal;
            diagActs.VerticalAlignment = VerticalAlignment.Center;

            Btn fixNow = new Btn("一键修复残留", BtnKind.Primary);
            fixNow.Height = T.BtnH;
            fixNow.Compact = true;
            fixNow.Margin = new Thickness(0, 0, 8, 0);
            fixNow.Click += delegate { FixAllRemediation(c); };
            diagActs.Children.Add(fixNow);

            Btn go = new Btn("查看明细", BtnKind.Ghost);
            go.Height = T.BtnH;
            go.Compact = true;
            go.Click += delegate { c.Shell.Go("tuning"); };
            diagActs.Children.Add(go);

            Grid.SetColumn(diagActs, 2);
            g.Children.Add(diagActs);

            Border outer = new Border();
            outer.CornerRadius = new CornerRadius(T.Radius);
            outer.Background = T.S(0x14FFB13C);
            outer.BorderBrush = T.S(0x33FFB13C);
            outer.BorderThickness = new Thickness(1);
            outer.Padding = new Thickness(16, 12, 16, 12);
            outer.Child = g;
            return outer;
        }

        /// <summary>一键直接修复所有检测到的第三方负优化残留（全走写前账本）。</summary>
        private void FixAllRemediation(Core c)
        {
            List<ActionDef> issues = c.Engine.RemediationIssues();
            if (issues.Count == 0) { c.Shell.Toast("当前未检测到第三方优化残留", true); return; }
            for (int i = 0; i < issues.Count; i++)
            {
                if (!c.Engine.CanWrite(issues[i]))
                {
                    c.Shell.Toast("修复残留需要管理员权限：点左下角状态行提权重启", false);
                    return;
                }
            }
            ApplyReport rep = c.Engine.ApplyMany(issues);
            c.State.Post(Dirty.Ledger);
            RenderTuningCoverage();
            int left = c.Engine.RemediationIssues().Count;
            _diagCount = left;
            if (_diagBox != null) _diagBox.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_diagGap != null) _diagGap.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_diagTitle != null && left > 0) _diagTitle.Text = "检测到 " + left + " 项第三方「优化脚本」残留";
            c.Shell.Toast(rep.Ok ? ("已修复 " + rep.Applied + " 项第三方残留") : ("修复完成，有 " + rep.Failed + " 项失败"), rep.Ok);
        }

        /// <summary>一键推荐：优先修复第三方残留，其次补两项低风险延迟项。</summary>
        private void ApplyRecommended(Core c)
        {
            System.Collections.Generic.List<ActionDef> sel = new System.Collections.Generic.List<ActionDef>();
            foreach (ActionDef a in c.Engine.Actions)
            {
                if (!a.Remediation) continue;
                if (c.Engine.ProbeForUi(a) == AState.Applied) continue;
                sel.Add(a);
            }
            foreach (string id in new string[] { "game-mode", "game-dvr" })
            {
                ActionDef a = c.Engine.Find(id);
                if (a != null && c.Engine.ProbeForUi(a) != AState.Applied) sel.Add(a);
            }
            if (sel.Count == 0) { c.Shell.Toast("当前已是推荐状态，无需改动", true); return; }

            bool needAdmin = false;
            foreach (ActionDef a in sel) if (!c.Engine.CanWrite(a)) { needAdmin = true; break; }
            if (needAdmin)
            {
                c.Shell.Toast("推荐方案包含 " + sel.Count + " 项需要管理员权限：点左下角状态行提权重启", false);
                return;
            }
            ApplyReport rep = c.Engine.ApplyMany(sel);
            // 账本条目变了：Post 一次，让账本读数在下一帧合并刷新（不等 1Hz Tick）。
            c.State.Post(Dirty.Ledger);
            c.Shell.Toast(rep.Ok ? ("已应用推荐方案 · " + rep.Applied + " 项") : ("部分失败 " + rep.Failed + " 项"), rep.Ok);
            if (c.Get("tuning") != null) c.Shell.Go("tuning");
        }

        // ---- 生命周期 -------------------------------------------------------

        public override void OnEnter()
        {
            // 本页要画抖动波形：让探针热跑（Highest + 1ms）。
            // 不点这一行，波形与 WAKE JITTER 读数会是 0 —— 探针按需运行，
            // 而"谁在看"只有页面自己知道。调度页与基准页早已同样处理。
            if (C != null && C.Perf != null) C.Perf.JitterHot = true;

            // 切回本页时读数必须立刻是真的：电源方案走 SyncProfile（powrprof 真读），
            // CPU 频率在这里补一次。慢拍只负责"页面开着的时候"，进页这一下不能等。
            RefreshFallbackPowerIfNeeded();
            SyncProfile();
            RenderPowerBadge();
            RenderTuningCoverage();
            RenderReadinessPower();
            RenderReadinessGuard();
            SyncGameModeUi();
            SyncForeground(true);
            Hardware.CpuPowerSummary psum = Hardware.ReadCpuPowerSummary();
            _mhz = psum.CurrentMhz;
            _cpuThrottled = psum.Throttled;
            _mhzBeat = 1;
            RenderLiveGpu();
            if (C != null && C.Engine != null) C.Engine.RefreshPowerAcCacheAsync(null);
            if (C != null)
            {
                Snapshot cur = C.Perf.Current;
                if (cur != null && cur.Tick > 0)
                {
                    WriteCpuNote(cur, _mhz);
                    if (_scoreShown < 0) Tick(cur);
                }
            }
            PrimeWave();
            if (_pulse == null) _pulse = TrackTimer(200, delegate { Pump(); });
            _pulse.Start();
        }

        public override void OnLeave()
        {
            if (C != null && C.Perf != null) C.Perf.JitterHot = false;
            if (_pulse != null) _pulse.Stop();
            if (_profPopup != null) _profPopup.IsOpen = false;
        }

        public override void Tick(Snapshot s)
        {
            // 每 5 秒核对一次系统电源计划：用户可能在 Windows 设置里改过，
            // 这里必须跟着变，而不是永远显示"平衡"。
            // 为什么不再每 2 秒：Power.SyncInto 一次要 powrprof 三连 + 一次堆分配，
            // 实测单次 ~0.45 ms —— 比本页其它所有事加起来还贵；而电源方案不会在
            // 5 秒内自己变一次。切回本页时 OnEnter → SyncProfile 会立刻复核一遍。
            // 注意这里只写"唯一真源"（Power.SyncInto → Core.Profile → Dirty.Profile），
            // 标签由订阅刷新——不在 Tick 里再抄一遍同样的文案逻辑。
            if (C != null && (++_powerBeat % PowerBeatSecs) == 0)
            {
                Power.SyncInto(C);
                if (C.Daemon == null) RefreshFallbackPowerIfNeeded();
                RenderPowerBadge();
                RenderReadinessPower();
            }

            if (_gauge == null) return;
            if (_applyScore == null) _applyScore = ApplyScore;

            // 就绪度：CPU / 抖动 / 内存压力 / 定时器颗粒度 的合成启发式
            double jitterPenalty = Math.Min(26, s.JitterP99Us / 90.0);
            double cpuPenalty = Math.Max(0, s.CpuTotal - 0.45) * 34;
            double memPenalty = Math.Max(0, s.MemUsedPct - 0.55) * 52;
            double timerPenalty = s.TimerMs > 4 ? 10 : 0;
            double score = Math.Max(0, Math.Min(100, 100 - jitterPenalty - cpuPenalty - memPenalty - timerPenalty));

            // 数值跃动：弧与数字一起做 280ms 插值，而不是每秒硬跳一格。
            // 补间只写 _gauge.Value（触发一次 InvalidateVisual），不碰布局。
            double target = score / 100.0;
            PaintGaugeTier(score);          // 三档颜色只在换档时写（弧 + 数字）
            if (_scoreShown < 0) { _applyScore(target); }
            else if (Math.Abs(target - _scoreShown) > 0.004) Motion.Tween(_scoreShown, target, 280, _applyScore);

            _cpuTick.Set(s.CpuTotal * 100, 280);
            _cpuBar.Value = s.CpuTotal;
            // 频率 4 秒读一次（见字段区注释）；说明行自己带变化检测。
            if ((_mhzBeat++ % MhzBeatSecs) == 0)
            {
                Hardware.CpuPowerSummary psum = Hardware.ReadCpuPowerSummary();
                _mhz = psum.CurrentMhz;
                _cpuThrottled = psum.Throttled;
            }
            WriteCpuNote(s, _mhz);

            _memTick.Set(s.MemUsedMB / 1024.0, 280);
            _memBar.Value = s.MemUsedPct;
            WriteMemNote(s);
            RenderStandbySub(s);
            RenderLiveGpu();

            // 抖动只在探针热跑时才是真读数；冷态（窗口收起/首帧前）显示 —，不拿 0 冒充"零抖动"。
            if (s.JitterLive)
            {
                _jitTick.Set(s.JitterP99Us, 280);
                _jitBar.Value = Math.Min(1, s.JitterP99Us / 6000.0);
            }
            else
            {
                _jitTick.ShowText("\u2014");
                _jitBar.Value = 0;
            }
            WriteJitNote(s);

            // TIMER RES 的数值是连续量，仍走 Tick；开关与注释由 Dirty.Timer 订阅驱动。
            _tmrTick.Set(s.TimerMs, 280);
            if (_tmrBar != null) _tmrBar.Value = Math.Max(0.08, Math.Min(1.0, (16.0 - s.TimerMs) / 15.5));
            UpdateReadinessTimer(s.TimerMs);
            if (_swGm != null && C != null && C.Shell != null && _swGm.On != C.Shell.GameModeOn)
            {
                _swGm.On = C.Shell.GameModeOn;
                SyncGameModeUi();
            }
            SyncForeground(false);

            PushTrends(s);          // 趋势带 + 变化量：四格各喂一个 0..1 样本
            WriteStatPills(s);      // 三个标签各自比对整数，没变就不拼不写
            RenderSystemHealth(s);  // 四维健康评分与瓶颈诊断

            // 残留数量会因本工具或其它工具而变，但探测要读 8 个注册表值——
            // 1Hz 全量探测是纯浪费，改为每 10 拍（10 秒）复判一次。
            if (_diagBox != null && C != null && (_diagBeat++ % 10) == 0)
            {
                RenderTuningCoverage();
                int now = C.Engine.RemediationIssues().Count;
                if (now != _diagCount)
                {
                    _diagCount = now;
                    bool show = now > 0;
                    _diagBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (_diagGap != null) _diagGap.Visibility = _diagBox.Visibility;
                    if (_diagTitle != null) _diagTitle.Text = "检测到 " + now + " 项第三方「优化脚本」残留";
                }
            }
            // 账本条目数已改由 Dirty.Ledger / Dirty.Daemon 订阅驱动（RenderLedger），
            // 这里不再重复读一遍——同一处逻辑只留一个写入点。

            // 核心矩阵：数据没变就不重画（Load 自己会 InvalidateVisual，
            // 旧代码在它后面又白叫了一次）。
            PushCores(s.Cores);
        }
    }
}
