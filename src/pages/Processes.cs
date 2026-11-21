// ============================================================================
//  Processes.cs — 07 进程与优先级：页面骨架 / 前台聚焦 / 采样与排序（进程页主片）
//
//  Rev86 改的是"怎么读"，不是"读什么"：
//   1) 列网格固定为 进程名 / CPU% / 内存 / 优先级徽标 / 操作（表在 Processes.Table.cs）；
//   2) 排序变成"点列标题 + 方向箭头"，取代页头三个排序按钮：顺序是表格的属性；
//   3) PID / 线程数折进名称下的说明行（第三枚徽标本来就该进说明行，契约 §5.2）；
//   4) 空态分三种：还没采到 / 过滤无匹配（给"清空过滤"）/ 真枚举不到。
//
//  本片只放"页是谁、怎么拼、数据从哪来"：拼装、前台读数、2 s 采样、排序比较器。
//  行的骨架与渲染在 Processes.Table.cs —— 一个文件一件事，超 800 行就拆（HANDOFF §2.7）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Processes : Page
    {
        public override string Key { get { return "processes"; } }
        public override string Idx { get { return "07"; } }
        public override string Title { get { return "进程与优先级"; } }
        public override string En { get { return "Processes"; } }
        public override string Ico { get { return Glyph.Processes; } }

        // ---- 列网格：唯一一份列宽（表头 / 行 / 刻度尺共用）------------------
        private const int MaxRows = 16;
        private const double RowH = 50;        // 契约 §1.2：列表行高 50
        private const double ColCpu = 112;     // 值/读数（与网络页的速率列同宽）
        private const double ColMem = 88;
        private const double ColPri = 96;      // 徽标（与网络页状态列同宽）
        private const double ColOp = 98;       // 操作（CCD0 + 调优）

        private const int SortName = 0, SortCpu = 1, SortMem = 2, SortPri = 3;

        private sealed class PStat
        {
            public int Pid;
            public string Name;
            public string Image;
            public double CpuPct;
            public bool Measured;     // 差分窗口成立才有读数；首帧 false，不能画成 0
            public double WsMb;       // <0 = 读不到
            public int Threads = -1;  // <0 = 读不到（线程数不可能是 0）
            public uint PriClass;
            public int PriRank;
            public string PriLabel;
            public string Prio;       // null/空 = 读不到（通常是没有管理员权限）
            public long Mask;         // 0 = 读不到（Affinity.Read 的约定，界面必须显示 —）
            public bool EcoQoS;
            public bool EcoQoSKnown;
            public bool DetailsSynced;
            public ProcEntry Entry;
        }

        /// <summary>一行固化骨架：元素只建一次，之后只改 Text / Value / Visibility。</summary>
        private sealed class Row
        {
            public Border Root;
            public StackPanel Host;
            public Grid Summary;
            public Border DetailBox;
            public DetailRefs Detail;
            public TextBlock Name, Sub, Cpu, Mem;
            public CpuBar Bar;
            public Badge Pri, EcoTag, AffTag;
            public Btn Pin, ExpandBtn;
            public int CurPid;
            public string CurName, CurImage, CurPriLabel;
            public bool Pinned, PinAll, CurEco, CurEcoKnown;
            public long CurMask;
            public uint CurPriClass;
            public int CpuShown = int.MinValue, CpuTier = -1, MemShown = int.MinValue;
            public int SubPid = int.MinValue, SubThreads = int.MinValue;
        }

        /// <summary>可重绘徽标：Ui.Tag 是一次性的，这里要按 2 s 拍改字与色（只碰两个 DP）。</summary>
        private sealed class Badge
        {
            public Border Box;
            public TextBlock Text;
            public uint Tint;
            public string Shown;

            public void Paint(string text, uint tint)
            {
                if (text != Shown) { Shown = text; Text.Text = text; }
                if (tint == Tint) return;
                Tint = tint;
                Text.Foreground = T.S(tint);
                Box.BorderBrush = T.S(Ui.Wash(tint, 0x38));
            }
        }

        private static Badge MakeBadge(string text, uint tint)
        {
            Badge b = new Badge();
            b.Box = new Border();
            b.Box.CornerRadius = new CornerRadius(3);
            b.Box.BorderThickness = new Thickness(1);
            b.Box.Padding = new Thickness(5, 0, 5, 0);
            b.Box.VerticalAlignment = VerticalAlignment.Center;
            b.Box.HorizontalAlignment = HorizontalAlignment.Left;
            b.Box.BorderBrush = T.S(Ui.Wash(tint, 0x38));
            b.Text = Ui.Mono(text, T.FsNano, tint);
            b.Text.VerticalAlignment = VerticalAlignment.Center;
            b.Box.Child = b.Text;
            b.Tint = tint;
            b.Shown = text;
            return b;
        }

        private readonly Row[] _skel = new Row[MaxRows];
        private StackPanel _rows;
        private Border _emptyHost, _headBand;
        private TextBlock _count, _cpuHead, _fgName, _fgPid, _fgAff, _fgPri;
        private TextBlock _sumTotalVal, _sumHotVal;
        private TextBlock[] _sortArrows;
        private Border _fgChip;
        private TranslateTransform _headShift;
        private ScrollViewer _sv;
        private FrameworkElement _pageRoot;
        private DispatcherTimer _beat;
        private readonly Dictionary<int, TimeSpan> _prevCpu = new Dictionary<int, TimeSpan>();
        private DateTime _prevAt = DateTime.MinValue;
        private int _prevSnapTick;
        private int _sort = SortCpu;
        private bool _sortDesc = true;
        private Comparison<PStat> _cmp;
        private List<PStat> _last = new List<PStat>();
        private int _total, _matched;
        private string _countShown = "", _emptyShown = "", _filterKey = "";
        private bool _sampled;
        private TextBox _filter;
        private TextBlock _ph;
        private Ui.SegTabs _viewTabs;
        private string _viewMode = "all";
        private int _expandedPid;
        private readonly HashSet<int> _touchedPids = new HashSet<int>();
        private readonly object _touchLock = new object();
        private bool _sampling, _pendingSample;

        // CPU 条量程档位。固定档位读者才能记住"这一档意味着什么"。
        private static readonly double[] Ladder = new double[]
        { 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 75, 100 };
        private double _scaleCpu = 2;

        public override UIElement Build(Core c)
        {
            // 视觉树重建后，这些缓存仍然指着上一棵树：
            //   · _countShown/_emptyShown 与旧的空 TextBlock 比较会直接命中而跳过赋值；
            //   · _sv 指着已被丢弃的 ScrollViewer，HookScroll 见非空直接 return。
            // 每个 Build 开头把本页持有的引用/缓存归零（HANDOFF §4）。
            _countShown = "";
            _emptyShown = "";
            _filterKey = "";
            _sv = null;
            _sampled = false;
            _viewMode = "all";
            _expandedPid = 0;
            _cmp = Compare;
            _sortArrows = new TextBlock[4];

            StackPanel page = new StackPanel();

            // Rev130：顶部非对称进程雷达舞台（左 1.72* 前台目标锁定卡 / 右 1* 活跃进程统计双卡）
            Grid topStage = new Grid();
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.72, GridUnitType.Star) });
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            topStage.Children.Add(BuildFocus(c));
            UIElement sumCards = BuildSummaryDeck(c);
            Grid.SetColumn(sumCards, 2);
            topStage.Children.Add(sumCards);
            page.Children.Add(topStage);

            page.Children.Add(Gap(14));
            page.Children.Add(BuildTable(c));

            Grid root = Ui.PageRoot(BuildHeader(c), page);
            _pageRoot = root;
            // 吸顶需要外层滚动容器：页面本身不滚，滚的是 Shell 的 ThinScroll。
            root.Loaded += delegate { HookScroll(); };
            SyncSort();      // 首帧就要画出当前排序方向，不能等第一次点击
            EmptyState();
            return root;
        }

        // ---- 页头 ------------------------------------------------------------
        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            _count = Ui.Mono("", T.FsNano, T.Mute);
            _count.VerticalAlignment = VerticalAlignment.Center;
            _count.Margin = new Thickness(0, 0, 14, 0);
            right.Children.Add(_count);
            right.Children.Add(BuildFilter());

            return Ui.PageHeader("MODULE / 07", "进程与优先级",
                "2 s 差分 · 按整机 " + Environment.ProcessorCount + " 核归一 · 可锁 CCD0 · 点列标题排序", right);
        }

        private UIElement BuildFilter()
        {
            Border wrap = new Border();
            wrap.Width = 176;
            wrap.Height = T.BtnH;
            wrap.CornerRadius = new CornerRadius(14);
            wrap.Background = T.S(T.Inset);
            wrap.BorderBrush = T.S(T.LineMid);
            wrap.BorderThickness = new Thickness(1);
            wrap.Padding = new Thickness(10, 0, 10, 0);
            wrap.VerticalAlignment = VerticalAlignment.Center;
            wrap.Margin = new Thickness(0, 0, 4, 0);

            Grid host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            host.ColumnDefinitions.Add(new ColumnDefinition());
            System.Windows.Shapes.Path mag = Ui.Icon(Glyph.Search, 11, T.Dim, 1.35);
            mag.Margin = new Thickness(0, 0, 6, 0);
            mag.IsHitTestVisible = false;
            host.Children.Add(mag);

            Grid ih = new Grid();
            Grid.SetColumn(ih, 1);
            _filter = new TextBox();
            _filter.FontSize = T.FsSmall; _filter.FontFamily = T.FMono;
            _filter.Foreground = T.S(T.Hi);
            _filter.Background = Brushes.Transparent;
            _filter.BorderThickness = new Thickness(0);
            _filter.VerticalContentAlignment = VerticalAlignment.Center;
            _filter.CaretBrush = T.S(T.Accent);
            ih.Children.Add(_filter);

            _ph = Ui.Tx("过滤进程名", T.FsSmall, T.Mute, T.FCn);
            _ph.IsHitTestVisible = false;
            ih.Children.Add(_ph);
            host.Children.Add(ih);
            wrap.Child = host;
            _filter.TextChanged += delegate
            {
                _filterKey = _filter.Text == null ? "" : _filter.Text.Trim();
                _ph.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Sample();
            };
            _filter.GotKeyboardFocus += delegate { wrap.BorderBrush = T.S(T.AccentA(0x88)); mag.Stroke = T.S(T.Accent); };
            _filter.LostKeyboardFocus += delegate { wrap.BorderBrush = T.S(T.LineMid); mag.Stroke = T.S(T.Dim); };
            Tip.Attach(wrap, "在全部进程里按进程名过滤（不区分大小写）；结果仍按当前排序列取前 " + MaxRows + " 条。");
            return wrap;
        }

        // ---- 前台进程聚焦 ----------------------------------------------------
        private UIElement BuildFocus(Core c)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Ui.IconTile(Glyph.Processes, 44, T.Accent, T.AccentWash, T.RadiusMd));

            StackPanel mid = new StackPanel();
            mid.Margin = new Thickness(14, 0, 14, 0);
            mid.VerticalAlignment = VerticalAlignment.Center;
            TextBlock kick = Ui.Label("前台窗口 / FOREGROUND", T.AccentDim);
            kick.Margin = new Thickness(0, 0, 0, 4);
            mid.Children.Add(kick);
            _fgName = Ui.Tx("切换到游戏窗口后自动识别", T.FsTitle, T.Dim, T.FCn);
            _fgName.FontWeight = FontWeights.SemiBold;
            _fgName.TextTrimming = TextTrimming.CharacterEllipsis;
            mid.Children.Add(_fgName);
            StackPanel meta = new StackPanel();
            meta.Orientation = Orientation.Horizontal;
            meta.Margin = new Thickness(0, 5, 0, 0);
            _fgPid = Ui.Mono("PID —", T.FsNano, T.Dim);
            _fgAff = Ui.Mono("   ·   亲和性 —", T.FsNano, T.Dim);
            _fgPri = Ui.Mono("   ·   优先级 —", T.FsNano, T.Dim);
            meta.Children.Add(_fgPid);
            meta.Children.Add(_fgAff);
            meta.Children.Add(_fgPri);
            _fgChip = Chip.Make("需管理员", T.Warn, T.ChipHSm);
            _fgChip.Margin = new Thickness(10, 0, 0, 0);
            _fgChip.Visibility = Visibility.Collapsed;
            Tip.Attach(_fgChip, "亲和性与优先级类需要以管理员身份运行本工具才能读取；系统进程与反作弊进程即使提权也会拒绝访问。");
            meta.Children.Add(_fgChip);
            mid.Children.Add(meta);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);
            Tip.Attach(mid, "锁 CCD0：把该进程的处理器亲和性限制到第一个 CCD 的核心上。全核：恢复全部逻辑核。写入失败会在下方弹出原因。");

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.VerticalAlignment = VerticalAlignment.Center;
            Btn pin = new Btn("锁 CCD0", BtnKind.Accent);
            pin.Height = T.BtnH; pin.Margin = new Thickness(0, 0, 8, 0);
            pin.Click += delegate { PinForeground(c, true); };
            Tip.Attach(pin, "把前台进程限制在 CCD0（Affinity.CcdMask(Topo, 0)）");
            btns.Children.Add(pin);
            Btn free = new Btn("全核", BtnKind.Ghost);
            free.Height = T.BtnH;
            free.Click += delegate { PinForeground(c, false); };
            Tip.Attach(free, "恢复前台进程的全部逻辑核");
            btns.Children.Add(free);
            Grid.SetColumn(btns, 2);
            g.Children.Add(btns);
            Grid hero = new Grid();
            hero.Children.Add(Ui.HeroPanel(g, T.PadPanel));
            hero.Children.Add(Ui.CornerTicks(T.AccentA(0x80), 10, 1.5));
            return hero;
        }

        private UIElement BuildSummaryDeck(Core c)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            _sumTotalVal = Ui.Num("\u2014", 26, T.Hi);
            StackPanel s1 = new StackPanel();
            s1.Children.Add(Ui.Label("ACTIVE PROCESSES", T.AccentDim));
            _sumTotalVal.Margin = new Thickness(0, 4, 0, 2);
            s1.Children.Add(_sumTotalVal);
            s1.Children.Add(Ui.Mono(c.Topo.Logical + " 逻辑核 · " + c.Topo.CcdLabel, T.FsNano, T.Mute));
            g.Children.Add(Ui.GlassTile(s1, 12, T.Accent));

            _sumHotVal = Ui.Num("\u2014", 26, T.Ok);
            StackPanel s2 = new StackPanel();
            s2.Children.Add(Ui.Label("MATCHED / TOP", T.AccentDim));
            _sumHotVal.Margin = new Thickness(0, 4, 0, 2);
            s2.Children.Add(_sumHotVal);
            s2.Children.Add(Ui.Mono("前 " + MaxRows + " 行 · 2s 差分", T.FsNano, T.Mute));
            Border c2 = Ui.GlassTile(s2, 12, T.Accent2);
            Grid.SetColumn(c2, 2);
            g.Children.Add(c2);
            return g;
        }

        // ---- 生命周期 --------------------------------------------------------
        public override void OnEnter()
        {
            Sample();
            if (_beat == null)
            {
                _beat = new DispatcherTimer();
                _beat.Interval = TimeSpan.FromSeconds(2);
                _beat.Tick += delegate { Sample(); };
            }
            _beat.Start();
        }

        public override void OnLeave() { if (_beat != null) _beat.Stop(); }
        public override void Tick(Snapshot s) { }

        private void PinRow(Row r)
        {
            if (r.CurPid == 0) return;
            Pin(r.CurPid, !r.Pinned);
        }

        private void PinForeground(Core c, bool pin)
        {
            Pin(c.LastForeignPid, pin);
        }

        /// <summary>
        /// 唯一的亲和性写入点。锁 CCD0 = Affinity.CcdMask(Topo, 0)，全核 = Affinity.Restore；
        /// 掩码拼装在 Affinity 里（本页此前自己循环 1L &lt;&lt; k，64 核以上会静默算错）。
        /// 失败必须说出来：Affinity 已把 Win32 码翻成中文原因，这里只负责不吞掉它。
        /// 成功与失败都重新采样——按钮文字与读数来自真实的 Read，不是这次点击。
        /// </summary>
        private void Pin(int pid, bool pin)
        {
            if (pid == 0) { C.Shell.Toast("请先切换到游戏窗口，再回到本工具点击", false); return; }
            string err = null;
            long target = pin ? Affinity.CcdMask(C.Topo, 0) : Affinity.AllMask();
            bool ok = C.Procs.SetRowAffinity(pid, target, out err);

            string name = ProcName.OrPid(pid);
            if (ok)
            {
                _touchedPids.Add(pid);
                C.Engine.Ledger.AppendAudit("PROC affinity " + name + " (" + pid + ") -> " + (pin ? "CCD0" : "ALL"));
                C.Shell.Toast((pin ? "已锁 CCD0：" : "已恢复全核：") + name, true);
            }
            else
            {
                string why = err == null ? "未知原因" : err;
                C.Shell.Toast("失败：" + why + " · " + name, false);
                Diag.Fail("processes.affinity", new InvalidOperationException(why));
            }
            Sample();
        }

        // ---- 采样：后台枚举 → 排序 → 只补齐可见行的重字段 ---------------------
        /// <summary>后台线程枚举进程，回到 UI 线程再排序绘制，避免首帧冻结。</summary>
        private void Sample()
        {
            if (_sampling) { _pendingSample = true; return; }
            _sampling = true;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                List<PStat> list = null;
                int total = 0, matched = 0;
                try { list = Collect(out total, out matched); }
                catch (Exception ex) { Diag.Fail("proc.collect", ex); list = new List<PStat>(); }
                ui.BeginInvoke(new Action(delegate ()
                {
                    _sampling = false;
                    if (list != null)
                    {
                        _last = list;
                        _total = total;
                        _matched = matched;
                        _sampled = true;
                        Render();
                        UpdateFocus();
                    }
                    if (_pendingSample) { _pendingSample = false; Sample(); }
                }));
            });
            t.IsBackground = true;
            t.Priority = System.Threading.ThreadPriority.BelowNormal;
            t.Name = "go.procs";
            t.Start();
        }

        /// <summary>一拍：读一帧 → 排序过滤 → 截断 → 补齐可见行。每一步都在自己的小函数里。</summary>
        private List<PStat> Collect(out int total, out int matched)
        {
            DateTime now = DateTime.Now;
            ProcSnapshot snap = C.Procs.Get(1500);

            // CPU 差分的窗口必须是**两次真实采样之间**的间隔，不是"页面两次刷新的墙钟差"。
            // 共享快照可能被别的读者提前刷新：用桥钟差会把 0.5s 的采样间隔算成 2s，
            // 于是占用率被高估 4 倍——一个看起来完全合理的假数字。
            double window = 0;
            if (snap.Tick != 0 && _prevSnapTick != 0)
            {
                int dt = unchecked(snap.Tick - _prevSnapTick);
                if (dt > 50 && dt < 60000) window = dt / 1000.0;
            }
            if (window <= 0 && _prevAt != DateTime.MinValue) window = (now - _prevAt).TotalSeconds;
            if (window < 0) window = 0;

            Dictionary<int, TimeSpan> next = new Dictionary<int, TimeSpan>(snap.Count + 8);
            List<PStat> list = new List<PStat>(snap.Count);

            if (!snap.Ok)
            {
                // 扫描失败 ≠ 没有进程：空表 + 空态说明是可接受的降级，拿上一代冒充实时不行。
                Diag.Hit("proc.table.fail");
            }
            else
            {
                // 默认模式下使用 EnrichRange 对前段做低开销增量填充；按优先级排序或「已锁核或节能」视图下全量同步以避免偏置与闪烁。
                int batch = (_viewMode == "tuned" || _sort == SortPri) ? snap.Count : Math.Min(snap.Count, MaxRows);
                if (batch > 0) C.Procs.EnrichRange(snap, 0, batch);

                int ncpu = Environment.ProcessorCount;
                for (int i = 0; i < snap.Count; i++)
                {
                    ProcEntry e = snap.Items[i];
                    if (e == null) continue;
                    bool touched;
                    lock (_touchLock) { touched = _touchedPids.Contains(e.Pid); }
                    if (touched) C.Procs.EnrichRow(e);

                    PStat st = new PStat();
                    st.Pid = e.Pid;
                    st.Name = e.Name;
                    st.Image = e.Image;
                    st.WsMb = e.WorkingSetBytes > 0 ? e.WorkingSetBytes / 1048576.0 : -1;
                    st.Threads = e.Threads;
                    st.PriClass = e.PriorityClass;
                    st.PriRank = e.PriorityRank;
                    st.PriLabel = e.PriorityLabel;
                    st.Mask = e.AffinityMask;
                    st.EcoQoS = e.EcoQoS;
                    st.EcoQoSKnown = e.EcoQoSKnown;
                    st.DetailsSynced = e.DetailsSynced;
                    st.Entry = e;

                    // 100ns 刻度与 TimeSpan.Tick 同一个单位，不需要换算。
                    TimeSpan cpu = TimeSpan.FromTicks(e.CpuTicks);
                    next[e.Pid] = cpu;
                    TimeSpan before;
                    if (window > 0.2 && _prevCpu.TryGetValue(e.Pid, out before))
                    {
                        double pct = (cpu - before).TotalSeconds / (window * ncpu) * 100.0;
                        st.CpuPct = Math.Max(0, Math.Min(100, pct));
                        st.Measured = true;
                    }
                    list.Add(st);
                }
            }
            // 本拍之后才替换基线：扫描期间 ReadOne 读的必须是上一拍的副本。
            _prevCpu.Clear();
            foreach (KeyValuePair<int, TimeSpan> kv in next) _prevCpu[kv.Key] = kv.Value;
            _prevAt = now;
            if (snap.Ok) _prevSnapTick = snap.Tick;

            total = list.Count;
            list.Sort(_cmp);
            List<PStat> hit = Filter(list);
            matched = hit.Count;
            if (hit.Count > MaxRows) hit.RemoveRange(MaxRows, hit.Count - MaxRows);
            Enrich(hit);
            return hit;
        }

        /// <summary>过滤在截断之前：名称搜索 + 视图分栏均在全局有序集合上执行。</summary>
        private List<PStat> Filter(List<PStat> all)
        {
            string key = _filterKey;
            bool highOnly = _viewMode == "high";
            bool tunedOnly = _viewMode == "tuned";
            if (key.Length == 0 && !highOnly && !tunedOnly) return all;

            long allMask = Affinity.AllMask();
            List<PStat> hit = new List<PStat>();
            for (int i = 0; i < all.Count; i++)
            {
                PStat st = all[i];
                if (key.Length > 0)
                {
                    string n = st.Name;
                    if (n == null || n.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                if (highOnly)
                {
                    bool busy = (st.Measured && st.CpuPct >= 2.0) || st.WsMb >= 300.0;
                    if (!busy) continue;
                }
                if (tunedOnly)
                {
                    bool pinned = st.Mask > 0 && allMask > 0 && st.Mask != allMask;
                    if (!st.EcoQoS && !pinned) continue;
                }
                hit.Add(st);
            }
            return hit;
        }

        /// <summary>为最终可见的 <=16 行经由 ProcessTable.EnrichRow 同步真值（优先级/亲和/EcoQoS）。</summary>
        private void Enrich(List<PStat> top)
        {
            if (C == null) return;
            for (int i = 0; i < top.Count; i++)
            {
                PStat s = top[i];
                if (s.Entry != null)
                {
                    C.Procs.EnrichRow(s.Entry);
                    s.PriClass = s.Entry.PriorityClass;
                    s.PriRank = s.Entry.PriorityRank;
                    s.PriLabel = s.Entry.PriorityLabel;
                    s.Mask = s.Entry.AffinityMask;
                    s.EcoQoS = s.Entry.EcoQoS;
                    s.EcoQoSKnown = s.Entry.EcoQoSKnown;
                    s.DetailsSynced = s.Entry.DetailsSynced;
                }
                if (string.IsNullOrEmpty(s.PriLabel) || s.PriLabel == "\u2014")
                    s.PriLabel = ProcessPower.PriorityLabel(s.PriClass);
                s.Prio = s.PriClass != 0 ? s.PriLabel : null;
            }
        }

        /// <summary>排序比较器：支持 进程名 / CPU / 内存 / 优先级（PriorityRank 全局排序）。</summary>
        private int Compare(PStat a, PStat b)
        {
            int c;
            if (_sort == SortCpu) c = a.CpuPct.CompareTo(b.CpuPct);
            else if (_sort == SortMem) c = a.WsMb.CompareTo(b.WsMb);
            else if (_sort == SortPri) c = a.PriRank.CompareTo(b.PriRank);
            else c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = a.CpuPct.CompareTo(b.CpuPct);
            if (c == 0) return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return _sortDesc ? -c : c;
        }

        // ---- 前台进程读数 ----------------------------------------------------
        private void UpdateFocus()
        {
            try
            {
                int pid = C == null ? 0 : C.LastForeignPid;
                if (pid == 0)
                {
                    SetText(_fgName, "切换到游戏窗口后自动识别");
                    _fgName.Foreground = T.S(T.Dim);
                    SetText(_fgPid, "PID —");
                    SetText(_fgAff, "   ·   亲和性 —");
                    SetText(_fgPri, "   ·   优先级 —");
                    _fgChip.Visibility = Visibility.Collapsed;
                    return;
                }
                bool denied, reused;
                bool alive = ProcName.IsAlive(pid, 0, out denied, out reused);
                if (!alive && !denied)
                {
                    SetText(_fgName, "—");
                    _fgName.Foreground = T.S(T.Dim);
                    SetText(_fgPid, "PID —");
                    SetText(_fgAff, "   ·   亲和性 —");
                    SetText(_fgPri, "   ·   优先级 —");
                    _fgChip.Visibility = Visibility.Collapsed;
                    return;
                }
                SetText(_fgName, ProcName.OrPid(pid));
                _fgName.Foreground = T.S(T.Hi);
                SetText(_fgPid, "PID " + pid);
                // 0 = 读不到（受保护进程 / 已退出 / 无权限）：显示破折号，绝不显示 0 或「全部处理器」。
                long mask = Affinity.Read(pid);
                SetText(_fgAff, mask > 0 ? ("   ·   亲和性 0x" + mask.ToString("X")) : "   ·   亲和性 —");
                uint priCls = ProcessPower.ReadPriority(pid);
                string pri = priCls != 0 ? ProcessPower.PriorityLabel(priCls) : null;
                SetText(_fgPri, string.IsNullOrEmpty(pri) ? "   ·   优先级 —" : ("   ·   优先级 " + pri));
                _fgChip.Visibility = (denied || mask <= 0 || priCls == 0) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception)
            {
                Diag.Hit("proc.focus");
                // 进程已退出 / 拒绝访问：页头读数退到"—"，不在界面留上一次的名字。
                SetText(_fgName, "—");
                _fgName.Foreground = T.S(T.Dim);
                SetText(_fgPid, "PID —");
                SetText(_fgAff, "   ·   亲和性 —");
                SetText(_fgPri, "   ·   优先级 —");
                _fgChip.Visibility = Visibility.Collapsed;
            }
        }
    }
}
