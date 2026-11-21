// ============================================================================
//  Profiles.cs — 08 战术档案：为每个游戏固化一套亲和性 / GPU / 定时器策略
//
//  Rev86：从"卡片墙"改成"行网格"。
//  卡片墙的问题不是不好看，是每一张卡自己算宽度、自己算内边距：
//  读者没法沿一条竖线比较"哪个游戏命中多、哪个没配策略"，
//  而这三件事（谁、配了什么、上次用是什么时候）恰恰是这一页的全部内容。
//  现在一行一个档案，四列固定：名称 + exe / 命中·最近 / 策略徽标（最多 2） / 应用。
//  三个策略开关收进行下方的抽屉：点行展开，同一次只展开一行。
//
//  行网格与 03 调度页共用同一套列宽（110 / 92 / 56，DESIGN-86 §3）。
//  档案库的落盘格式没有"命中次数 / 上次使用"两个字段，也不会为了一列去改它：
//  这一列只显示本次会话里真实发生过的应用（没有就显示 —），不拿建档时间冒充使用时间。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Profiles : Page
    {
        public override string Key { get { return "profiles"; } }
        public override string Idx { get { return "08"; } }
        public override string Title { get { return "战术档案"; } }
        public override string En { get { return "Profiles"; } }
        public override string Ico { get { return Glyph.Profiles; } }

        // 行网格：列 2 = 命中 / 最近（等宽读数），列 3 = 策略徽标，列 4 = 应用。
        // 与 03 调度页的审计行同一套数——两页的行必须能并排对齐。
        private const double ColVal = Ui.ColValue;
        private const double ColTag = Ui.ColTag;
        private const double ColAct = 56;
        private const double RowH = 50;

        // 档案库挂在 Core 上：守护引擎与页面读写的是同一份，避免双份内存态互相覆盖。
        private ProfileStore Store { get { return C.Profiles; } }

        private Reflow _list;            // 档案卡牌墙（2 列自适应网格）
        private StackPanel _emptyBox;    // 空态
        private TextBlock _listMeta;     // 面板头右侧：档案数 · 权限 · 精度
        private int _statKey = int.MinValue;

        // 本次会话内的"命中"与"最近一次应用"。
        private readonly Dictionary<string, int> _hits = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _lastApply = new Dictionary<string, DateTime>();
        private readonly List<PRow> _rows = new List<PRow>();
        private string _selectedId = "";
        private bool _pulseOn;

        /// <summary>一行档案持有的引用：刷新徽标 / 命中 / 抽屉时不必重建整行。</summary>
        private sealed class PRow
        {
            public GameProfile P;
            public StackPanel Host;
            public Border Shell;
            public System.Windows.Shapes.Rectangle Bar;
            public Border Editor;
            public StackPanel Badges;
            public TextBlock Desc;
            public TextBlock Hits;
        }

        public override UIElement Build(Core c)
        {
            _discoverScanning = false;
            _discoverGen++;
            UIElement root = Ui.PageRoot(BuildHeader(c), BuildBody(c));
            WireBus();       // 先订阅，再画第一帧：构建期的 Reload 就是首帧
            Reload(c);
            RefreshGuard();
            return root;
        }

        // ---- 总线订阅（SPINE）-----------------------------------------------
        /// <summary>
        /// 本页两条订阅：
        ///  · Daemon —— 守护条与每一行的"挂载中"读数。原来只有"页面重进"和
        ///    Shell 的回调两条路径，现在统一由总线推，离页期间也在攒最新值；
        ///  · Elevated | Timer —— 面板头上的工况读数（权限、0.5ms 租约）。
        /// 订阅在构建期登记、Invalidate 时由基类退订，所以不会因为换主题而泄漏。
        /// </summary>
        private void WireBus()
        {
            Watch(Dirty.Daemon, OnDaemonBus);
            Watch(Dirty.Elevated | Dirty.Timer, OnEnvBus);
        }

        private void OnDaemonBus(Dirty d) { RefreshGuard(); RefreshRows(); ShowLastSession(); }
        private void OnEnvBus(Dirty d) { RefreshStat(); }

        private UIElement BuildHeader(Core c)
        {
            StackPanel actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            actions.VerticalAlignment = VerticalAlignment.Bottom;

            Btn find = new Btn("自动发现", BtnKind.Quiet);
            find.SetIcon(Glyph.Search, 12);
            find.Margin = new Thickness(0, 0, 8, 0);
            find.Click += delegate { RunDiscover(c); };
            Tip.Attach(find, "扫描 Steam / Epic / WeGame / Xbox / 桌面快捷方式，列出已安装游戏一键建档。");
            actions.Children.Add(find);

            Btn add = new Btn("添加游戏", BtnKind.Primary);
            add.SetIcon(Glyph.Bolt, 13);
            add.Click += delegate { AddGame(c); };
            actions.Children.Add(add);

            return Ui.PageHeader("LOADOUT / 08", "战术档案", "把「核心拓扑 / 优先级 / 独显优先 / 0.5ms / 后台能效」固化成单个可执行文件级别的策略", actions);
        }

        private UIElement BuildBody(Core c)
        {
            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 守护条
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 自动发现
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 战报
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 档案
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) }); // 区块间距
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 模板
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 脚注

            body.Children.Add(BuildGuard());

            _discoverHost = new StackPanel();
            _discoverHost.Visibility = Visibility.Collapsed;
            _discoverHost.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(_discoverHost, 2);
            body.Children.Add(_discoverHost);

            _reportHost = new StackPanel();
            _reportHost.Visibility = Visibility.Collapsed;
            _reportHost.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(_reportHost, 3);
            body.Children.Add(_reportHost);

            UIElement list = BuildList(c);
            list.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 14, 0, 0));
            Grid.SetRow(list, 4);
            body.Children.Add(list);

            UIElement templates = BuildTemplates(c);
            Grid.SetRow(templates, 6);
            body.Children.Add(templates);

            // 档案库落点与写入范围：这是本页唯一"会改系统"的地方，说清楚在哪里、能不能退。
            TextBlock note = Ui.Tx("档案库 " + Store.FilePath
                + " · 应用档案会写入 HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences（可逆），"
                + "并对正在运行的进程设置 ProcessorAffinity；不替换游戏文件、不注入任何代码。",
                T.FsMicro, T.Mute, T.FCn);
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 17;
            note.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(note, 7);
            body.Children.Add(note);

            return body;
        }

        // ====================================================================
        //  动态守护
        // ====================================================================
        private TextBlock _guardHead;
        private TextBlock _guardBody;
        private Switch _swGuard;
        private System.Windows.Shapes.Ellipse _guardDot;

        private UIElement BuildGuard()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _guardDot = new System.Windows.Shapes.Ellipse();
            _guardDot.Width = 6;
            _guardDot.Height = 6;
            _guardDot.Fill = T.S(T.AccentDim);
            _guardDot.VerticalAlignment = VerticalAlignment.Top;
            _guardDot.Margin = new Thickness(0, 7, 11, 0);
            g.Children.Add(_guardDot);

            StackPanel mid = new StackPanel();
            Grid.SetColumn(mid, 1);
            _guardHead = Ui.Tx("动态守护 · 待命", T.FsBody, T.Hi, T.FCn);
            _guardHead.FontWeight = FontWeights.SemiBold;
            mid.Children.Add(_guardHead);
            _guardBody = Ui.Mono("为游戏建档后，启动即自动挂载 · 退出按账本复原", T.FsNano, T.Mute);
            _guardBody.Margin = new Thickness(0, 4, 0, 0);
            _guardBody.TextTrimming = TextTrimming.CharacterEllipsis;
            mid.Children.Add(_guardBody);
            g.Children.Add(mid);

            _swGuard = new Switch(C != null && C.Daemon != null && (C.Daemon.Watcher.Enabled || C.Daemon.Watcher.Active != null));
            _swGuard.VerticalAlignment = VerticalAlignment.Center;
            _swGuard.Changed += delegate
            {
                if (C == null || C.Daemon == null) { _swGuard.On = false; return; }
                C.Daemon.Watcher.Enabled = _swGuard.On;
                if (C.Daemon.Tray != null) C.Daemon.Tray.UpdateTip(_swGuard.On ? "GameOptimizer · 守护中" : "GameOptimizer · 守护已关闭");
                RefreshGuard();
            };
            Grid.SetColumn(_swGuard, 2);
            g.Children.Add(_swGuard);

            Border box = Ui.HeroPanel(g, 14);
            Tip.Attach(box, "只对建立了战术档案的 exe 生效：不做启发式猜测，不误伤 IDE 与启动器。");
            return box;
        }

        /// <summary>由 Shell 在守护状态变化时回调，保证页面与托盘显示同一份事实。</summary>
        public void RefreshGuard()
        {
            if (_guardHead == null) return;
            Go.Daemon.DaemonHost d = C == null ? null : C.Daemon;
            if (d == null)
            {
                _guardHead.Text = "动态守护 · 不可用";
                _guardBody.Text = "离屏渲染模式不启动守护引擎";
                _guardDot.Fill = T.S(T.Mute);
                if (_swGuard != null) { _swGuard.On = false; _swGuard.SetReadOnly(true); }
                return;
            }
            GameProfile a = d.Watcher.Active;
            bool on = d.Watcher.Enabled;
            if (_swGuard != null && _swGuard.On != (on || a != null)) _swGuard.On = on || a != null;

            if (a != null)
            {
                string pol = BuildGuardPolicyText(a);
                _guardHead.Text = "动态守护 · 运行中 " + a.Name;
                _guardHead.Foreground = T.S(T.Hi);
                _guardBody.Text = pol + "  ·  PID " + d.Watcher.ActivePid
                    + (string.IsNullOrEmpty(d.Pilot.LastReport) ? "" : "  ·  " + d.Pilot.LastReport);
                _guardDot.Fill = T.S(T.Ok);
                SetGuardPulse(true);
                // 反作弊把进程保护起来时，亲和度是注定失败的——这不是故障，但要让人看见
                if (d.Pilot.LastHadDenied)
                {
                    _guardDot.Fill = T.S(T.Warn);
                    _guardBody.Foreground = T.S(T.Warn);
                }
                else _guardBody.Foreground = T.S(T.Mute);
            }
            else
            {
                _guardHead.Text = on ? "动态守护 · 待命" : "动态守护 · 已关闭";
                _guardHead.Foreground = T.S(on ? T.Hi : T.Dim);
                _guardBody.Text = on
                    ? (d.LastNotice != null ? d.LastNotice : "为游戏建档后，启动即自动挂载 · 退出按账本复原")
                    : "已停止巡检：不会自动应用任何策略";
                SetGuardPulse(false);
                _guardDot.Fill = T.S(on ? T.AccentDim : T.Mute);
            }
        }

        /// <summary>
        /// 呼吸灯只在页面真的挂在窗口上时开。
        /// 总线订阅在离页后仍然推送（这是刻意的：回到页面时应该已经是最新值），
        /// 所以"要不要动"必须每次现判，不能只在 OnEnter 里判一次。
        /// </summary>
        private void SetGuardPulse(bool want)
        {
            bool on = want && _guardDot != null && System.Windows.Window.GetWindow(_guardDot) != null;
            if (_pulseOn == on) return;
            _pulseOn = on;
            if (on) Motion.Breathe(_guardDot, 0.45, 1.0, 1800);
            else Motion.StopBreathe(_guardDot);
        }

        // ====================================================================
        //  面板头读数：档案数 · 权限 · 精度（单一写入点）
        // ====================================================================
        private void RefreshStat()
        {
            if (_listMeta == null || C == null) return;
            int count = Store.Count;
            bool el = C.Elevated;
            bool tuned = C.Timer != null && C.Timer.Active;

            int key = count * 4 + (el ? 2 : 0) + (tuned ? 1 : 0);
            if (key == _statKey) return;
            _statKey = key;

            _listMeta.Text = count + " 个档案 · " + (el ? "ADMIN" : "STANDARD") + " · 精度 " + (tuned ? "0.5 ms" : "系统默认");
            _listMeta.Foreground = T.S(el ? T.Dim : T.Warn);
        }

        public override void OnEnter() { Reload(C); RefreshGuard(); ShowLastSession(); }

        public override void OnLeave()
        {
            // 守护状态点的呼吸动画在页面隐藏后必须停，否则动画时钟持有整棵已离屏的视觉树。
            if (_guardDot != null) Motion.StopBreathe(_guardDot);
            _pulseOn = false;
            _discoverGen++;
            if (_discoverScanning)
            {
                _discoverScanning = false;
                if (_discoverHost != null)
                {
                    _discoverHost.Children.Clear();
                    _discoverHost.Visibility = Visibility.Collapsed;
                }
            }
        }

        // ====================================================================
        //  写档案（页面级动作；发现页复用下面这几个入口）
        // ====================================================================
        private static string EffectiveCcdMode(GameProfile p)
        {
            if (p == null) return "Auto";
            string m = p.CcdMode;
            if (!string.IsNullOrEmpty(m))
            {
                if (string.Equals(m, "Ccd0", StringComparison.OrdinalIgnoreCase)) return "Ccd0";
                if (string.Equals(m, "Ccd1", StringComparison.OrdinalIgnoreCase)) return "Ccd1";
                if (string.Equals(m, "Physical", StringComparison.OrdinalIgnoreCase)) return "Physical";
                if (string.Equals(m, "All", StringComparison.OrdinalIgnoreCase)) return "All";
                if (string.Equals(m, "Auto", StringComparison.OrdinalIgnoreCase)) return "Auto";
            }
            return p.PinCcd0 ? "Ccd0" : "Auto";
        }

        private static string CcdLabel(string mode)
        {
            if (mode == "Ccd0") return "CCD0";
            if (mode == "Ccd1") return "CCD1";
            if (mode == "Physical") return "物理核";
            if (mode == "All") return "全核";
            return "自动";
        }

        private static string EffectivePriorityMode(GameProfile p)
        {
            if (p == null || string.IsNullOrEmpty(p.PriorityMode)) return "Keep";
            if (string.Equals(p.PriorityMode, "High", StringComparison.OrdinalIgnoreCase)) return "High";
            if (string.Equals(p.PriorityMode, "AboveNormal", StringComparison.OrdinalIgnoreCase)) return "AboveNormal";
            return "Keep";
        }

        private static string PriorityTag(string mode)
        {
            if (mode == "High") return "高优";
            if (mode == "AboveNormal") return "高于";
            return "保持";
        }

        private static string BuildGuardPolicyText(GameProfile p)
        {
            List<string> parts = new List<string>(5);
            string ccd = EffectiveCcdMode(p);
            if (ccd != "Auto") parts.Add(CcdLabel(ccd) + " 亲和");
            string prio = EffectivePriorityMode(p);
            if (prio != "Keep") parts.Add(prio == "High" ? "高优先级" : "高于标准");
            if (p.HighGpu) parts.Add("独显优先");
            if (p.Timer) parts.Add("0.5ms 精度");
            if (p.EcoBackground) parts.Add("后台降效");
            return parts.Count == 0 ? "未配置策略" : string.Join("  ", parts.ToArray());
        }

        private static string FormatDurationShort(long sec)
        {
            if (sec <= 0) return "";
            if (sec < 60) return sec + "s";
            if (sec < 3600) return (sec / 60) + "m";
            return (sec / 3600.0).ToString("0.0") + "h";
        }

        private void AddGameWith(Core c, bool pin, bool gpu, bool timer)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选择游戏可执行文件";
            dlg.Filter = "可执行文件 (*.exe)|*.exe";
            if (dlg.ShowDialog() != true) return;

            GameProfile p = new GameProfile();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
            p.Exe = dlg.FileName;
            p.PinCcd0 = pin;
            p.CcdMode = pin ? "Ccd0" : "Auto";
            p.PriorityMode = "Keep";
            p.HighGpu = gpu;
            p.Timer = timer;
            p.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Store.Add(p);
            if (c.Shell != null) c.Shell.Toast("已按模板建立档案 " + p.Name, true);
            Reload(c);
        }

        private void AddGame(Core c)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选择游戏可执行文件";
            dlg.Filter = "可执行文件 (*.exe)|*.exe";
            if (dlg.ShowDialog() != true) return;

            GameProfile p = new GameProfile();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
            p.Exe = dlg.FileName;
            p.PinCcd0 = true;
            p.CcdMode = "Ccd0";
            p.PriorityMode = "Keep";
            p.HighGpu = true;
            p.Timer = false;
            p.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Store.Add(p);
            if (c.Shell != null) c.Shell.Toast("已建立档案 " + p.Name, true);
            Reload(c);
        }

        private static long ResolveAffinityMask(Topology topo, string ccdMode, out string note)
        {
            note = "";
            if (ccdMode == "Auto") return 0;
            if (ccdMode == "Physical") return Affinity.PhysicalOnlyMask(topo);
            if (ccdMode == "All") return Affinity.AllMask();
            if (ccdMode == "Ccd1")
            {
                if (topo == null || topo.Ccds <= 1) { note = "本机仅单 CCD，已跳过 CCD1 绑定"; return 0; }
                return Affinity.CcdMask(topo, 1);
            }
            return Affinity.CcdMask(topo, 0);
        }

        private void ApplyProfile(Core c, GameProfile p)
        {
            int okCount = 0;
            string fail = "";

            if (p.HighGpu)
            {
                // 走写前账本（与守护 AutoPilot.AttachGpu 同一通道），Restore 页可逆。
                try
                {
                    Op op = new Op();
                    op.Kind = "Reg";
                    op.Hive = "HKCU";
                    op.Path = @"Software\Microsoft\DirectX\UserGpuPreferences";
                    op.Name = p.Exe;
                    op.Type = "String";
                    op.Value = "GpuPreference=2;";
                    OpResult res = Ops.Apply(op, "profile-gpu-" + p.Id, "档案·独显优先 " + p.Name, c.Engine.Ledger);
                    if (res.Ok) { okCount++; c.State.Post(Dirty.Ledger); }
                    else fail = res.Message;
                }
                catch (Exception ex) { Diag.Fail("profiles.gpu", ex); fail = ex.Message; }
            }

            string ccdMode = EffectiveCcdMode(p);
            string affNote;
            long mask = ResolveAffinityMask(c.Topo, ccdMode, out affNote);
            if (affNote.Length > 0 && fail.Length == 0) fail = affNote;

            string prioMode = EffectivePriorityMode(p);
            uint prioClass = ProcessPower.ParsePriorityMode(prioMode);
            string procName = string.IsNullOrEmpty(p.Exe) ? "" : Path.GetFileNameWithoutExtension(p.Exe);
            int procCount = 0;
            int gamePid = 0;

            if (procName.Length > 0)
            {
                try
                {
                    int[] pids = Go.Daemon.ProcessScan.FindPidsByName(procName);
                    procCount = pids.Length;
                    for (int pi = 0; pi < pids.Length; pi++)
                    {
                        int targetPid = pids[pi];
                        try
                        {
                            if (gamePid == 0) gamePid = targetPid;
                            if (mask > 0)
                            {
                                string aerr;
                                if (Affinity.Apply(targetPid, mask, out aerr)) okCount++;
                                else if (fail.Length == 0) fail = aerr ?? "亲和性应用失败";
                            }
                            if (prioClass != 0)
                            {
                                string perr;
                                if (ProcessPower.SetPriority(targetPid, prioClass, true, out perr)) okCount++;
                                else if (fail.Length == 0) fail = perr ?? "优先级设置失败";
                            }
                        }
                        catch (Exception ex) { Diag.Fail("profiles.proc", ex); if (fail.Length == 0) fail = ex.Message; }
                    }
                    if (procCount > 0 && p.Timer)
                    {
                        c.Timer.Request(TimerOwner.Profile, 0.5);
                        okCount++;
                    }
                }
                catch (Exception ex) { Diag.Fail("profiles.enum", ex); fail = ex.Message; }
            }

            int ecoSup = 0;
            bool ecoHandled = false;
            if (procCount > 0 && p.EcoBackground)
            {
                Go.Daemon.DaemonHost d = c.Daemon;
                if (d != null && d.Pilot != null)
                {
                    if (d.Recorder != null && d.Recorder.Active)
                    {
                        ecoSup = d.Pilot.OnPulse(p, gamePid, d.Recorder);
                        ecoHandled = true;
                        okCount++;
                    }
                    else if (d.Watcher != null && d.Watcher.Enabled)
                    {
                        d.Watcher.Reload();
                        ecoHandled = true;
                        okCount++;
                    }
                    else if (fail.Length == 0)
                    {
                        fail = "动态守护未开启，后台降效未生效";
                    }
                }
                else if (fail.Length == 0)
                {
                    fail = "守护引擎不可用，后台降效未生效";
                }
            }

            if (c.Shell != null)
            {
                if (procCount == 0 && (mask > 0 || prioClass != 0 || p.Timer || p.EcoBackground))
                {
                    if (fail.Length > 0)
                        c.Shell.Toast("部分完成：" + fail, false);
                    else if (p.HighGpu && okCount > 0)
                        c.Shell.Toast("已写入独显优先；" + procName + " 未在运行，其余策略守护挂载时自动生效", true);
                    else
                        c.Shell.Toast("档案已保存；" + procName + " 未在运行，守护挂载时自动生效", true);
                }
                else
                {
                    string okMsg = "已对 " + (procName.Length > 0 ? procName : p.Name) + " 应用 " + okCount + " 项策略";
                    if (ecoSup > 0) okMsg += "（后台降效 " + ecoSup + " 个进程）";
                    else if (ecoHandled) okMsg += "（后台降效已就绪）";
                    c.Shell.Toast(fail.Length > 0 ? "部分完成：" + fail : okMsg, fail.Length == 0);
                }
            }
            c.Engine.Ledger.AppendAudit("PROFILE apply " + p.Name + " ccd=" + ccdMode + " prio=" + prioMode
                + (p.EcoBackground ? (" eco=" + (ecoHandled ? ecoSup.ToString() : "off")) : "")
                + " ok=" + okCount + " fail=" + fail);
        }
    }
}