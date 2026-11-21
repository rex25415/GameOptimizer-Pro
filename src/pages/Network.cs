// ============================================================================
//  Network.cs — 06 网络与吞吐：页面骨架（类声明 / 字段 / 生命周期 / 分部粘合）
//  REV86：从"五个面板各说一件事"收敛成三段 + 一个写入口。
//    ① 读数带  下行 / 上行 / 延迟 P50 / 丢包，四格同基线，各带 30 样本走势；
//    ② 吞吐图  唯一一张 60 s 双线图，下面一行时间轴（−60 s … NOW）；
//    ③ 适配器表 名称 / 速率 / 状态徽标 / 操作；点行内联展开 TCP 连接与协议栈参数。
//  删掉的两份冗余：Network.AdapterCards（同一份适配器数据的第二种画法）与
//  Network.TcpTable（同一批 TCP 参数的第二种画法）。一条数据只留一个落点，否则
//  两处都要维护、两处都可能过期，读者还得自己判断该信哪一处。
//
//  分片：
//    Network.Band.cs     读数带：四格 + 30 样本走势 + 变化量（只画，不采样）
//    Network.Chart.cs    60 s 双线吞吐图（Y 轴带单位，时间轴单独一行）
//    Network.Adapters.cs 适配器表 / 行内展开 / 三种空态
//    Network.Tcp.cs      TCP 连接 + 协议栈参数（展开内容）
//    Network.Sample.cs   采样：1 Hz 字节差分 + 1 Hz ICMP（不碰任何控件）
//    Network.Actions.cs  本页动作行（唯一写入口）
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Network : Page
    {
        public override string Key { get { return "network"; } }
        public override string Idx { get { return "06"; } }
        public override string Title { get { return "网络与吞吐"; } }
        public override string En { get { return "Network"; } }
        public override string Ico { get { return Glyph.Network; } }

        /// <summary>区块间距：全页统一 18（DESIGN-86 §1.2），面板内部间距走 12。</summary>
        private const double BlockGap = 18;

        private TextBlock _headAdapter;
        private TextBlock _headRate;
        private Border _roChip;
        private int _beat;
        private int _netGen;

        public override UIElement Build(Core c)
        {
            _netGen++;
            _pidTcpBusy = false;
            _dualProbing = false;
            // Page 实例复用：每次 Build 都要把"本页持有的引用"归零，
            // 否则上一棵树里的缓存会让新树的第一次比较直接命中而跳过赋值。
            ResetBand();
            ResetAdapters();
            BuildAuditRows(c);          // 参数目录只建数据；表在"展开"时才建（见 Network.Tcp.cs）

            StackPanel body = new StackPanel();
            // Rev130：顶部四节点实时路由拓扑图
            body.Children.Add(BuildRouteTopology(c));
            body.Children.Add(Gap(BlockGap));

            // 中段：非对称 1.52* 左高幅双通道吞吐示波 / 右 1* 2×2 实时指标象限
            Grid midStage = new Grid();
            midStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.52, GridUnitType.Star) });
            midStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BlockGap) });
            midStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            midStage.Children.Add(BuildChart(c));
            UIElement band = BuildBand(c);
            Grid.SetColumn(band, 2);
            midStage.Children.Add(band);
            body.Children.Add(midStage);
            body.Children.Add(Gap(BlockGap));

            // 底段：左 1.16* 活动适配器表 / 右 1* 进程 TCP 雷达 + 协议栈动作
            Grid lowStage = new Grid();
            lowStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.16, GridUnitType.Star) });
            lowStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BlockGap) });
            lowStage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            lowStage.Children.Add(BuildAdapters(c));
            StackPanel rightLow = new StackPanel();
            rightLow.Children.Add(BuildPidTcpPanel(c));
            rightLow.Children.Add(Gap(BlockGap));
            rightLow.Children.Add(BuildActions(c));
            Grid.SetColumn(rightLow, 2);
            lowStage.Children.Add(rightLow);
            body.Children.Add(lowStage);

            body.Children.Add(Gap(14));
            body.Children.Add(BuildFootnote());

            Grid root = Ui.PageRoot(BuildHeader(c), body);
            // 以页根自身的可用宽度判定断点：侧栏折叠 / 窗口缩放都会重新回调。
            Rs.Watch(root, ApplyBp);

            Refresh();
            return root;
        }

        // ---- 页头 -----------------------------------------------------------
        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            _headAdapter = Ui.Mono("—", T.FsNano, T.Mute);
            _headAdapter.VerticalAlignment = VerticalAlignment.Center;
            _headAdapter.Margin = new Thickness(0, 0, 14, 2);
            _headAdapter.TextTrimming = TextTrimming.CharacterEllipsis;
            _headAdapter.MaxWidth = 240;
            right.Children.Add(_headAdapter);

            _headRate = Ui.Mono("↓ —   ↑ —  Mbps", T.FsMicro, T.Hi);
            _headRate.VerticalAlignment = VerticalAlignment.Center;
            _headRate.Margin = new Thickness(0, 0, 14, 2);
            right.Children.Add(_headRate);

            if (!c.Engine.Elevated)
            {
                // 与图形页/进程页同一种说法：全应用只有一种"没有管理员权限"的表达，
                // 否则同一个状态在不同页面写成 READ ONLY / 只读 / 需管理员，读者要重新学一遍。
                _roChip = Chip.Make("只读 · 需管理员", T.Warn, T.ChipHLg);
                _roChip.Margin = new Thickness(0, 0, 12, 4);
                right.Children.Add(_roChip);
            }

            Btn flushDns = new Btn("刷新 DNS", BtnKind.Quiet);
            flushDns.Height = T.BtnH;
            flushDns.Compact = true;
            flushDns.Margin = new Thickness(0, 0, 8, 0);
            flushDns.Click += delegate
            {
                bool ok = WinNative.FlushDnsCache();
                c.Engine.Ledger.AppendAudit("NET flush-dns -> " + ok);
                c.Shell.Toast(ok ? "系统 DNS 解析缓存已刷新（dnsapi.dll）" : "刷新 DNS 缓存失败", ok);
            };
            Tip.Attach(flushDns, "原生调用 dnsapi.dll 立即清空 Windows DNS 客户端解析缓存（零子进程）。");
            right.Children.Add(flushDns);

            Btn rescan = new Btn("重新采样", BtnKind.Ghost);
            rescan.Height = T.BtnH;
            rescan.SetIcon(Glyph.Refresh, 12);
            rescan.Click += delegate
            {
                Refresh();
                c.Shell.Toast("适配器、网关与 TCP 参数已重新读取", true);
            };
            Tip.Attach(rescan, "重新枚举活动接口、重测到网关的往返，并复读 TCP 参数；实时吞吐的采样窗口同时从零开始。");
            right.Children.Add(rescan);

            return Ui.PageHeader("MODULE / 06", "网络与吞吐",
                "NETWORK & THROUGHPUT — 下行 / 上行 / 延迟 P50 / 丢包 / 适配器与 TCP", right);
        }

        // ---- 状态刷新 -------------------------------------------------------
        /// <summary>重新读书：枚举适配器（后台）+ 动作回读 + 页头。不动走势历史。</summary>
        private void Refresh()
        {
            _pidTcpSig = null;
            RefreshAdapters();
            RefreshPidTcp();
            RefreshActions();
            TriggerDualProbe(1, 140);
            UpdateHead();
        }

        private void UpdateHead()
        {
            if (_headRate != null)
            {
                if (_measured)
                    _headRate.Text = "↓ " + FmtMbps(_rxMbps) + "   ↑ " + FmtMbps(_txMbps) + "  Mbps";
                else
                    _headRate.Text = "↓ —   ↑ —  Mbps";
            }
            if (_headAdapter != null)
            {
                string best = _busiest;
                if (!string.IsNullOrEmpty(best))
                    _headAdapter.Text = Ui.Trunc(best, 28, "—") + " · " + _adapterCount + " 接口";
                else
                    _headAdapter.Text = _adapterCount + " 接口";
            }
        }

        // ---- 自适应 ---------------------------------------------------------
        /// <summary>断点回调：只切换可见性与高度，不碰数据、不重采样。</summary>
        private void ApplyBp(Bp bp)
        {
            bool compact = bp == Bp.Compact;

            if (_roChip != null)
                _roChip.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            // 页头右侧留给速率与按钮：链路名属次要读数，Compact 下让位。
            if (_headAdapter != null)
                _headAdapter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

            // Compact 下把走势压短：先保读数带，再保图表形状。
            if (_chart != null) _chart.Height = compact ? 148 : 224;

            ApplyActionBp(compact);
        }

        // ---- 底部说明 -------------------------------------------------------
        private UIElement BuildFootnote()
        {
            StackPanel sp = new StackPanel();

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            Rectangle tick = new Rectangle();
            tick.Width = 1;
            tick.Height = 11;
            tick.Fill = T.S(T.LineHi);
            tick.Margin = new Thickness(0, 0, 10, 0);
            tick.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(tick);

            TextBlock t = Ui.Tx("吞吐 = 活动接口 IPv4 字节计数差分 ×8 ÷ 采样窗口；读不到计数时显示 —，不写 0。",
                T.FsNano, T.Mute, T.FCn);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            sp.Children.Add(g);

            TextBlock t2 = Ui.Tx("网关往返是 ICMP echo 往返，不等于游戏服务器延迟；Nagle / MMCSS 对已建立连接需重连才完全生效。",
                T.FsNano, T.Mute, T.FCn);
            t2.Margin = new Thickness(21, 6, 0, 0);
            t2.TextTrimming = TextTrimming.CharacterEllipsis;
            t2.TextWrapping = TextWrapping.NoWrap;
            sp.Children.Add(t2);
            return sp;
        }

        // ---- 生命周期 -------------------------------------------------------
        public override void OnEnter()
        {
            Refresh();
            StartSampler();
        }

        public override void OnLeave()
        {
            _netGen++;
            _pidTcpBusy = false;
            _dualProbing = false;
            _adapterLoading = false;
            StopSampler();
        }

        /// <summary>1 Hz 节拍：吞吐与 ICMP 由页面自己的采样器驱动，这里只做低频回读。</summary>
        public override void Tick(Snapshot s)
        {
            _beat++;
            if ((_beat % 5) != 0) return;
            RefreshActions();
            RefreshPidTcp();
            TriggerDualProbe(2, 300);
            UpdateHead();
            RefreshOpenRow();
        }
    }
}