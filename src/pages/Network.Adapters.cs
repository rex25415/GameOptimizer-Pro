// ============================================================================
//  Network.Adapters.cs — 适配器表：枚举 / 行网格 / 行内展开 / 空态（Network 分片）
//
//  一张表回答三个问题：这条链路叫什么、现在多快、点开能看到什么。
//  列宽在 Columnize 里是唯一真源，表头与每一行共用同一份数字；
//  与进程页对齐的公共列：值/读数 112、徽标 96、操作 56，行高 50。
//
//  枚举走后台线程（Wi-Fi 机器上 GetAllNetworkInterfaces 要 100-300 ms），
//  UI 线程只拿 DTO 建行；展开里的 TCP 连接同样走后台。
//  一次只摊开一行（手风琴）：同时展开两张表，读者又要在两处之间找差异。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Network : Page
    {
        // ---- 行网格：唯一一份列宽 ------------------------------------------
        private const double RowH = 50;
        private const double ColRate = 112;    // 值/读数
        private const double ColBadge = 96;    // 徽标
        private const double ColOp = 56;       // 控件

        private StackPanel _adapterHost;
        private TextBlock _adapterMeta;
        private int _adapterCount;
        private bool _adapterLoading;
        private string _busiest = "";

        private readonly List<AdapterRow> _cards = new List<AdapterRow>();
        private List<AdapterInfo> _adapterInfos = new List<AdapterInfo>();
        private AdapterRow _open;

        private sealed class AdapterInfo
        {
            public string Name, Desc, Ipv4, Mac, Id, VirtualKind;
            public long Speed;
            public int Index;
            public bool IsVirtual;
            public NetworkInterfaceType Type;
            public NetworkInterface Ni;
        }

        /// <summary>一行适配器上会被 1 Hz 采样与 5 s 回读改动的元素；骨架只建一次。</summary>
        private sealed class AdapterRow
        {
            public string Id, Name, Ipv4;
            public Grid Root;
            public TextBlock RxVal, TxVal;
            public Tag State;
            public Btn Toggle;
            public Border Expand;          // 懒填内容的容器（骨架在 Build 时就占好位）
            public StackPanel ConnHost;
            public TextBlock ConnMeta;
            public List<AuditRow> AuditRows;
            public TextBlock AuditMeta;
            public bool Open, ConnBusy;
            public long ConnSig = -1;
            public int RxCs = -1, TxCs = -1;
        }

        /// <summary>可重绘徽标：Ui.Tag 是一次性的，这里每秒可能改字与色（只碰两个 DP）。</summary>
        private sealed class Tag
        {
            public Border Box;
            public TextBlock Text;
            public uint Tint;
            public string Shown;

            public void Paint(string text, uint tint)
            {
                if (string.IsNullOrEmpty(text))
                {
                    if (Box.Visibility != Visibility.Collapsed) Box.Visibility = Visibility.Collapsed;
                    return;
                }
                if (Box.Visibility != Visibility.Visible) Box.Visibility = Visibility.Visible;
                if (text != Shown) { Shown = text; Text.Text = text; }
                if (tint == Tint) return;
                Tint = tint;
                Text.Foreground = T.S(tint);
                Box.BorderBrush = T.S(Ui.Wash(tint, 0x38));
            }
        }

        private static Tag MakeTag(string text, uint tint)
        {
            Tag t = new Tag();
            t.Box = new Border();
            t.Box.CornerRadius = new CornerRadius(3);
            t.Box.BorderThickness = new Thickness(1);
            t.Box.Padding = new Thickness(5, 0, 5, 0);
            t.Box.VerticalAlignment = VerticalAlignment.Center;
            t.Box.HorizontalAlignment = HorizontalAlignment.Left;
            t.Box.BorderBrush = T.S(Ui.Wash(tint, 0x38));
            t.Text = Ui.Mono(text, T.FsNano, tint);
            t.Text.VerticalAlignment = VerticalAlignment.Center;
            t.Box.Child = t.Text;
            t.Tint = tint;
            t.Shown = text;
            return t;
        }

        // ---- 面板 -----------------------------------------------------------
        private UIElement BuildAdapters(Core c)
        {
            StackPanel body = new StackPanel();
            _adapterMeta = Ui.Mono("—", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("网络适配器", "ADAPTERS · LINK / WINDOW RATE / TCP", _adapterMeta));
            body.Children.Add(Gap(12));

            _adapterHost = new StackPanel();
            body.Children.Add(_adapterHost);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));
            TextBlock foot = Ui.Tx("接口来自 NetworkInterface（OperationalStatus.Up，排除环回）。速率是本页 1 s 窗口内的 IPv4 字节差分，" +
                                   "标称链路速率写在名称下；点一行展开它的 TCP 连接与协议栈参数。", T.FsMicro, T.Mute, T.FCn);
            foot.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(foot);

            return Ui.Panel(body, T.PadPanel);
        }

        private void ResetAdapters()
        {
            _adapterHost = null;
            _adapterMeta = null;
            _adapterCount = 0;
            _adapterLoading = false;
            _busiest = "";
            _open = null;
            _cards.Clear();
            _adapterInfos = new List<AdapterInfo>();
            _accounts.Clear();
        }

        // ---- 枚举（后台）---------------------------------------------------
        private void RefreshAdapters()
        {
            if (_adapterHost == null) return;
            // 门闩 _adapterLoading 保证同一时刻只有一条枚举线程，不会每次进页面堆一条。
            if (_adapterLoading) return;
            _adapterLoading = true;
            _adapterHost.Children.Clear();
            _adapterHost.Children.Add(Ui.Muted("正在读取适配器…", T.FsSmall));

            int gen = _netGen;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            Thread t = new Thread(delegate ()
            {
                List<AdapterInfo> list = CollectAdapters();
                ui.BeginInvoke(new Action(delegate ()
                {
                    if (gen != _netGen) return;
                    _adapterLoading = false;
                    ApplyAdapters(list);
                }));
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "go.net";
            t.Start();
        }

        private List<AdapterInfo> CollectAdapters()
        {
            List<AdapterInfo> list = new List<AdapterInfo>();
            try
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < all.Length; i++)
                {
                    AdapterInfo a = Describe(all[i]);
                    if (a != null) list.Add(a);
                }
            }
            catch (Exception ex) { Diag.Fail("net.adapters.enum", ex); }
            return list;
        }

        /// <summary>把一张网卡读成 DTO；只要 Up 的非环回接口。读不到字段给 —，不猜。</summary>
        private static AdapterInfo Describe(NetworkInterface ni)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) return null;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return null;
            AdapterInfo a = new AdapterInfo();
            a.Name = ni.Name;
            a.Desc = ni.Description;
            a.Speed = ni.Speed;
            a.Type = ni.NetworkInterfaceType;
            a.Id = ni.Id;
            a.Ni = ni;
            a.Ipv4 = Ipv4Text(ni);
            a.Mac = MacText(ni);
            a.Index = SafeIndex(ni);
            string vk;
            a.IsVirtual = NetTrace.IsVirtualTunNic(ni, out vk);
            a.VirtualKind = vk ?? "";
            return a;
        }

        // ---- 落表 -----------------------------------------------------------
        private void ApplyAdapters(List<AdapterInfo> cards)
        {
            if (_adapterHost == null) return;
            if (cards == null) cards = new List<AdapterInfo>();
            _adapterCount = cards.Count;
            _adapterHost.Children.Clear();
            _cards.Clear();
            _adapterInfos = cards;
            _open = null;
            _busiest = "";

            if (cards.Count == 0)
            {
                // 空态必须解释自己：没有接口 ≠ 接口全空闲。
                _adapterHost.Children.Add(Ui.Empty(Glyph.Network, "未检测到活动适配器",
                    "NetworkInterface 枚举里没有 OperationalStatus.Up 的非环回接口：网卡可能被禁用、仍在初始化，或链路上只有一条断开的连接。",
                    T.Warn, RetryButton()));
            }
            else
            {
                _adapterHost.Children.Add(BuildTableHead());
                for (int i = 0; i < cards.Count; i++) _adapterHost.Children.Add(BuildRowHost(cards[i]));
                if (_chartHint != null) _chartHint.Text = "等待首个采样窗口（切到本页即开始）";
            }

            if (_adapterMeta != null)
                _adapterMeta.Text = cards.Count == 0 ? "NO ADAPTER" : (cards.Count + " 接口 · 待测");

            if (cards.Count == 0 && _chartHint != null)
            {
                _chartHint.Text = "未检测到活动适配器，无法采样吞吐";
                _chartHint.Visibility = Visibility.Visible;
            }

            // 吞吐计数以下一轮窗口从新表起算：旧计数不能和新的网卡集合做差。
            UpdateRouteAdapters(cards);
            PrimeThroughput();
        }

        private Btn RetryButton()
        {
            Btn b = new Btn("重新枚举", BtnKind.Ghost);
            b.Height = T.BtnH;
            b.SetIcon(Glyph.Refresh, 12);
            b.Click += delegate
            {
                Refresh();
                if (C != null && C.Shell != null) C.Shell.Toast("已重新枚举活动接口", true);
            };
            return b;
        }

        /// <summary>表头与行共用这份列定义——同一页出现两套列宽就是失败。</summary>
        private static void Columnize(Grid g)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColRate) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColBadge) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColOp) });
        }

        private Border BuildTableHead()
        {
            Grid g = new Grid();
            Columnize(g);
            HeadText(g, "名称 / 链路", 0, HorizontalAlignment.Left);
            HeadText(g, "↓ / ↑ Mbps", 1, HorizontalAlignment.Right);
            HeadText(g, "状态", 2, HorizontalAlignment.Left);
            HeadText(g, "操作", 3, HorizontalAlignment.Right);

            Border band = new Border();
            band.Child = g;
            band.BorderBrush = T.S(T.Line);
            band.BorderThickness = new Thickness(0, 0, 0, 1);
            band.Padding = new Thickness(0, 0, 0, 7);
            band.Margin = new Thickness(0, 0, 0, 2);
            band.SnapsToDevicePixels = true;
            return band;
        }

        private static void HeadText(Grid g, string s, int col, HorizontalAlignment ha)
        {
            TextBlock t = Ui.Label(s, T.Mute);
            t.HorizontalAlignment = ha;
            if (col == 1) t.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        /// <summary>一行 + 它的展开容器：两者必须相邻，展开才长在这张网卡下面。</summary>
        private UIElement BuildRowHost(AdapterInfo a)
        {
            AdapterRow r = new AdapterRow();

            StackPanel cell = new StackPanel();
            r.Root = BuildGrid(r, a);
            cell.Children.Add(r.Root);

            Border expand = new Border();
            expand.Visibility = Visibility.Collapsed;
            expand.Margin = new Thickness(0, 0, 0, 2);
            r.Expand = expand;
            cell.Children.Add(expand);

            _cards.Add(r);
            return cell;
        }

        private Grid BuildGrid(AdapterRow r, AdapterInfo a)
        {
            Grid row = new Grid();
            row.Height = RowH;
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
            Columnize(row);

            // 名称：主名 + TUN/VPN 显式徽标 + 一行副信息（IPv4 · 类型 · 标称速率）
            StackPanel nm = new StackPanel();
            nm.VerticalAlignment = VerticalAlignment.Center;
            nm.Margin = new Thickness(0, 0, 14, 0);

            Grid nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition());
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock name = Ui.Tx(a.Name, T.FsBody, T.Hi, T.FCn);
            name.FontWeight = FontWeights.SemiBold;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            name.VerticalAlignment = VerticalAlignment.Center;
            nameRow.Children.Add(name);
            if (a.IsVirtual)
            {
                string bText = string.IsNullOrEmpty(a.VirtualKind) || a.VirtualKind == "TUN" || a.VirtualKind == "VPN/TUN"
                    ? "TUN/VPN"
                    : "TUN/VPN · " + a.VirtualKind;
                Border vTag = Ui.Tag(bText, T.Warn);
                vTag.Margin = new Thickness(8, 0, 0, 0);
                vTag.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(vTag, 1);
                nameRow.Children.Add(vTag);
            }
            nm.Children.Add(nameRow);

            string subInfo = a.Ipv4 + " · " + TypeTag(a.Type) + " · " + SpeedText(a.Speed);
            if (a.IsVirtual) subInfo += " · 虚拟隧道";
            TextBlock sub = Ui.Mono(subInfo, T.FsNano, T.Mute);
            sub.Margin = new Thickness(0, 4, 0, 0);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.TextWrapping = TextWrapping.NoWrap;
            nm.Children.Add(sub);
            Grid.SetColumn(nm, 0);
            row.Children.Add(nm);

            // 速率：本窗口实测（不是标称链路速率），↓ 强调色 / ↑ 紫，与读数带、图同色。
            StackPanel rate = new StackPanel();
            rate.Orientation = Orientation.Horizontal;
            rate.HorizontalAlignment = HorizontalAlignment.Right;
            rate.VerticalAlignment = VerticalAlignment.Center;
            rate.Margin = new Thickness(0, 0, 14, 0);   // 与状态徽标之间留 14：右对齐读数不能贴住下一列

            TextBlock down = Ui.Mono("↓", T.FsNano, T.Accent);
            down.Margin = new Thickness(0, 0, 4, 0);
            rate.Children.Add(down);
            r.RxVal = Ui.Mono("—", T.FsSmall, T.Mute);
            r.RxVal.Margin = new Thickness(0, 0, 10, 0);
            rate.Children.Add(r.RxVal);

            TextBlock up = Ui.Mono("↑", T.FsNano, T.Violet);
            up.Margin = new Thickness(0, 0, 4, 0);
            rate.Children.Add(up);
            r.TxVal = Ui.Mono("—", T.FsSmall, T.Mute);
            rate.Children.Add(r.TxVal);

            Grid.SetColumn(rate, 1);
            row.Children.Add(rate);

            r.State = MakeTag("待测", T.Dim);
            Grid.SetColumn(r.State.Box, 2);
            row.Children.Add(r.State.Box);

            r.Toggle = new Btn("展开", BtnKind.Quiet);
            r.Toggle.Height = T.BtnHSm;
            r.Toggle.Compact = true;
            r.Toggle.HorizontalAlignment = HorizontalAlignment.Right;
            r.Toggle.VerticalAlignment = VerticalAlignment.Center;
            r.Toggle.Click += delegate { ToggleRow(r); };
            Grid.SetColumn(r.Toggle, 3);
            row.Children.Add(r.Toggle);

            r.Id = a.Id;
            r.Name = a.Name;
            r.Ipv4 = a.Ipv4;
            Tip.Attach(row, "接口索引 " + (a.Index < 0 ? "—" : a.Index.ToString()) + " · " + TypeTag(a.Type) + " · 标称 " + SpeedText(a.Speed)
                         + (a.IsVirtual ? "\nTUN/VPN 虚拟隧道接口（" + a.VirtualKind + "）：代理/隧道流量将绕行此卡" : "")
                         + "\n" + Ui.Trunc(a.Desc, 60, "—") + "\n点行展开 TCP 连接与协议栈参数。");

            row.MouseEnter += delegate { if (!r.Open) row.Background = T.S(T.LineFaint); };
            row.MouseLeave += delegate { if (!r.Open) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += delegate { ToggleRow(r); };
            return row;
        }

        // ---- 采样回写 -------------------------------------------------------
        /// <summary>1 Hz 采样回调：把本窗口的每卡字节差分写成两列速率与状态徽标。</summary>
        private void ApplyTraffic(List<AdapterTraffic> list)
        {
            if (list == null) return;
            string leader = "";
            long best = 0;
            int talking = 0;

            for (int i = 0; i < list.Count; i++)
            {
                AdapterTraffic t = list[i];
                AdapterRow r = FindRow(t.Id);
                if (r == null) continue;
                PaintRowTraffic(r, t);
                if (t.Bytes > 0) talking++;
                if (t.Bytes > best) { best = t.Bytes; leader = t.Id; }
            }

            _busiest = "";
            for (int i = 0; i < _cards.Count; i++)
                if (leader.Length > 0 && _cards[i].Id == leader) _busiest = _cards[i].Name;

            if (_adapterMeta != null)
                _adapterMeta.Text = _adapterCount + " 接口 · " + talking + " 有流量";
        }

        private AdapterRow FindRow(string id)
        {
            for (int i = 0; i < _cards.Count; i++)
                if (_cards[i].Id == id) return _cards[i];
            return null;
        }

        private void PaintRowTraffic(AdapterRow r, AdapterTraffic t)
        {
            int cs = ToCs(t.Rx);
            if (cs != r.RxCs)
            {
                r.RxCs = cs;
                r.RxVal.Text = FmtMbps(t.Rx);
                r.RxVal.Foreground = T.S(T.Text);
            }
            cs = ToCs(t.Tx);
            if (cs != r.TxCs)
            {
                r.TxCs = cs;
                r.TxVal.Text = FmtMbps(t.Tx);
                r.TxVal.Foreground = T.S(T.Text);
            }
            r.State.Paint(t.Bytes > 0 ? "活动" : "空闲", t.Bytes > 0 ? T.Accent : T.Mute);
        }

        // ---- 展开 -----------------------------------------------------------
        private void ToggleRow(AdapterRow r)
        {
            if (r == null) return;
            if (r.Open) { SetOpen(r, false); return; }
            if (_open != null && _open != r) SetOpen(_open, false);
            SetOpen(r, true);
        }

        private void SetOpen(AdapterRow r, bool open)
        {
            r.Open = open;
            if (open) _open = r;
            else if (_open == r) _open = null;
            if (open && r.Expand.Child == null)
            {
                r.Expand.Child = BuildExpand(r);
            }
            r.Expand.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            r.Root.Background = open ? T.S(T.Raised) : Brushes.Transparent;
            r.Toggle.Label = open ? "收起" : "展开";
            if (open)
            {
                RefreshTable();      // 每次展开都立即复读参数表，确保当前展开行是最新真值
                RefreshConns(r);
            }
        }

        private UIElement BuildExpand(AdapterRow r)
        {
            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock t = Ui.Label("TCP 连接 / 本适配器", T.AccentDim);
            head.Children.Add(t);
            r.ConnMeta = Ui.Mono("正在读取…", T.FsNano, T.Mute);
            r.ConnMeta.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(r.ConnMeta, 1);
            head.Children.Add(r.ConnMeta);
            body.Children.Add(head);
            body.Children.Add(Gap(8));

            r.ConnHost = new StackPanel();
            r.ConnHost.Children.Add(Ui.Muted("正在读取 TCP 连接表…", T.FsMicro));
            body.Children.Add(r.ConnHost);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));
            body.Children.Add(BuildAuditBlock(r));

            Border inset = Ui.Inset(body, T.PadInset);
            inset.Margin = new Thickness(0, 0, 0, 0);
            return inset;
        }

        /// <summary>展开行每 5 s 复读一次：连接表是活的，摊开着却不动就是在撒谎。</summary>
        private void RefreshOpenRow()
        {
            if (_open == null || !_open.Open) return;
            RefreshConns(_open);
            RefreshTable();
        }

        private void RefreshConns(AdapterRow r)
        {
            if (r == null || r.ConnBusy || r.ConnHost == null) return;
            r.ConnBusy = true;
            string ip = r.Ipv4;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            Thread t = new Thread(delegate ()
            {
                ConnResult res = ReadConns(ip);
                ui.BeginInvoke(new Action(delegate ()
                {
                    r.ConnBusy = false;
                    if (r.Open && r.ConnHost != null) PaintConns(r, res);
                }));
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "go.tcpconn";
            t.Start();
        }

        private void PaintConns(AdapterRow r, ConnResult res)
        {
            if (r.ConnHost == null) return;
            long sig = ConnSig(res);
            if (sig == r.ConnSig) return;
            r.ConnSig = sig;
            r.ConnHost.Children.Clear();

            if (r.ConnMeta != null)
                r.ConnMeta.Text = res.Failed
                    ? "读不到"
                    : (res.Rows.Count + " 条归属本机地址 · 全机 " + res.All + " 条");

            if (res.Failed)
            {
                r.ConnHost.Children.Add(EmptyBox(Glyph.Warn, "抓不到 TCP 连接表",
                    "IPGlobalProperties.GetActiveTcpConnections 失败：" + Ui.Trunc(res.Why, 80, "未知原因")
                    + "。常见原因：安全软件拦截了系统连接表查询。", T.Warn));
                return;
            }
            if (res.Rows.Count == 0)
            {
                r.ConnHost.Children.Add(EmptyBox(Glyph.Network, "本适配器没有 TCP 连接",
                    "全机当前 " + res.All + " 条连接的本地地址都不是 " + Ui.Trunc(r.Ipv4, 24, "—")
                    + "（监听在 0.0.0.0 的套接字不归属任何单张网卡）。", T.Dim));
                return;
            }
            r.ConnHost.Children.Add(BuildConnTable(res.Rows));
        }

        /// <summary>连接表：本地端口 / 远端 / 状态。最多 8 行，多出来的写成一行计数。</summary>
        private static Grid BuildConnTable(List<ConnRow> rows)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            int extra = rows.Count - MaxConnRows;
            int n = Math.Min(rows.Count, MaxConnRows);
            // 行 0 = 表头，1..n = 数据，最后一行为"还有 N 条"留位——多出来的行不能落到数据行上。
            for (int i = 0; i <= n + 1; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            ConnHead(g, "本地端口", 0);
            ConnHead(g, "远端地址", 1);
            ConnHead(g, "状态", 2);
            g.Children.Add(Hair(0, T.Line));

            for (int i = 0; i < n; i++)
            {
                ConnRow c = rows[i];
                g.Children.Add(Cell(Ui.Mono(c.Local, T.FsNano, T.Dim), 0, i + 1));
                g.Children.Add(Cell(Ui.Mono(c.Remote, T.FsNano, T.Text), 1, i + 1));
                g.Children.Add(Cell(Ui.Mono(c.State, T.FsNano, c.State == "ESTABLISHED" ? T.AccentDim : T.Mute), 2, i + 1));
                if (i < n - 1) g.Children.Add(Hair(i + 1, T.LineSoft));
            }
            if (extra > 0)
            {
                TextBlock more = Ui.Mono("还有 " + extra + " 条未列出（本表最多 " + MaxConnRows + " 行）", T.FsNano, T.Mute);
                more.Margin = new Thickness(0, 6, 0, 0);
                Grid.SetRow(more, n + 1);
                Grid.SetColumnSpan(more, 3);
                g.Children.Add(more);
            }
            return g;
        }

        private static void ConnHead(Grid g, string s, int col)
        {
            TextBlock t = Ui.Label(s, T.Mute);
            t.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        private static Border EmptyBox(string glyph, string title, string reason, uint tint)
        {
            Border b = new Border();
            b.Height = 136;
            b.Child = Ui.Empty(glyph, title, reason, tint, null);
            return b;
        }

        /// <summary>内容签名：内容没变就不重建行（展开着每 5 s 一次重建等于白造一棵树）。</summary>
        private static long ConnSig(ConnResult res)
        {
            long h = 1469598103934665603L;
            h = (h ^ res.All) * 1099511628211L;
            if (res.Failed) return h ^ 0x5A5A5A5AL;
            for (int i = 0; i < res.Rows.Count; i++)
            {
                ConnRow r = res.Rows[i];
                h = (h ^ r.Port) * 1099511628211L;
                h = Fold(h, r.Remote);
                h = Fold(h, r.State);
            }
            return h;
        }

        private static long Fold(long h, string s)
        {
            if (s == null) return h * 1099511628211L;
            for (int i = 0; i < s.Length; i++) h = (h ^ s[i]) * 1099511628211L;
            return h;
        }

        // ---- 字段格式化 -----------------------------------------------------
        private static string TypeTag(NetworkInterfaceType t)
        {
            switch (t)
            {
                case NetworkInterfaceType.Ethernet: return "ETHERNET";
                case NetworkInterfaceType.GigabitEthernet: return "GBE";
                case NetworkInterfaceType.FastEthernetT: return "FAST ETH";
                case NetworkInterfaceType.FastEthernetFx: return "FAST ETH";
                case NetworkInterfaceType.Wireless80211: return "WIRELESS";
                case NetworkInterfaceType.Ppp: return "PPP";
                case NetworkInterfaceType.Tunnel: return "TUNNEL";
                // 53/243 是 IF_TYPE 里未进 .NET 枚举的虚拟接口（VPN / 代理 TUN 常见）。
                case (NetworkInterfaceType)53: return "VIRTUAL";
                case (NetworkInterfaceType)243: return "VIRTUAL";
                default: return "IF " + ((int)t);
            }
        }

        /// <summary>标称链路速率：按量级选单位，100 Mbit 的网口不该读成 0.1 Gbps。</summary>
        private static string SpeedText(long bps)
        {
            if (bps <= 0) return "—";
            if (bps >= 1000000000L) return (bps / 1000000000.0).ToString("0.#") + " Gbps";
            if (bps >= 1000000L) return (bps / 1000000.0).ToString("0.#") + " Mbps";
            if (bps >= 1000L) return (bps / 1000.0).ToString("0.#") + " kbps";
            return bps + " bps";
        }

        private static string Ipv4Text(NetworkInterface ni)
        {
            try
            {
                string apipa = null;
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = u.Address.ToString();
                    if (s.StartsWith("169.254.")) { apipa = s; continue; }
                    return s;
                }
                return apipa == null ? "—" : apipa;
            }
            catch { return "—"; }   // 接口在两拍之间被拔出：地址读不到就是 —
        }

        private static string MacText(NetworkInterface ni)
        {
            try
            {
                byte[] b = ni.GetPhysicalAddress().GetAddressBytes();
                if (b == null || b.Length == 0) return "—";
                string s = "";
                for (int i = 0; i < b.Length; i++)
                {
                    if (i > 0) s += ":";
                    s += b[i].ToString("X2");
                }
                return s;
            }
            catch { return "—"; }
        }

        private static int SafeIndex(NetworkInterface ni)
        {
            try
            {
                IPv4InterfaceProperties p = ni.GetIPProperties().GetIPv4Properties();
                return p == null ? -1 : p.Index;
            }
            catch { return -1; }
        }
    }
}