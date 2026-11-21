// ============================================================================
//  Network.Topology.cs — Rev130 网络与吞吐：实时四节点路由拓扑图 (Route Topology)
//  可视化呈现 [本机 PC] → [链路 / TUN 隧道] → [LAN 网关] → [WAN 公网]
//  实时接入 NetTrace.DualPathLatencyReport 与 TelemetryEngine.ClassifyRoute。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Go.Pages
{
    public partial class Network : Page
    {
        private TextBlock _rtGradeText, _rtVerdictText;
        private Border _rtGradeBadge;
        private TextBlock _rtNicTitle, _rtNicSub;
        private TextBlock _rtLanTitle, _rtLanSub, _rtHop1Tag;
        private TextBlock _rtWanTitle, _rtWanSub, _rtHop2Tag;
        private Border _rtNicNode, _rtLanNode, _rtWanNode;

        private UIElement BuildRouteTopology(Core c)
        {
            StackPanel body = new StackPanel();

            // 头部：标题 + 链路质量评级徽标
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            head.Children.Add(Ui.PanelHead("链路路由拓扑", "LIVE ROUTE TOPOLOGY · PC \u2192 NIC/TUN \u2192 LAN \u2192 WAN", null));

            _rtGradeBadge = new Border();
            _rtGradeBadge.CornerRadius = new CornerRadius(T.RadiusMd);
            _rtGradeBadge.BorderThickness = new Thickness(1);
            _rtGradeBadge.BorderBrush = T.S(T.LineMid);
            _rtGradeBadge.Background = T.S(T.Inset);
            _rtGradeBadge.Padding = new Thickness(10, 3, 10, 3);
            _rtGradeBadge.VerticalAlignment = VerticalAlignment.Center;
            _rtGradeText = Ui.Mono("待测", T.FsNano, T.Mute);
            _rtGradeText.FontWeight = FontWeights.SemiBold;
            _rtGradeBadge.Child = _rtGradeText;
            Grid.SetColumn(_rtGradeBadge, 1);
            head.Children.Add(_rtGradeBadge);
            body.Children.Add(head);
            body.Children.Add(Gap(12));

            // 4 节点信号路径：Node0 --Hop0--> Node1 --Hop1--> Node2 --Hop2--> Node3
            Grid path = new Grid();
            path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.18, GridUnitType.Star) });
            path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) });
            path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) });

            TextBlock pcTitle, pcSub;
            Border pcNode = MakeRouteNode(Glyph.Cpu, "本机端点", Environment.MachineName, T.Accent, out pcTitle, out pcSub);
            path.Children.Add(pcNode);

            TextBlock hop0Tag;
            UIElement hop0 = MakeHopLink("本地栈", T.AccentA(0x88), out hop0Tag);
            Grid.SetColumn(hop0, 1);
            path.Children.Add(hop0);

            _rtNicNode = MakeRouteNode(Glyph.Network, "活动网卡 / 隧道", "等待适配器枚举…", T.Accent2, out _rtNicTitle, out _rtNicSub);
            Grid.SetColumn(_rtNicNode, 2);
            path.Children.Add(_rtNicNode);

            UIElement hop1 = MakeHopLink("LAN \u2014", T.Accent, out _rtHop1Tag);
            Grid.SetColumn(hop1, 3);
            path.Children.Add(hop1);

            _rtLanNode = MakeRouteNode(Glyph.Shield, "LAN 默认网关", "探测中…", T.Ok, out _rtLanTitle, out _rtLanSub);
            Grid.SetColumn(_rtLanNode, 4);
            path.Children.Add(_rtLanNode);

            UIElement hop2 = MakeHopLink("WAN \u2014", T.Ok, out _rtHop2Tag);
            Grid.SetColumn(hop2, 5);
            path.Children.Add(hop2);

            _rtWanNode = MakeRouteNode(Glyph.Bolt, "WAN 公网节点", "223.5.5.5 / 114 DNS", T.Violet, out _rtWanTitle, out _rtWanSub);
            Grid.SetColumn(_rtWanNode, 6);
            path.Children.Add(_rtWanNode);

            body.Children.Add(path);
            body.Children.Add(Gap(10));

            _rtVerdictText = Ui.Mono("双路径探针待测 · 切到本页自动探测局域网网关与公网延迟", T.FsNano, T.Mute);
            _rtVerdictText.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(_rtVerdictText);

            Grid hero = new Grid();
            hero.Children.Add(Ui.HeroPanel(body, 16));
            hero.Children.Add(Ui.CornerTicks(T.AccentA(0x80), 10, 1.5));
            return hero;
        }

        private static Border MakeRouteNode(string ico, string title, string sub, uint tint, out TextBlock titleTb, out TextBlock subTb)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            Border emblem = new Border();
            emblem.Width = 28; emblem.Height = 28;
            emblem.CornerRadius = new CornerRadius(7);
            emblem.Background = T.S(Ui.Wash(tint, 0x20));
            emblem.BorderBrush = T.S(Ui.Wash(tint, 0x64));
            emblem.BorderThickness = new Thickness(1);
            emblem.VerticalAlignment = VerticalAlignment.Center;
            emblem.Margin = new Thickness(0, 0, 10, 0);
            emblem.Child = Ui.Icon(ico, 13, tint, 1.5);
            g.Children.Add(emblem);

            StackPanel txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleTb = Ui.Tx(title, T.FsSmall, T.Hi, T.FCn);
            titleTb.FontWeight = FontWeights.SemiBold;
            titleTb.TextTrimming = TextTrimming.CharacterEllipsis;
            txt.Children.Add(titleTb);

            subTb = Ui.Mono(sub, T.FsNano, T.Mute);
            subTb.Margin = new Thickness(0, 2, 0, 0);
            subTb.TextTrimming = TextTrimming.CharacterEllipsis;
            txt.Children.Add(subTb);
            Grid.SetColumn(txt, 1);
            g.Children.Add(txt);

            return Ui.GlassTile(g, 10, tint);
        }

        private static UIElement MakeHopLink(string label, uint tint, out TextBlock tag)
        {
            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.HorizontalAlignment = HorizontalAlignment.Stretch;
            sp.Margin = new Thickness(4, 0, 4, 0);

            tag = Ui.Mono(label, 9.5, T.Dim);
            tag.HorizontalAlignment = HorizontalAlignment.Center;
            tag.Margin = new Thickness(0, 0, 0, 4);
            sp.Children.Add(tag);

            Grid lineWrap = new Grid { Height = 6 };
            Rectangle bar = new Rectangle();
            bar.Height = 1.5;
            bar.VerticalAlignment = VerticalAlignment.Center;
            bar.Fill = T.Grad(Ui.Wash(tint, 0x35), Ui.Wash(tint, 0xC8), 0);
            lineWrap.Children.Add(bar);

            Ellipse head = new Ellipse();
            head.Width = 5; head.Height = 5;
            head.Fill = T.S(tint);
            head.HorizontalAlignment = HorizontalAlignment.Right;
            head.VerticalAlignment = VerticalAlignment.Center;
            lineWrap.Children.Add(head);

            sp.Children.Add(lineWrap);
            return sp;
        }

        private void UpdateRouteAdapters(System.Collections.Generic.List<AdapterInfo> cards)
        {
            if (_rtNicTitle == null || _rtNicSub == null) return;
            if (cards == null || cards.Count == 0)
            {
                _rtNicTitle.Text = "无活动网卡";
                _rtNicSub.Text = "未发现 Up 接口";
                _rtNicSub.Foreground = T.S(T.Warn);
                return;
            }
            AdapterInfo tun = null, phys = null;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].IsVirtual && tun == null) tun = cards[i];
                else if (!cards[i].IsVirtual && phys == null) phys = cards[i];
            }
            if (tun != null)
            {
                _rtNicTitle.Text = tun.Name + " · TUN";
                _rtNicSub.Text = !string.IsNullOrEmpty(tun.VirtualKind) ? tun.VirtualKind : "虚拟隧道接管";
                _rtNicSub.Foreground = T.S(T.Warn);
            }
            else
            {
                AdapterInfo a = phys ?? cards[0];
                _rtNicTitle.Text = a.Name;
                _rtNicSub.Text = (string.IsNullOrEmpty(a.Ipv4) ? "—" : a.Ipv4) + " · " + cards.Count + " 接口";
                _rtNicSub.Foreground = T.S(T.Ok);
            }
        }

        private void UpdateRouteTopology(DualPathLatencyReport rep)
        {
            if (_rtGradeText == null || rep == null) return;
            NetRouteSummary rs = TelemetryEngine.ClassifyRoute(rep);

            _rtGradeText.Text = rs.Grade;
            _rtGradeText.Foreground = T.S(rs.Tint);
            if (_rtGradeBadge != null)
            {
                _rtGradeBadge.BorderBrush = T.S(Ui.Wash(rs.Tint, 0x66));
                _rtGradeBadge.Background = T.S(Ui.Wash(rs.Tint, 0x1A));
            }
            if (_rtVerdictText != null)
            {
                _rtVerdictText.Text = rs.HopVerdict;
                _rtVerdictText.Foreground = T.S(rs.Tint == T.Warn ? T.Warn : T.Dim);
            }

            if (_rtNicTitle != null && _rtNicSub != null)
            {
                if (rep.TunnelActive)
                {
                    _rtNicTitle.Text = "TUN / VPN 隧道活跃";
                    _rtNicSub.Text = !string.IsNullOrEmpty(rep.ActiveTunnelName) ? rep.ActiveTunnelName : "虚拟隧道接管";
                    _rtNicSub.Foreground = T.S(T.Warn);
                }
                else
                {
                    _rtNicTitle.Text = string.IsNullOrEmpty(rep.GatewayInterface) ? "物理活动链路" : rep.GatewayInterface;
                    _rtNicSub.Text = "直连物理网卡";
                    _rtNicSub.Foreground = T.S(T.Ok);
                }
            }

            if (_rtLanSub != null && _rtHop1Tag != null)
            {
                bool ok = rep.Lan != null && rep.Lan.Reachable && rep.Lan.RttMs >= 0;
                _rtLanSub.Text = string.IsNullOrEmpty(rep.GatewayIp) ? "未发现网关" : rep.GatewayIp;
                _rtHop1Tag.Text = ok ? (rep.Lan.RttMs.ToString("0.#") + " ms") : "LAN \u2014";
                _rtHop1Tag.Foreground = T.S(ok ? PathTint(rep.Lan) : T.Mute);
            }

            if (_rtWanSub != null && _rtHop2Tag != null)
            {
                bool ok = rep.Wan != null && rep.Wan.Reachable && rep.Wan.RttMs >= 0;
                _rtWanSub.Text = (rep.Wan != null && !string.IsNullOrEmpty(rep.Wan.Target)) ? rep.Wan.Target : "公网不可达";
                string hop = ok ? (rep.Wan.RttMs.ToString("0.#") + " ms") : "WAN \u2014";
                if (ok && rep.RouteDeltaMs >= 0) hop = "+" + rep.RouteDeltaMs.ToString("0.#") + " ms";
                _rtHop2Tag.Text = hop;
                _rtHop2Tag.Foreground = T.S(ok ? PathTint(rep.Wan) : T.Warn);
            }
        }
    }
}
