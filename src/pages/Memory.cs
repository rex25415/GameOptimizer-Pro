// ============================================================================
//  Memory.cs — 04 内存与提交：页根 / 生命周期 / 公共小工具（Memory 主片）
//
//  这一页按"读数 → 构成 → 分项"排三层，每层只回答一个问题：
//    1) 现在多少？      → 顶部四格读数带：已用 / 可用 / 待机 / 已修改。
//    2) 被谁占着？      → 一条 MemRibbon 缎带 + 同序图例：物理页的四种去向。
//    3) 谁在吃 / 还有什么账 → 吃内存前几名（行网格）/ 内核池 / 提交与页面文件 / 动作。
//
//  Rev86 的结构性改动：旧版把"提交压力"做成 60 号 hero，右边再排三格 32 号读数——
//  一屏出现两个"最大数字"，读者不知道先看谁；构成条用 WPF Grid 手拼一套、内核池又有
//  一套 MeterBar + 图例，同一个"构成"要学两遍。现在数值只进读数带，构成只进 MemRibbon，
//  提交压力退成读数带下面的一行判词：它回答的是"够不够"，不是"多少"。
//
//  诚实性：待机 / 已修改来自 NtQuerySystemInformation(80)，查询失败时两者同时留在 0。
//  我们把"两者同时为 0"判为未测量并显示 —，而不是画一根 0 长的条冒充"没有待机页"。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Memory : Page
    {
        public override string Key { get { return "memory"; } }
        public override string Idx { get { return "04"; } }
        public override string Title { get { return "内存与提交"; } }
        public override string En { get { return "Memory"; } }
        public override string Ico { get { return Glyph.Memory; } }

        // 进程扫描：5 秒一次，后台线程。绝不 500ms 全表扫。
        private const int ProcRows = 5;
        private const int ScanEveryBeats = 5;

        // 区块间距 18（DESIGN-86 §1.2）。T.Gap(14) 是面板内部的呼吸，不是区块间距——
        // 两个数混用之后，页面上所有东西看起来都属于同一段，段落感消失。
        private const double BlockGap = 12;

        // 空闲页的灰：缎带、读数带的"可用"量条、图例色块共用同一个值。
        // 同色同序是读者把"这一格"和"那一段"对上的唯一线索。
        private const uint FreeTint = 0x2EFFFFFF;

        // ---- 读数带 --------------------------------------------------------
        private MemCell _cellUsed, _cellAvail, _cellStby, _cellMod;
        private Border _verdictDot;
        private TextBlock _verdictWord, _verdictLine, _commitLine;

        // ---- 构成缎带 ------------------------------------------------------
        private MemRibbon _ribbon;
        private double[] _ribParts;
        private uint[] _ribTint;
        private Border _ribTrack;
        private TextBlock _ribWait;
        private TextBlock[] _legVal;
        private TextBlock _compMeta;
        private long _ribSig = long.MinValue;
        private int _compMetaKey = int.MinValue;
        private readonly int[] _legKey = new int[4];

        // ---- 内核池 --------------------------------------------------------
        private TextBlock _poolPagedVal, _poolNonpagedVal;
        private MeterBar _poolPagedBar, _poolNonpagedBar;
        private TextBlock _poolMeta;

        // ---- 提交与页面文件 ------------------------------------------------
        private TextBlock _cmtLine, _cmtHead, _pageFile;
        private RatioRuler _ruler;

        // ---- 进程排行 ------------------------------------------------------
        private sealed class ProcMem
        {
            public int Pid;
            public long Born;
            public string Name;
            public string Image;
            public long Ws;
            public long Priv;
            public bool IsProtected;
            public bool EcoKnown;
            public bool EcoOn;
        }

        private sealed class ScanResult
        {
            public readonly List<ProcMem> Rows = new List<ProcMem>();
            public long SelfWs;
            public int Total;
        }

        private sealed class ProcRow
        {
            public Border Root;
            public TextBlock Rank, Name, Ws, Priv;
            public Border EcoBadge;
            public TextBlock EcoBadgeText;
            public Btn EcoBtn;
            public ProcMem Mem;
        }

        private ProcRow[] _rows = new ProcRow[ProcRows];
        private TextBlock _topMeta;
        private FrameworkElement _topEmpty;
        private bool _scanning;
        private int _scanGen;
        private int _beat;

        // ---- 动作 ----------------------------------------------------------
        private Grid _purgeChipHost;
        private string _purgeChipKey = "";
        private TextBlock _purgeChipText;
        private int _purgeChipTenth = int.MinValue;
        private TextBlock _selfWs;
        private TextBlock _actMeta;
        private System.Windows.Threading.DispatcherTimer _purgeTimer;

        // ====================================================================
        //  页面
        // ====================================================================
        public override UIElement Build(Core c)
        {
            _scanGen++;
            _scanning = false;
            if (_purgeTimer != null) _purgeTimer.Stop();
            _purgeChipKey = "";
            _purgeChipTenth = int.MinValue;
            _ribSig = long.MinValue;
            _compMetaKey = int.MinValue;
            for (int i = 0; i < _legKey.Length; i++) _legKey[i] = int.MinValue;
            _poolPagedKey = int.MinValue;
            _poolNonpagedKey = int.MinValue;
            _poolMetaKey = int.MinValue;
            _commitKey = long.MinValue;
            _verdictKey = long.MinValue;
            _dUsed = int.MinValue;
            _dAvail = int.MinValue;
            _dStby = int.MinValue;
            _dMod = int.MinValue;

            StackPanel body = new StackPanel();

            // Rev130：非对称立体内存态势台
            // 顶部：左 1.52*（立体物理构成缎带 + 2×2 实时读数象限） / 右 1*（提交压力尺 + 一键待机释放指令台）
            Grid topStage = new Grid();
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.52, GridUnitType.Star) });
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BlockGap) });
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            StackPanel leftTop = new StackPanel();
            leftTop.Children.Add(BuildComposition(c));
            leftTop.Children.Add(Gap(BlockGap));
            leftTop.Children.Add(BuildReadoutBand(c));
            topStage.Children.Add(leftTop);

            StackPanel rightTop = new StackPanel();
            rightTop.Children.Add(BuildCommit(c));
            rightTop.Children.Add(Gap(BlockGap));
            rightTop.Children.Add(BuildActions(c));
            Grid.SetColumn(rightTop, 2);
            topStage.Children.Add(rightTop);
            body.Children.Add(topStage);

            body.Children.Add(Gap(BlockGap));

            // 底部：左 1.42*（吃内存进程排行 + 能效压制） / 右 1*（内核分页与非分页池诊断）
            Grid lowStage = new Grid();
            lowStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.42, GridUnitType.Star) });
            lowStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BlockGap) });
            lowStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            UIElement topProc = BuildTop(c);
            lowStage.Children.Add(topProc);

            UIElement kpool = BuildKernel(c);
            Grid.SetColumn(kpool, 2);
            lowStage.Children.Add(kpool);
            body.Children.Add(lowStage);

            UIElement root = Ui.PageRoot(BuildHeader(c), body);

            // 离屏渲染没有 Tick：首帧必须在这里取一次真实读数。
            Apply(c == null ? null : c.Perf.Current);
            LoadTop(!Ui.Animate);
            return root;
        }

        private UIElement BuildHeader(Core c)
        {
            TextBlock right = Ui.Mono("1 HZ · 实测", T.FsNano, T.Mute);
            right.VerticalAlignment = VerticalAlignment.Bottom;
            right.Margin = new Thickness(0, 0, 2, 7);
            return Ui.PageHeader("MODULE / 04", "内存与提交",
                "四格读数 · 物理页构成 · 进程占用 · 本机实测，未测量显示 —", right);
        }

        private static TextBlock Src(string s)
        {
            TextBlock t = Ui.Mono(s, T.FsNano, T.Mute);
            t.Opacity = 0.9;
            return t;
        }

        // ====================================================================
        //  生命周期
        // ====================================================================
        public override void OnEnter()
        {
            if (C == null) return;
            Apply(C.Perf.Current);
            LoadTop(false);
        }

        public override void OnLeave()
        {
            _scanGen++;
            _scanning = false;
            if (_purgeTimer != null) _purgeTimer.Stop();
        }

        public override void Tick(Snapshot s)
        {
            if (s == null) return;
            Apply(s);
            _beat++;
            if ((_beat % ScanEveryBeats) == 0) LoadTop(false);
        }

        // ====================================================================
        //  小工具
        // ====================================================================
        private static void Set(TextBlock t, string s)
        {
            if (t != null && t.Text != s) t.Text = s;
        }

        private static string Gb(double mb)
        {
            return (mb / 1024.0).ToString("0.0");
        }

        /// <summary>提交压力保留一位小数：89.6% 与 90% 在阈值上意味着不同的判词。</summary>
        private static string Pct1(double v)
        {
            return (v * 100.0).ToString("0.0") + "%";
        }

        private static string Mb(double mb)
        {
            return Group(mb) + " MB";
        }

        /// <summary>千位分隔用窄空格：等宽数字读起来仍然是「一个数」，不会被逗号切成两截。</summary>
        private static string Group(double v)
        {
            long n = (long)Math.Round(v);
            string s = Math.Abs(n).ToString();
            if (s.Length <= 3) return n < 0 ? "-" + s : s;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length + s.Length / 3);
            int lead = s.Length % 3;
            if (n < 0) sb.Append('-');
            if (lead > 0)
            {
                sb.Append(s, 0, lead);
                if (s.Length > lead) sb.Append(' ');
            }
            for (int i = lead; i < s.Length; i += 3)
            {
                sb.Append(s, i, 3);
                if (i + 3 < s.Length) sb.Append(' ');
            }
            return sb.ToString();
        }
    }
}
