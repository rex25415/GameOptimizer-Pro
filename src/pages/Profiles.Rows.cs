// ============================================================================
//  Profiles.Rows.cs — 08 战术档案：行网格 / 抽屉 / 策略模板（Profiles 分片）
//
//  为什么切：档案行、它的三列内容与展开后的策略抽屉是一套版面的四个部分——
//  列宽、行高、点到哪一列该做什么，改动必然同时落在四者上；留在主文件里会把
//  页面装配（页头 / 守护条 / 写档案）淹掉。主文件因此保持在 500 行以内。
//
//  列宽与 03 调度页的审计行同一套数（110 / 92 / 56，DESIGN-86 §3）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Profiles : Page
    {
        // ====================================================================
        //  Rev130 游戏战术卡牌墙 (2 列 Bento Game Library Cards)
        // ====================================================================
        private UIElement BuildList(Core c)
        {
            StackPanel body = new StackPanel();
            _listMeta = Ui.Mono("—", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("游戏战术库", "GAME LIBRARY · TACTICAL LOADOUT CARDS", _listMeta));
            body.Children.Add(Gap(12));

            _list = new Reflow(12, 1, 1, 2, 2);
            body.Children.Add(_list);

            Btn find = new Btn("扫描已安装游戏", BtnKind.Primary);
            find.SetIcon(Glyph.Search, 12);
            find.Click += delegate { RunDiscover(c); };

            Grid emptyHost = new Grid();
            emptyHost.MinHeight = 200;
            emptyHost.Children.Add(Ui.Empty(Glyph.Profiles,
                "尚未建立任何战术档案",
                "档案是一个 exe 级的策略包：锁哪个 CCD、用哪块显卡、要不要 0.5ms 精确定时。\r\n建好之后，守护引擎会在它启动的瞬间自动挂载，退出时按账本复原。",
                T.Accent, find));

            _emptyBox = new StackPanel();
            _emptyBox.Visibility = Visibility.Collapsed;
            _emptyBox.Children.Add(emptyHost);
            body.Children.Add(_emptyBox);

            return Ui.Panel(body, T.PadPanel);
        }

        private void Reload(Core c)
        {
            if (_list == null) return;
            _rows.Clear();
            _list.Clear();
            RefreshStat();

            GameProfile[] items = Store.Snapshot();
            bool none = items.Length == 0;
            if (_emptyBox != null) _emptyBox.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            _list.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            if (none) { _selectedId = ""; return; }

            for (int i = 0; i < items.Length; i++)
            {
                PRow row = BuildProfileRow(c, items[i]);
                _rows.Add(row);
                _list.Add(row.Host);
            }
            if (_selectedId.Length > 0) Select(_selectedId);
        }

        private PRow BuildProfileRow(Core c, GameProfile p)
        {
            PRow row = new PRow();
            row.P = p;
            row.Host = new StackPanel();

            uint riskTint = RiskOf(p);
            StackPanel cardBody = new StackPanel();

            // 顶部：游戏圆章 + 标题/路径 + 命中读数
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border medal = new Border();
            medal.Width = 36; medal.Height = 36;
            medal.CornerRadius = new CornerRadius(9);
            medal.Background = T.Grad(T.AccentA(0x28), T.Accent2A(0x14), 135);
            medal.BorderBrush = T.S(T.AccentA(0x64));
            medal.BorderThickness = new Thickness(1);
            medal.VerticalAlignment = VerticalAlignment.Center;
            medal.Margin = new Thickness(0, 0, 12, 0);
            medal.Child = Ui.Icon(Glyph.Profiles, 16, T.Accent, 1.5);
            top.Children.Add(medal);

            StackPanel mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock nm = Ui.Tx(p.Name, T.FsTitle, T.Hi, T.FCn);
            nm.FontWeight = FontWeights.SemiBold;
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            nm.TextWrapping = TextWrapping.NoWrap;
            mid.Children.Add(nm);

            row.Desc = Ui.Mono(DescText(p), T.FsNano, T.Mute);
            row.Desc.Margin = new Thickness(0, 3, 0, 0);
            row.Desc.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Desc.TextWrapping = TextWrapping.NoWrap;
            mid.Children.Add(row.Desc);
            Grid.SetColumn(mid, 1);
            top.Children.Add(mid);

            row.Hits = Ui.Mono(HitText(p), T.FsNano, HitTint(p));
            row.Hits.HorizontalAlignment = HorizontalAlignment.Right;
            row.Hits.VerticalAlignment = VerticalAlignment.Top;
            row.Hits.Margin = new Thickness(8, 2, 0, 0);
            Grid.SetColumn(row.Hits, 2);
            top.Children.Add(row.Hits);
            cardBody.Children.Add(top);

            cardBody.Children.Add(Gap(10));
            cardBody.Children.Add(Ui.Rule(T.LineSoft, 0));
            cardBody.Children.Add(Gap(10));

            // 底部：策略徽标群 + 展开提示 + 立即应用按钮
            Grid bot = new Grid();
            bot.ColumnDefinitions.Add(new ColumnDefinition());
            bot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Badges = new StackPanel();
            row.Badges.Orientation = Orientation.Horizontal;
            row.Badges.VerticalAlignment = VerticalAlignment.Center;
            AddPolicyBadges(row.Badges, p);
            TextBlock editHint = Ui.Mono("  · 点卡编辑策略", T.FsNano, T.Mute);
            editHint.VerticalAlignment = VerticalAlignment.Center;
            row.Badges.Children.Add(editHint);
            bot.Children.Add(row.Badges);

            Btn apply = new Btn("应用策略", BtnKind.Accent);
            apply.Height = T.BtnHSm;
            apply.Compact = true;
            apply.HorizontalAlignment = HorizontalAlignment.Right;
            apply.VerticalAlignment = VerticalAlignment.Center;
            apply.Click += delegate { ApplyRow(c, row); };
            Tip.Attach(apply, "对这个档案里的 exe 应用策略：写 GPU 首选项（走写前账本）+ 对运行中的进程设亲和性。");
            Grid.SetColumn(apply, 1);
            bot.Children.Add(apply);
            cardBody.Children.Add(bot);

            // 左侧状态光条（兼容 RefreshRow 对 row.Bar.Fill 的动态刷新）
            Grid frame = new Grid();
            frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            frame.ColumnDefinitions.Add(new ColumnDefinition());
            System.Windows.Shapes.Rectangle bar = new System.Windows.Shapes.Rectangle();
            bar.Fill = T.S(riskTint);
            bar.RadiusX = 1.5; bar.RadiusY = 1.5;
            bar.Margin = new Thickness(0, 2, 0, 2);
            frame.Children.Add(bar);
            cardBody.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(cardBody, 1);
            frame.Children.Add(cardBody);

            row.Shell = Ui.GlassTile(frame, 14, T.Accent);
            row.Shell.Cursor = Cursors.Hand;
            row.Bar = bar;
            row.Shell.MouseEnter += delegate
            {
                if (_selectedId == p.Id) return;
                row.Shell.BorderBrush = T.S(T.AccentA(0x78));
            };
            row.Shell.MouseLeave += delegate
            {
                if (_selectedId == p.Id) return;
                row.Shell.BorderBrush = T.S(T.LineSoft);
            };
            Pressable.On(row.Shell, delegate { ToggleRow(row); });
            Tip.Attach(row.Shell, RowTipText(p));

            row.Editor = BuildEditor(c, row);
            row.Editor.Margin = new Thickness(0, 6, 0, 0);
            row.Editor.CornerRadius = new CornerRadius(T.RadiusMd);
            row.Editor.BorderThickness = new Thickness(1);
            row.Editor.Visibility = Visibility.Collapsed;

            row.Host.Children.Add(row.Shell);
            row.Host.Children.Add(row.Editor);
            return row;
        }

        /// <summary>抽屉：拓扑亲和 + 优先级 + 开关策略 + 移除（移除需二次确认）。</summary>
        private Border BuildEditor(Core c, PRow row)
        {
            Border b = new Border();
            b.Background = T.S(T.Inset);
            b.BorderThickness = new Thickness(0, 0, 0, 1);
            b.BorderBrush = T.S(T.LineSoft);
            b.Padding = new Thickness(16, 10, 16, 12);

            StackPanel wrap = new StackPanel();

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel toggles = new StackPanel();
            toggles.Orientation = Orientation.Horizontal;

            GameProfile p = row.P;
            toggles.Children.Add(Toggle(row, "独显优先", p.HighGpu,
                "写 HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences 的 GpuPreference=2（写前账本，可在还原中心撤销）。",
                delegate (GameProfile gp, bool v) { gp.HighGpu = v; }));
            toggles.Children.Add(Toggle(row, "本会话 0.5ms", p.Timer,
                "向 TimerService 申请 0.5 ms 定时器租约（引用计数），仅在挂载期间生效。",
                delegate (GameProfile gp, bool v) { gp.Timer = v; }));
            toggles.Children.Add(Toggle(row, "后台降效", p.EcoBackground,
                "守护挂载期间将明显抢占 CPU 的后台进程降为 EcoQoS 能效模式，跳过系统关键与游戏进程。",
                delegate (GameProfile gp, bool v) { gp.EcoBackground = v; }));
            top.Children.Add(toggles);

            Btn del = new Btn("移除档案", BtnKind.Quiet);
            del.Height = T.BtnHSm;
            del.Compact = true;
            del.HorizontalAlignment = HorizontalAlignment.Right;
            del.VerticalAlignment = VerticalAlignment.Center;
            Tip.Attach(del, "从档案库移除（需再点一次确认）。移除不会回滚已经应用到系统上的策略——那是还原中心的职责。");

            bool armed = false;
            DispatcherTimer disarm = null;
            Action reset = delegate
            {
                armed = false;
                del.Label = "移除档案";
                if (disarm != null) disarm.Stop();
            };
            del.Click += delegate
            {
                if (!armed)
                {
                    armed = true;
                    del.Label = "确认移除";
                    if (disarm == null)
                    {
                        disarm = new DispatcherTimer();
                        disarm.Interval = TimeSpan.FromSeconds(3);
                        disarm.Tick += delegate { reset(); };
                    }
                    disarm.Stop();
                    disarm.Start();
                    return;
                }
                reset();
                GameProfile cur = row.P;
                if (cur == null) return;
                Store.Remove(cur.Id);
                if (c.Shell != null) c.Shell.Toast("已移除档案 " + cur.Name, true);
                _selectedId = "";
                Reload(c);
            };
            Grid.SetColumn(del, 1);
            top.Children.Add(del);
            wrap.Children.Add(top);

            StackPanel segRow = new StackPanel();
            segRow.Orientation = Orientation.Horizontal;
            segRow.Margin = new Thickness(0, 10, 0, 0);

            Ui.SegTabItem[] ccdItems = new Ui.SegTabItem[]
            {
                new Ui.SegTabItem("Auto", "自动", "由系统自由调度，不绑定处理器亲和性"),
                new Ui.SegTabItem("Ccd0", "CCD0", "锁定到第一个 CCD 逻辑核心（跨簇敏感推荐）"),
                new Ui.SegTabItem("Ccd1", "CCD1", "锁定到第二个 CCD 逻辑核心（需多 CCD 拓扑）"),
                new Ui.SegTabItem("Physical", "物理核", "仅绑定每个物理核心主线程（跳过超线程）"),
                new Ui.SegTabItem("All", "全核", "显式恢复并锁定全部可用逻辑处理器")
            };
            string curCcd = EffectiveCcdMode(p);
            Ui.SegTabs ccdTabs = new Ui.SegTabs(ccdItems, IndexOfKey(ccdItems, curCcd));
            ccdTabs.Changed += delegate
            {
                string key = ccdTabs.SelectedKey;
                UpdateRowField(row, delegate (GameProfile gp)
                {
                    gp.CcdMode = key;
                    gp.PinCcd0 = (key == "Ccd0");
                });
            };
            segRow.Children.Add(SegGroup("核心拓扑", ccdTabs, 20));

            Ui.SegTabItem[] prioItems = new Ui.SegTabItem[]
            {
                new Ui.SegTabItem("Keep", "保持", "保持进程原本优先级"),
                new Ui.SegTabItem("AboveNormal", "高于", "设为高于标准（AboveNormal）"),
                new Ui.SegTabItem("High", "高", "设为高优先级（High）")
            };
            string curPrio = EffectivePriorityMode(p);
            Ui.SegTabs prioTabs = new Ui.SegTabs(prioItems, IndexOfKey(prioItems, curPrio));
            prioTabs.Changed += delegate
            {
                string key = prioTabs.SelectedKey;
                UpdateRowField(row, delegate (GameProfile gp) { gp.PriorityMode = key; });
            };
            segRow.Children.Add(SegGroup("优先级", prioTabs, 0));

            wrap.Children.Add(segRow);
            b.Child = wrap;
            return b;
        }

        private static int IndexOfKey(Ui.SegTabItem[] items, string key)
        {
            for (int i = 0; i < items.Length; i++)
                if (string.Equals(items[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        private static UIElement SegGroup(string label, Ui.SegTabs tabs, double rightMargin)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Margin = new Thickness(0, 0, rightMargin, 0);
            TextBlock t = Ui.Tx(label, T.FsSmall, T.Dim, T.FCn);
            t.Margin = new Thickness(0, 0, 8, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            sp.Children.Add(tabs);
            return sp;
        }

        private void UpdateRowField(PRow row, Action<GameProfile> mutator)
        {
            if (row == null || row.P == null || mutator == null) return;
            string id = row.P.Id;
            bool ok = Store.UpdateProfile(id, mutator, true);
            GameProfile latest = Store.FindById(id);
            if (latest != null) row.P = latest;
            if (!ok && C != null && C.Shell != null)
                C.Shell.Toast("档案保存失败：本次修改只在内存里", false);
            RefreshRow(row);
        }

        private UIElement Toggle(PRow row, string label, bool value, string tip, Action<GameProfile, bool> set)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.Margin = new Thickness(0, 0, 22, 0);
            sp.VerticalAlignment = VerticalAlignment.Center;

            TextBlock t = Ui.Tx(label, T.FsSmall, T.Text, T.FCn);
            t.Margin = new Thickness(0, 0, 10, 0);
            t.IsHitTestVisible = false;
            sp.Children.Add(t);

            Switch sw = new Switch(value);
            sw.VerticalAlignment = VerticalAlignment.Center;
            Tip.Attach(sw, tip);
            sw.Changed += delegate
            {
                bool on = sw.On;
                UpdateRowField(row, delegate (GameProfile gp) { set(gp, on); });
            };
            sp.Children.Add(sw);
            return sp;
        }

        /// <summary>展开 / 收起抽屉。同一次只展开一行——两行都开着时读者分不清开关属于谁。</summary>
        private void ToggleRow(PRow row)
        {
            bool open = _selectedId != row.P.Id;
            Select(open ? row.P.Id : "");
        }

        private void Select(string id)
        {
            _selectedId = id;
            for (int i = 0; i < _rows.Count; i++)
            {
                PRow r = _rows[i];
                bool on = r.P.Id == id;
                if (r.Editor != null) r.Editor.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (r.Shell != null)
                {
                    r.Shell.Background = on ? (Brush)T.S(T.Raised) : T.SubCardBg();
                    r.Shell.BorderBrush = T.S(on ? T.AccentA(0x88) : T.LineSoft);
                }
            }
        }

        private void RefreshRows()
        {
            for (int i = 0; i < _rows.Count; i++) RefreshRow(_rows[i]);
        }

        /// <summary>只重画一行：徽标 / 说明 / 命中 / 行首风险色。开关的点击不重建整行。</summary>
        private void RefreshRow(PRow row)
        {
            if (row == null) return;
            if (row.P != null && C != null && Store != null && !string.IsNullOrEmpty(row.P.Id))
            {
                GameProfile fresh = Store.FindById(row.P.Id);
                if (fresh != null) row.P = fresh;
            }
            if (row.Badges != null)
            {
                row.Badges.Children.Clear();
                AddPolicyBadges(row.Badges, row.P);
                TextBlock editHint = Ui.Mono("  · 点卡编辑策略", T.FsNano, T.Mute);
                editHint.VerticalAlignment = VerticalAlignment.Center;
                row.Badges.Children.Add(editHint);
            }
            if (row.Desc != null) SetText(row.Desc, DescText(row.P));
            if (row.Hits != null)
            {
                SetText(row.Hits, HitText(row.P));
                row.Hits.Foreground = T.S(HitTint(row.P));
            }
            if (row.Bar != null) row.Bar.Fill = T.S(RiskOf(row.P));
            if (row.Shell != null) Tip.Attach(row.Shell, RowTipText(row.P));
        }

        private void ApplyRow(Core c, PRow row)
        {
            if (row == null || row.P == null) return;
            GameProfile latest = !string.IsNullOrEmpty(row.P.Id) ? Store.FindById(row.P.Id) : null;
            if (latest != null) row.P = latest;
            ApplyProfile(c, row.P);
            int n;
            _hits.TryGetValue(row.P.Id, out n);
            _hits[row.P.Id] = n + 1;
            _lastApply[row.P.Id] = DateTime.Now;
            RefreshRow(row);
        }

        // ---- 行的三列各自说什么 ----------------------------------------------
        private static Grid RowGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColVal) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTag) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColAct) });
            return g;
        }

        /// <summary>Ui.RowShell 的结论色条是内层 Grid 的第一个子元素；取不到就保留构建时的颜色。</summary>
        private static System.Windows.Shapes.Rectangle BarOf(Border shell)
        {
            Grid g = shell.Child as Grid;
            if (g == null || g.Children.Count == 0) return null;
            return g.Children[0] as System.Windows.Shapes.Rectangle;
        }

        /// <summary>
        /// 行首色：这个档案此刻会不会真的被挂载。
        /// 没配策略 = 需要看；守护关着 = 档案存在但不会触发（也是需要看）；其余是低亮度绿。
        /// </summary>
        private uint RiskOf(GameProfile p)
        {
            bool any = EffectiveCcdMode(p) != "Auto" || p.HighGpu || p.Timer
                || EffectivePriorityMode(p) != "Keep" || p.EcoBackground;
            if (!any) return T.Warn;
            if (!GuardOn()) return T.Warn;
            return Ui.Wash(T.Ok, 0x55);
        }

        private bool GuardOn()
        {
            Go.Daemon.DaemonHost d = C == null ? null : C.Daemon;
            if (d == null) return false;
            return d.Watcher.Enabled || d.Watcher.Active != null;
        }

        private string DescText(GameProfile p)
        {
            string s = Ui.ShortPath(p.Exe);
            if (p.LastP99Us > 0)
            {
                s += "  ·  P99 " + p.LastP99Us.ToString("0") + " µs";
                if (!string.IsNullOrEmpty(p.LastVerdict)) s += " · " + p.LastVerdict;
            }
            else if (!string.IsNullOrEmpty(p.LastVerdict))
            {
                s += "  ·  " + p.LastVerdict;
            }

            List<string> extra = OverflowPolicies(p);
            for (int i = 0; i < extra.Count; i++) s += "  ·  " + extra[i];
            return s;
        }

        private static List<string> OverflowPolicies(GameProfile p)
        {
            List<string> all = new List<string>(5);
            string ccd = EffectiveCcdMode(p);
            if (ccd != "Auto") all.Add(CcdLabel(ccd));
            string prio = EffectivePriorityMode(p);
            if (prio != "Keep") all.Add(PriorityTag(prio));
            if (p.HighGpu) all.Add("独显");
            if (p.Timer) all.Add("0.5 ms");
            if (p.EcoBackground) all.Add("后台降效");
            if (all.Count <= 2) all.Clear();
            else all.RemoveRange(0, 2);
            return all;
        }

        private static void AddPolicyBadges(StackPanel host, GameProfile p)
        {
            int n = 0;
            string ccd = EffectiveCcdMode(p);
            if (ccd != "Auto") { host.Children.Add(Tag(CcdLabel(ccd), T.Accent)); n++; }
            string prio = EffectivePriorityMode(p);
            if (prio != "Keep" && n < 2) { host.Children.Add(Tag(PriorityTag(prio), T.SeriesB)); n++; }
            if (p.HighGpu && n < 2) { host.Children.Add(Tag("独显", T.Text)); n++; }
            if (p.Timer && n < 2) { host.Children.Add(Tag("0.5ms", T.Dim)); n++; }
            if (p.EcoBackground && n < 2) { host.Children.Add(Tag("Eco", T.Ok)); n++; }
            if (n == 0) host.Children.Add(Ui.Tag("未配置", T.Mute));
        }

        private static string RowTipText(GameProfile p)
        {
            string tip = "点击展开策略抽屉：" + BuildGuardPolicyText(p);
            if (p.Hits > 0 || p.TotalPlaySec > 0 || p.LastP99Us > 0)
            {
                tip += "\r\n累计命中 " + p.Hits + " 次";
                string dur = FormatDurationShort(p.TotalPlaySec);
                if (dur.Length > 0) tip += " · 时长 " + dur;
                if (p.LastP99Us > 0) tip += " · 最近 P99 " + p.LastP99Us.ToString("0") + " µs";
                if (!string.IsNullOrEmpty(p.LastVerdict)) tip += "\r\n" + p.LastVerdict;
            }
            return tip;
        }

        private static Border Tag(string text, uint tint)
        {
            Border b = Ui.Tag(text, tint);
            b.Margin = new Thickness(0, 0, 6, 0);
            return b;
        }

        // ---- 列 2 的三种说法 --------------------------------------------------
        private string HitText(GameProfile p)
        {
            if (IsActive(p)) return "挂载中";
            int n;
            _hits.TryGetValue(p.Id, out n);
            int total = p.Hits + n;
            string dur = FormatDurationShort(p.TotalPlaySec);
            if (p.Hits > 0 && dur.Length > 0) return "×" + total + " · " + dur;
            DateTime at;
            if (_lastApply.TryGetValue(p.Id, out at)) return "×" + total + " · " + at.ToString("HH:mm");
            if (p.Hits > 0 && !string.IsNullOrEmpty(p.LastUsed) && p.LastUsed.Length >= 16)
                return "×" + total + " · " + p.LastUsed.Substring(5, 5);
            return total > 0 ? ("×" + total) : "—";
        }

        private uint HitTint(GameProfile p)
        {
            if (IsActive(p)) return T.Accent;
            int n;
            _hits.TryGetValue(p.Id, out n);
            return (p.Hits + n) > 0 ? T.Dim : T.Mute;
        }

        private bool IsActive(GameProfile p)
        {
            Go.Daemon.DaemonHost d = C == null ? null : C.Daemon;
            if (d == null) return false;
            GameProfile a = d.Watcher.Active;
            return a != null && a.Id == p.Id;
        }

        private static void SetText(TextBlock t, string s)
        {
            if (t != null && t.Text != s) t.Text = s;
        }

        // ====================================================================
        //  策略模板（Rev120 HYPERION：三列立体玻璃战术模板卡）
        // ====================================================================
        private UIElement BuildTemplates(Core c)
        {
            StackPanel body = new StackPanel();
            body.Children.Add(Ui.PanelHead("策略模板", "TACTICAL TEMPLATES", null));
            body.Children.Add(Gap(12));

            Grid cards = new Grid();
            for (int i = 0; i < 3; i++)
                cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UIElement c0 = TemplateCard(c, "竞技射击", "锁 CCD0 + 0.5ms 精确定时，压榨输入延迟", Glyph.Target, T.Accent, true, false, true);
            UIElement c1 = TemplateCard(c, "3A 单机", "锁 CCD0 + 独显优先，稳定高帧生成时间", Glyph.Graphics, T.Violet, true, true, false);
            UIElement c2 = TemplateCard(c, "混合显卡通用", "强制直连独显优先，保持自由核心调度", Glyph.Bolt, T.Ok, false, true, false);

            ((FrameworkElement)c1).Margin = new Thickness(12, 0, 0, 0);
            ((FrameworkElement)c2).Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(c0, 0);
            Grid.SetColumn(c1, 1);
            Grid.SetColumn(c2, 2);
            cards.Children.Add(c0);
            cards.Children.Add(c1);
            cards.Children.Add(c2);
            body.Children.Add(cards);
            return Ui.Panel(body, T.PadPanel);
        }

        private UIElement TemplateCard(Core c, string name, string desc, string glyph, uint tint, bool pin, bool gpu, bool timer)
        {
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            Grid ico = Ui.IconTile(glyph, 32, tint, Ui.Wash(tint, 0x22), 8);
            ico.Margin = new Thickness(0, 0, 10, 0);
            top.Children.Add(ico);

            StackPanel titleWrap = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titleWrap, 1);
            TextBlock t = Ui.Tx(name, T.FsBody, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            titleWrap.Children.Add(t);
            int count = (pin ? 1 : 0) + (gpu ? 1 : 0) + (timer ? 1 : 0);
            TextBlock sub = Ui.Mono(count + " 项策略预设", T.FsNano, tint);
            sub.Margin = new Thickness(0, 2, 0, 0);
            titleWrap.Children.Add(sub);
            top.Children.Add(titleWrap);
            g.Children.Add(top);

            TextBlock d = Ui.Tx(desc, T.FsMicro, T.Dim, T.FCn);
            d.TextWrapping = TextWrapping.Wrap;
            d.LineHeight = 16;
            d.Margin = new Thickness(0, 10, 0, 12);
            Grid.SetRow(d, 1);
            g.Children.Add(d);

            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(foot, 2);

            StackPanel badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (pin) badges.Children.Add(Tag("CCD0", T.Accent));
            if (gpu) badges.Children.Add(Tag("独显", T.Violet));
            if (timer) badges.Children.Add(Tag("0.5ms", T.Ok));
            foot.Children.Add(badges);

            Btn use = new Btn("建立 →", BtnKind.Accent);
            use.Height = T.BtnHSm;
            use.Compact = true;
            use.Click += delegate { AddGameWith(c, pin, gpu, timer); };
            Grid.SetColumn(use, 1);
            foot.Children.Add(use);
            g.Children.Add(foot);

            Border card = Ui.GlassTile(g, 14, tint);
            card.MouseEnter += delegate { card.BorderBrush = T.S(Ui.Wash(tint, 0x7A)); };
            card.MouseLeave += delegate { card.BorderBrush = T.S(Ui.Wash(tint, 0x36)); };
            return card;
        }

    }
}
