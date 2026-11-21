// ============================================================================
//  Profiles.Discover.cs — 多源零配置发现（Profiles 分片）
//  支持 Steam / Epic / WeGame / Xbox / 桌面快捷方式五源扫描、源筛选与滚动批量建档。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Profiles : Page
    {
        private StackPanel _discoverHost;
        private StackPanel _reportHost;
        private Go.Daemon.ScanResult _lastScanResult;
        private string _discoverSource = "all";
        private bool _discoverExpanded;
        private volatile int _discoverGen;
        private bool _discoverScanning;

        private const int DiscoverCap = 12;

        // ====================================================================
        //  自动发现
        // ====================================================================
        private void RunDiscover(Core c)
        {
            if (_discoverHost == null) return;
            int gen = ++_discoverGen;
            StackPanel host = _discoverHost;
            _discoverScanning = true;
            _discoverExpanded = false;
            _discoverHost.Visibility = Visibility.Visible;
            _discoverHost.Children.Clear();

            StackPanel body = new StackPanel();
            body.Children.Add(Ui.Mono("\u6B63\u5728\u626B\u63CF Steam / Epic / WeGame / Xbox / \u684C\u9762\u5FEB\u6377\u65B9\u5F0F\u2026", T.FsMicro, T.Mute));
            _discoverHost.Children.Add(Ui.Panel(body, T.PadPanel));

            Dispatcher ui = Dispatcher.CurrentDispatcher;
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                Go.Daemon.ScanResult res = null;
                try { res = Go.Daemon.GameScanner.Scan(); }
                catch (Exception ex) { Diag.Fail("discover.scan", ex); }
                if (gen != _discoverGen) return;
                try
                {
                    ui.BeginInvoke(new Action(delegate
                    {
                        if (gen != _discoverGen || _discoverHost == null || !object.ReferenceEquals(_discoverHost, host)) return;
                        _discoverScanning = false;
                        ShowDiscover(c, res);
                    }));
                }
                catch (Exception ex) { Diag.Fail("discover.marshal", ex); }
            });
            th.IsBackground = true;
            th.Priority = System.Threading.ThreadPriority.BelowNormal;
            th.Name = "go.gamescan";
            th.Start();
        }

        private void ShowDiscover(Core c, Go.Daemon.ScanResult res)
        {
            if (_discoverHost == null || c == null) return;
            _lastScanResult = res;
            _discoverHost.Children.Clear();

            StackPanel body = new StackPanel();
            if (res == null)
            {
                body.Children.Add(BuildDiscoverHead(c, null, null));
                body.Children.Add(Gap(10));
                body.Children.Add(Line("\u626B\u63CF\u5931\u8D25\uFF1A\u65E0\u6CD5\u8BFB\u53D6\u542F\u52A8\u5668\u4E0E\u5FEB\u6377\u65B9\u5F0F\u6E05\u5355", T.Warn));
                _discoverHost.Children.Add(Ui.Panel(body, T.PadPanel));
                return;
            }

            List<Go.Daemon.FoundGame> allFresh = new List<Go.Daemon.FoundGame>();
            foreach (Go.Daemon.FoundGame g in res.Games)
                if (c.Profiles.FindByExe(g.Exe) == null) allFresh.Add(g);

            List<Go.Daemon.FoundGame> visible = FilterBySource(allFresh, _discoverSource);

            body.Children.Add(BuildDiscoverHead(c, res, visible));
            body.Children.Add(Gap(10));
            body.Children.Add(BuildDiscoverTabs(c, res, allFresh));
            body.Children.Add(Gap(10));

            if (res.Count == 0)
            {
                bool anySource = res.SteamFound || res.EpicFound || res.WeGameFound || res.XboxFound || res.ShortcutsFound;
                body.Children.Add(Line(anySource
                    ? "\u5DF2\u626B\u63CF\u6E05\u5355\u4F46\u672A\u5B9A\u4F4D\u5230\u53EF\u6267\u884C\u6E38\u620F\u672C\u4F53\uFF08\u53EF\u80FD\u5C1A\u672A\u5B89\u88C5\u5B8C\u6210\uFF09"
                    : "\u672A\u68C0\u6D4B\u5230 Steam / Epic / WeGame / Xbox \u6216\u684C\u9762\u6E38\u620F\u5FEB\u6377\u65B9\u5F0F", T.Mute));
            }
            else if (allFresh.Count == 0)
            {
                body.Children.Add(Line("\u53D1\u73B0\u7684 " + res.Count + " \u4E2A\u6E38\u620F\u90FD\u5DF2\u7ECF\u5EFA\u8FC7\u6863\u4E86", T.Dim));
            }
            else if (visible.Count == 0)
            {
                body.Children.Add(Line("\u5F53\u524D\u6765\u6E90\u300C" + _discoverSource + "\u300D\u4E0B\u6682\u65E0\u672A\u5EFA\u6863\u6E38\u620F", T.Mute));
            }
            else
            {
                int shown = _discoverExpanded ? visible.Count : Math.Min(DiscoverCap, visible.Count);
                StackPanel list = new StackPanel();
                for (int i = 0; i < shown; i++) list.Children.Add(DiscoverRow(c, visible[i]));

                if (shown > 6)
                {
                    ThinScroll sc = new ThinScroll(list);
                    sc.View.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    sc.MaxHeight = _discoverExpanded ? 420 : 340;
                    body.Children.Add(sc);
                }
                else
                {
                    body.Children.Add(list);
                }

                if (visible.Count > DiscoverCap)
                    body.Children.Add(BuildDiscoverFoot(c, res, visible.Count, shown));
            }
            _discoverHost.Children.Add(Ui.Panel(body, T.PadPanel));
        }

        private UIElement BuildDiscoverTabs(Core c, Go.Daemon.ScanResult res, List<Go.Daemon.FoundGame> allFresh)
        {
            string[] keys = { "all", "Steam", "WeGame", "Epic", "Xbox", "\u684C\u9762" };
            string[] names = { "\u5168\u90E8", "Steam", "WeGame", "Epic", "Xbox", "\u684C\u9762" };
            Ui.SegTabItem[] items = new Ui.SegTabItem[keys.Length];
            int sel = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                int cnt = CountSource(allFresh, keys[i]);
                string lbl = cnt > 0 && i > 0 ? (names[i] + " " + cnt) : names[i];
                items[i] = new Ui.SegTabItem(keys[i], lbl, "\u7B5B\u9009\u6765\u6E90\uFF1A" + names[i] + " (" + cnt + " \u4E2A\u672A\u5EFA\u6863)");
                if (string.Equals(keys[i], _discoverSource, StringComparison.Ordinal)) sel = i;
            }
            Ui.SegTabs tabs = new Ui.SegTabs(items, sel);
            tabs.Changed += delegate
            {
                _discoverSource = tabs.SelectedKey;
                _discoverExpanded = false;
                ShowDiscover(c, res);
            };
            return tabs;
        }

        private UIElement BuildDiscoverFoot(Core c, Go.Daemon.ScanResult res, int total, int shown)
        {
            Grid foot = new Grid();
            foot.Margin = new Thickness(0, 10, 0, 0);
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock hint = Ui.Mono(_discoverExpanded
                ? ("\u5DF2\u5C55\u5F00\u5168\u90E8 " + total + " \u4E2A\u53D1\u73B0\u7ED3\u679C\uFF08\u53EF\u5728\u5217\u8868\u5185\u6EDA\u52A8\uFF09")
                : ("\u5DF2\u663E\u793A " + shown + " / " + total + " \u4E2A\u672A\u5EFA\u6863\u6E38\u620F"), T.FsNano, T.Mute);
            hint.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(hint);

            Btn toggle = new Btn(_discoverExpanded ? "\u6536\u8D77\u5217\u8868" : ("\u5C55\u5F00\u4F59\u4E0B " + (total - shown) + " \u4E2A"), BtnKind.Quiet);
            toggle.Height = T.BtnHSm;
            toggle.Compact = true;
            toggle.Click += delegate
            {
                _discoverExpanded = !_discoverExpanded;
                ShowDiscover(c, res);
            };
            Grid.SetColumn(toggle, 1);
            foot.Children.Add(toggle);
            return foot;
        }

        /// <summary>发现块的头：五源事实摘要 + 一个「全部建档」按钮（只在当前列表有新游戏时出现）。</summary>
        private UIElement BuildDiscoverHead(Core c, Go.Daemon.ScanResult res, List<Go.Daemon.FoundGame> visibleFresh)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel mid = new StackPanel();
            mid.Children.Add(Ui.Tx("\u591A\u6E90\u81EA\u52A8\u53D1\u73B0", T.FsBody, T.Hi, T.FCn));
            TextBlock m = Ui.Mono(DiscoverSummary(res), T.FsNano, T.Mute);
            m.Margin = new Thickness(0, 3, 0, 0);
            m.TextTrimming = TextTrimming.CharacterEllipsis;
            mid.Children.Add(m);
            g.Children.Add(mid);

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;

            if (visibleFresh != null && visibleFresh.Count > 0)
            {
                Btn all = new Btn("\u5168\u90E8\u5EFA\u6863 (" + visibleFresh.Count + ")", BtnKind.Quiet);
                all.Height = T.BtnHSm;
                all.Margin = new Thickness(0, 0, 8, 0);
                all.Click += delegate { ImportAll(c, visibleFresh); };
                right.Children.Add(all);
            }

            Btn hide = new Btn("\u6536\u8D77", BtnKind.Ghost);
            hide.Height = T.BtnHSm;
            hide.Compact = true;
            hide.Click += delegate
            {
                _discoverGen++;
                _discoverScanning = false;
                _discoverHost.Visibility = Visibility.Collapsed;
            };
            right.Children.Add(hide);

            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            return g;
        }

        private static string DiscoverSummary(Go.Daemon.ScanResult res)
        {
            if (res == null) return "\u626B\u63CF\u5931\u8D25";
            List<string> src = new List<string>(5);
            if (res.SteamFound) src.Add("Steam " + res.SteamLibraries + " \u5E93");
            if (res.WeGameFound) src.Add("WeGame " + res.WeGameLocations + " \u9879");
            if (res.EpicFound) src.Add("Epic " + res.EpicLocations + " \u9879");
            if (res.XboxFound) src.Add("Xbox " + res.XboxLocations + " \u9879");
            if (res.ShortcutsFound) src.Add("\u684C\u9762 " + res.ShortcutLocations + " \u9879");
            string head = src.Count > 0 ? (string.Join(" \u00B7 ", src.ToArray()) + " \u00B7 ") : "";
            return head + res.Count + " \u4E2A\u5DF2\u5B89\u88C5 \u00B7 " + res.ElapsedMs + " ms";
        }

        private static List<Go.Daemon.FoundGame> FilterBySource(List<Go.Daemon.FoundGame> list, string src)
        {
            if (src == "all" || string.IsNullOrEmpty(src)) return list;
            List<Go.Daemon.FoundGame> outList = new List<Go.Daemon.FoundGame>();
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Source, src, StringComparison.Ordinal)) outList.Add(list[i]);
            return outList;
        }

        private static int CountSource(List<Go.Daemon.FoundGame> list, string src)
        {
            if (src == "all" || string.IsNullOrEmpty(src)) return list.Count;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Source, src, StringComparison.Ordinal)) n++;
            return n;
        }

        private static uint SourceTint(string source)
        {
            if (source == "Steam") return T.Accent;
            if (source == "WeGame") return T.Ok;
            if (source == "Epic") return T.SeriesB;
            if (source == "Xbox") return T.SeriesA;
            if (source == "\u684C\u9762") return T.Warn;
            return T.Text;
        }

        /// <summary>发现行：与档案行同一套列（列 1 给实测体量，列 2 给清晰来源徽标，列 3 建档）。</summary>
        private UIElement DiscoverRow(Core c, Go.Daemon.FoundGame g)
        {
            Grid row = RowGrid();
            row.Height = RowH;
            row.Margin = new Thickness(16, 0, 0, 0);
            row.Background = Brushes.Transparent;

            StackPanel mid = new StackPanel();
            mid.VerticalAlignment = VerticalAlignment.Center;
            TextBlock name = Ui.Tx(g.Name, T.FsBody, T.Text, T.FCn);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            mid.Children.Add(name);
            TextBlock path = Ui.Mono(Ui.ShortPath(g.Exe), T.FsNano, T.Mute);
            path.Margin = new Thickness(0, 3, 0, 0);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.TextWrapping = TextWrapping.NoWrap;
            mid.Children.Add(path);
            row.Children.Add(mid);

            string sz = g.SizeText;
            TextBlock size = Ui.Mono(string.IsNullOrEmpty(sz) ? "\u2014" : sz, T.FsMicro, string.IsNullOrEmpty(sz) ? T.Mute : T.Dim);
            size.HorizontalAlignment = HorizontalAlignment.Right;
            size.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(size, 1);
            row.Children.Add(size);

            StackPanel badges = new StackPanel();
            badges.Orientation = Orientation.Horizontal;
            badges.VerticalAlignment = VerticalAlignment.Center;
            badges.Margin = new Thickness(12, 0, 0, 0);
            string srcLabel = string.IsNullOrEmpty(g.Source) ? "\u672A\u77E5" : g.Source;
            Border tag = Ui.Tag(srcLabel == "\u684C\u9762" ? "\u684C\u9762" : srcLabel.ToUpperInvariant(), SourceTint(g.Source));
            tag.Margin = new Thickness(0, 0, 6, 0);
            badges.Children.Add(tag);
            Grid.SetColumn(badges, 2);
            row.Children.Add(badges);

            Btn add = new Btn("\u5EFA\u6863", BtnKind.Quiet);
            add.Width = ColAct;
            add.Height = T.BtnH;
            add.Compact = true;
            add.HorizontalAlignment = HorizontalAlignment.Right;
            add.VerticalAlignment = VerticalAlignment.Center;
            add.Click += delegate { ImportOne(c, g, true, true, false); };
            Tip.Attach(add, "\u4EE5\u9ED8\u8BA4\u6218\u672F\u6A21\u677F\u5EFA\u6863\uFF1A\u9501 CCD0 + \u72EC\u663E\u4F18\u5148");
            Grid.SetColumn(add, 3);
            row.Children.Add(add);

            row.MouseEnter += delegate { row.Background = T.S(T.LineFaint); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            return row;
        }

        private void ImportOne(Core c, Go.Daemon.FoundGame g, bool pin, bool gpu, bool timer)
        {
            GameProfile p = new GameProfile();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name = g.Name;
            p.Exe = g.Exe;
            p.PinCcd0 = pin;
            p.CcdMode = pin ? "Ccd0" : "Auto";
            p.PriorityMode = "Keep";
            p.HighGpu = gpu;
            p.Timer = timer;
            p.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            c.Profiles.Add(p);
            c.Engine.Ledger.AppendAudit("PROFILE import " + g.Name + " <- " + g.Source);
            if (c.Shell != null) c.Shell.Toast("\u5DF2\u5EFA\u6863 " + g.Name, true);
            Reload(c);
            if (_lastScanResult != null) ShowDiscover(c, _lastScanResult);
        }

        private void ImportAll(Core c, List<Go.Daemon.FoundGame> list)
        {
            List<GameProfile> batch = new List<GameProfile>();
            foreach (Go.Daemon.FoundGame g in list)
            {
                if (c.Profiles.FindByExe(g.Exe) != null) continue;
                GameProfile p = new GameProfile();
                p.Id = Guid.NewGuid().ToString("N");
                p.Name = g.Name;
                p.Exe = g.Exe;
                p.PinCcd0 = true;
                p.CcdMode = "Ccd0";
                p.PriorityMode = "Keep";
                p.HighGpu = true;
                p.Timer = false;
                p.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                batch.Add(p);
            }
            if (batch.Count > 0) c.Profiles.AddRange(batch);
            int n = batch.Count;
            c.Engine.Ledger.AppendAudit("PROFILE batch-import count=" + n + " src=" + _discoverSource);
            if (c.Shell != null) c.Shell.Toast("\u5DF2\u6279\u91CF\u5EFA\u6863 " + n + " \u4E2A\u6E38\u620F", n > 0);
            Reload(c);
            if (_lastScanResult != null)
            {
                bool anyLeft = false;
                foreach (Go.Daemon.FoundGame g in _lastScanResult.Games)
                    if (c.Profiles.FindByExe(g.Exe) == null) { anyLeft = true; break; }
                if (anyLeft) { _discoverSource = "all"; ShowDiscover(c, _lastScanResult); }
                else if (_discoverHost != null) { _discoverHost.Children.Clear(); _discoverHost.Visibility = Visibility.Collapsed; }
            }
        }

        private static TextBlock Line(string s, uint col)
        {
            return Ui.Tx(s, T.FsMicro, col, T.FCn);
        }
    }
}
