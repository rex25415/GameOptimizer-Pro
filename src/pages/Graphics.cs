// ============================================================================
//  Graphics.cs — 05 图形栈：页根 / 顶部读数带（Graphics 主片）
//
//  这一页按"读数 → 适配器 → 引擎"排三层，每层只回答一个问题：
//    1) 这台机器是什么卡、跑得怎么样？ → 顶部四格读数带（适配器数 / 驱动版本 / GPU 占用 / 显存）
//    2) 有哪几张卡、各自什么状态？     → 适配器行网格（名称 + 驱动版本 + 独显/核显 + 操作）
//    3) 驱动到底在怎么调度？           → 呈现管线（HAGS / MPO / FSE）+ 动作区 + 首选项
//
//  Rev86 的结构性改动：旧版没有统一的读数带，适配器是两张横排的卡片——驱动版本塞在
//  卡片里的"事实行"，读者要在两张卡之间来回找同一个字段；GPU 页又用一个 60 号 hero
//  重复了"占用"这个数。现在数值只进读数带，适配器只进行网格：列 2 的驱动版本是等宽的，
//  列 3 的徽标告诉你是独显还是核显，列 4 是行内操作。
//
//  读数带里"驱动版本"是唯一非数值的一格：版本号按 FsTitle 放进与 32 号数字等高的槽里
//  （38px），四格的注脚因此仍落在同一条基线上——同基线不是"字号相同"，是"底线相同"。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Graphics : Page
    {
        public override string Key { get { return "graphics"; } }
        public override string Idx { get { return "05"; } }
        public override string Title { get { return "图形栈"; } }
        public override string En { get { return "Graphics"; } }
        public override string Ico { get { return Glyph.Graphics; } }

        private const string AdapterClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        // 区块间距 18（DESIGN-86 §1.2）：和内存页同一个数，换页时段落节奏不变。
        private const double BlockGap = 18;

        // ---- 内部数据 ------------------------------------------------------
        private sealed class GpuInfo
        {
            public string Name = "";
            public string ShortName = "";
            public string Driver = "";
            public string Luid = "";
            public string AuxLuid = "";
            public string DeviceName = "";
            public long VramBytes;
            public int DiscreteKnown = -1; // 1 独显 / 0 核显 / 2 虚拟 / -1 未知
            public bool Present;
            public bool PresentKnown;
            public long ResW;
            public long ResH;
            public long Hz;
        }

        private sealed class PrefInfo
        {
            public string ExePath = "";
            public string Raw = "";
            public int Mode = -1;
        }

        private sealed class PipeState
        {
            public bool Ok;
            public string State = "\u2014";
            public string Raw = "\u2014";
            public string Desc = "";
        }

        private sealed class PipeRef
        {
            public Ellipse Dot;
            public TextBlock State;
            public TextBlock Raw;
            public TextBlock Desc;
        }

        // ---- 读数带 --------------------------------------------------------
        private sealed class BandCell
        {
            public TextBlock Val;
            public TextBlock Note;
            public DeltaChip Delta;
            public SegBar Bar;
        }

        private BandCell _cellAdapters, _cellDriver, _cellGpu, _cellVram;
        private DateTime _builtAt;

        // ---- 页面状态 ------------------------------------------------------
        private TextBlock _headMeta, _adapterMeta, _pipeMeta, _actionMeta, _prefMeta;
        private StackPanel _adapterHost, _prefHost;
        private PipeRef _hags, _mpo, _fse;
        private DispatcherTimer _poll;
        /// <summary>IO 慢拍计数器：注册表类读数每 5 个快拍复核一次。</summary>
        private int _ioBeat;
        private bool _firstEnter = true;
        private volatile int _buildGen;
        private volatile int _scanGen;
        private volatile bool _left;

        // ---- 页面构建 ------------------------------------------------------
        public override UIElement Build(Core c)
        {
            _builtAt = DateTime.UtcNow;
            _buildGen++;
            _scanGen++;
            _left = false;
            _scanning = false;
            _adapterList = null;
            _adapterScanned = false;
            _vramTotal = 0;
            _ioBeat = 0;
            ResetGpuState();

            StackPanel page = new StackPanel();

            // Rev130：顶部非对称硬件展台（左 1.24* 虚拟多屏蓝图画布 / 右 1* 2×2 图形遥测象限）
            Grid topStage = new Grid();
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.24, GridUnitType.Star) });
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BlockGap) });
            topStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            topStage.Children.Add(BuildDisplays(c));
            UIElement readout = BuildReadoutBand(c);
            Grid.SetColumn(readout, 2);
            topStage.Children.Add(readout);
            page.Children.Add(topStage);
            page.Children.Add(Gap(BlockGap));

            // 中段：左 1.24* GPU 适配器表 / 右 1* 实时 PDH 引擎占有率
            Grid midStage = new Grid();
            midStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.24, GridUnitType.Star) });
            midStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BlockGap) });
            midStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            midStage.Children.Add(BuildAdapters(c));
            UIElement liveGpu = BuildGpuLive(c);
            Grid.SetColumn(liveGpu, 2);
            midStage.Children.Add(liveGpu);
            page.Children.Add(midStage);
            page.Children.Add(Gap(BlockGap));

            // 底段：左 1* 呈现管线 / 右 1.15* 图形动作 + 应用显卡首选项
            Grid botStage = new Grid();
            botStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            botStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BlockGap) });
            botStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            botStage.Children.Add(BuildPipeline(c));
            StackPanel rightBot = new StackPanel();
            rightBot.Children.Add(BuildActions(c));
            rightBot.Children.Add(Gap(BlockGap));
            rightBot.Children.Add(BuildPrefs(c));
            Grid.SetColumn(rightBot, 2);
            botStage.Children.Add(rightBot);
            page.Children.Add(botStage);

            // 页根接管外边距与页头间距：本页原先自己写 Padding，与其它页的页头
            // 落点差 4px —— 换页时标题会横跳一下，那是最容易被注意到、也最难说清的抖。
            Grid root = Ui.PageRoot(BuildHeader(c), page);

            Watch(Dirty.Power, delegate { if (_actRows.Count > 0) RefreshActions(false); });

            RefreshAdapters();
            RefreshPipeline();
            RefreshActions(false);
            RefreshPrefs();
            _firstEnter = true;

            // 以本页根元素的可用宽度判定断点（侧栏折叠 / 窗口缩放都会重新回调）。
            Rs.Watch(root, ApplyBp);
            return root;
        }

        // ---- 页头 ----------------------------------------------------------
        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            _headMeta = Ui.Mono("SCAN —", T.FsNano, T.Mute);
            _headMeta.Margin = new Thickness(0, 0, 14, 9);
            right.Children.Add(_headMeta);

            if (!c.Elevated)
            {
                Border ro = Chip.Make("只读 · 需管理员", T.Warn, T.ChipHLg);
                ro.Margin = new Thickness(0, 0, 12, 6);
                right.Children.Add(ro);
            }

            Btn rescan = new Btn("重新探测", BtnKind.Ghost);
            rescan.Height = T.BtnH;
            rescan.SetIcon(Glyph.Refresh, 12);
            rescan.Click += delegate
            {
                RefreshAdapters(true, false);
                RefreshPipeline();
                RefreshActions(true);
                RefreshPrefs();
                c.Shell.Toast("已重新探测显卡、显示输出与图形栈状态", true);
            };
            Tip.Attach(rescan, "重新读取 DXGI LUID 适配器、原生显示输出拓扑与 UserGpuPreferences。");
            right.Children.Add(rescan);

            return Ui.PageHeader("MODULE / 05", "图形栈", "显示适配器 · 呈现路径逐项判定 · 每次改动可逆", right);
        }

        // ====================================================================
        //  Rev130 2×2 四象限图形遥测卡（停靠在虚拟多屏画布右侧）
        // ====================================================================
        private UIElement BuildReadoutBand(Core c)
        {
            StackPanel body = new StackPanel();
            TextBlock meta = Ui.Mono("DXGI LUID · PDH", T.FsNano, T.Mute);
            meta.Opacity = 0.9;
            body.Children.Add(Ui.PanelHead("显卡态势", "GPU TELEMETRY · ADAPTER / ENGINE", meta));
            body.Children.Add(Gap(12));

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition());

            _cellAdapters = NumCell(grid, 0, 0, "适配器", "ADAPTERS", "", T.Accent, false, false);
            _cellDriver = DriverCell(grid, 2, 0);
            _cellGpu = NumCell(grid, 0, 2, "GPU 占用", "GPU UTIL", "%", T.Ok, true, true);
            _cellVram = NumCell(grid, 2, 2, "显存", "VRAM", "MB", T.Violet, false, true);
            body.Children.Add(grid);
            return Ui.Panel(body, T.PadPanel);
        }

        private BandCell NumCell(Grid host, int col, int row, string cn, string en, string unit, uint tint, bool withDelta, bool withBar)
        {
            BandCell c = new BandCell();
            c.Val = Ui.Num("\u2014", T.FsNumL, T.Hi);
            if (withBar)
            {
                c.Bar = new SegBar(0);
                c.Bar.Height = 3;
                c.Bar.Tint = tint;
            }
            c.Note = Ui.Mono("等待探测", T.FsNano, T.Mute);
            c.Note.TextTrimming = TextTrimming.CharacterEllipsis;
            c.Note.TextWrapping = TextWrapping.NoWrap;

            UIElement head;
            if (withDelta)
            {
                c.Delta = new DeltaChip();
                c.Delta.Set(0, "", false);
                head = Head(c.Val, unit, c.Delta);
            }
            else
            {
                head = Ui.Readout(c.Val, unit);
            }

            UIElement mid = withBar ? (UIElement)c.Bar : (UIElement)Spacer(3);
            Border tile = Ui.GlassTile(Ui.Stat(cn + " " + en, head, mid, c.Note), 12, tint);
            Grid.SetColumn(tile, col);
            Grid.SetRow(tile, row);
            host.Children.Add(tile);
            return c;
        }

        private BandCell DriverCell(Grid host, int col, int row)
        {
            BandCell c = new BandCell();
            c.Val = Ui.Mono("\u2014", T.FsTitle, T.Hi);
            c.Val.TextTrimming = TextTrimming.CharacterEllipsis;
            c.Val.TextWrapping = TextWrapping.NoWrap;
            c.Val.VerticalAlignment = VerticalAlignment.Bottom;

            Border slot = new Border();
            slot.Height = 38;
            slot.Child = c.Val;

            c.Note = Ui.Mono("等待探测", T.FsNano, T.Mute);
            c.Note.TextTrimming = TextTrimming.CharacterEllipsis;
            c.Note.TextWrapping = TextWrapping.NoWrap;
            Border tile = Ui.GlassTile(Ui.Stat("驱动版本 DRIVER", slot, Spacer(3), c.Note), 12, T.Accent2);
            Grid.SetColumn(tile, col);
            Grid.SetRow(tile, row);
            host.Children.Add(tile);
            return c;
        }

        /// <summary>占位 3px：没有量条的格子用它对齐——四格的中段必须一样高。</summary>
        private static Border Spacer(double h)
        {
            Border b = new Border();
            b.Height = h;
            b.Background = Brushes.Transparent;
            b.IsHitTestVisible = false;
            return b;
        }

        /// <summary>读数头：数值 · 单位 · 变化量同一行（与内存页 / 总览同构）。</summary>
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

            delta.Margin = new Thickness(10, 0, 0, 8);
            delta.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(delta, 2);
            g.Children.Add(delta);
            return g;
        }

        private void Set(TextBlock t, string s)
        {
            if (t == null) return;
            if (!string.IsNullOrEmpty(s))
            {
                if (_cellDriver != null && object.ReferenceEquals(t, _cellDriver.Note))
                    s = CompactDriverNote(s);
                else if (_cellGpu != null && object.ReferenceEquals(t, _cellGpu.Note))
                    s = s.Replace(" \u00B7 \u6309 LUID \u5206\u5361", " \u00B7 LUID");
            }
            if (t.Text != s) t.Text = s;
        }

        private static string CompactDriverNote(string s)
        {
            if (s.StartsWith("NVIDIA GeForce ", StringComparison.OrdinalIgnoreCase))
                return "GeForce " + s.Substring("NVIDIA GeForce ".Length);
            if (s.StartsWith("AMD Radeon(TM) ", StringComparison.OrdinalIgnoreCase))
                return "Radeon " + s.Substring("AMD Radeon(TM) ".Length);
            if (s.StartsWith("AMD Radeon ", StringComparison.OrdinalIgnoreCase))
                return "Radeon " + s.Substring("AMD Radeon ".Length);
            if (s.StartsWith("Intel(R) ", StringComparison.OrdinalIgnoreCase))
                return "Intel " + s.Substring("Intel(R) ".Length);
            return s;
        }
    }
}
