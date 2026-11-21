// ============================================================================
//  Tuning.cs — 02 调优矩阵：分组动作 + 实时回显 + 一键预设 + 逐项还原
//  构图（Rev80 · INSTRUMENT GRADE）：
//    每行三段——左（风险条 / 名称 / 胶囊 / 描述）· 中（现值读数胶囊）· 右（状态 + 开关）。
//    中间那段存在的理由：旧行把"这是什么"和"它开没开"分放在两端，读者要横跨整屏；
//    把 [键名 现值 → 目标值] 放在两者之间，视线只需要走完一列。
//  行本身由 Ui.ActionRow 统一构建与驱动（判读 / 写入 / 失败回滚 / 状态语言只有一份）；
//  本页只保留矩阵特有的东西：分组、预设卡、批量标记（×N）与页头进度胶囊。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Tuning : Page
    {
        public override string Key { get { return "tuning"; } }
        public override string Idx { get { return "02"; } }
        public override string Title { get { return "调优矩阵"; } }
        public override string En { get { return "Tuning Matrix"; } }
        public override string Ico { get { return Glyph.Tune; } }

        private class CardRef
        {
            public string Kind;
            public TextBlock Count;
            public TextBlock Go;
            public Border Fill;
            public int Total;         // 该预设涵盖的总动作数
            public int Pending;       // 该预设覆盖、且尚未应用的动作数
            public int Ready;         // 其中当前权限允许写入的数量
        }

        private class RowRef
        {
            public Ui.ActionRow Row;
            public Border Sep;
        }

        private class GroupRef
        {
            public Border TopGap;
            public UIElement Cap;
            public Border InnerGap;
            public UIElement Panel;
            public TextBlock CountText;
            public List<RowRef> Rows = new List<RowRef>();
        }

        private List<Ui.ActionRow> _rows = new List<Ui.ActionRow>();
        private List<CardRef> _cards = new List<CardRef>();
        private List<GroupRef> _groups = new List<GroupRef>();
        private Ui.SegTabs _tabs;
        private TextBox _searchBox;
        private TextBlock _searchPh;
        private string _searchQuery = "";
        private TextBlock _filterMeta;
        private Border _emptyBox;
        private Border _emptyGap;
        private string _filterKey = "all";
        private TextBlock _statNum;
        private TextBlock _statLdg;
        private Border _statSep;
        private Border _statFill;
        private bool _statTight;
        private bool _active;
        private bool _skipNextEnterRefresh;
        private bool _dirtyRefresh;

        public override UIElement Build(Core c)
        {
            // 页面会被重建（换主题 / 游戏模式 / 命令面板都会 Invalidate 当前页），
            // 而 Page 实例是复用的：不清空就是在旧列表上继续 Add —— 行数翻倍、
            // 页头 "22/22" 变成 "0/44"、每 3 秒的 Probe 次数跟着翻倍，且随重建次数无界增长。
            if (_sync != null) _sync.Stop();
            DetachStickyScroll();
            _rows.Clear();
            _cards.Clear();
            _groups.Clear();
            _statTight = false;
            _lastSig = "";
            _dirtyRefresh = false;
            _skipNextEnterRefresh = true;
            Watch(Dirty.Power | Dirty.Ledger | Dirty.Elevated, OnBus);
            StackPanel page = new StackPanel();
            page.Children.Add(BuildFilterBar());
            page.Children.Add(Gap(14));
            page.Children.Add(BuildPresets(c));
            page.Children.Add(Gap(18));

            // Rev130：主从双栏战术工作台（左：分类调优矩阵 / 右：随滚吸顶战术检视台）
            Grid workbench = new Grid();
            _workbench = workbench;
            workbench.ColumnDefinitions.Add(new ColumnDefinition());
            _inspGapCol = new ColumnDefinition { Width = new GridLength(12) };
            _inspCol = new ColumnDefinition { Width = new GridLength(296) };
            workbench.ColumnDefinitions.Add(_inspGapCol);
            workbench.ColumnDefinitions.Add(_inspCol);
            _inspHidden = false;
            workbench.SizeChanged += delegate { UpdateInspectorSticky(); };

            StackPanel listCol = new StackPanel();
            bool firstGrp = true;
            foreach (string g in c.Engine.Groups())
            {
                Border topGap = Gap(firstGrp ? 0 : 18);
                UIElement cap = Ui.Cap(T.Line, Ui.Wash(T.Accent, 0x66), 26);
                Border innerGap = Gap(10);
                GroupRef gr;
                UIElement panel = BuildGroup(c, g, out gr);
                gr.TopGap = topGap;
                gr.Cap = cap;
                gr.InnerGap = innerGap;
                gr.Panel = panel;
                _groups.Add(gr);

                if (!firstGrp) listCol.Children.Add(topGap);
                listCol.Children.Add(cap);
                listCol.Children.Add(innerGap);
                listCol.Children.Add(panel);
                firstGrp = false;
            }

            _emptyGap = Gap(24);
            _emptyGap.Visibility = Visibility.Collapsed;
            _emptyBox = BuildEmptyState();
            _emptyBox.Visibility = Visibility.Collapsed;
            listCol.Children.Add(_emptyGap);
            listCol.Children.Add(_emptyBox);
            listCol.Children.Add(Gap(18));
            workbench.Children.Add(listCol);

            UIElement insp = BuildInspector(c);
            Grid.SetColumn(insp, 2);
            workbench.Children.Add(insp);
            page.Children.Add(workbench);

            page.SizeChanged += delegate { FitHeader(page.ActualWidth); FitInspector(page.ActualWidth); };
            UIElement header = BuildHeader(c);
            Refresh();
            if (c.Engine.Actions.Count > 0) InspectAction(c.Engine.Actions[0]);
            return Ui.PageRoot(header, page);
        }

        private UIElement BuildFilterBar()
        {
            Grid bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Ui.SegTabItem[] items = new Ui.SegTabItem[]
            {
                new Ui.SegTabItem("all", "全部", "显示所有调优项目"),
                new Ui.SegTabItem("pending", "未应用", "仅显示当前尚未应用或待修复的项目"),
                new Ui.SegTabItem("applied", "已应用", "仅显示系统当前已应用生效的项目"),
                new Ui.SegTabItem("core", "仅核心项", "聚焦游戏延迟、图形链路、核心供电与残留修复项")
            };
            int initIdx = 0;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Key == _filterKey) { initIdx = i; break; }

            _tabs = new Ui.SegTabs(items, initIdx);
            _tabs.Selected += delegate (int idx, string key)
            {
                _filterKey = string.IsNullOrEmpty(key) ? "all" : key;
                ApplyFilter();
            };
            bar.Children.Add(_tabs);

            Border searchWrap = BuildSearchInput();
            Grid.SetColumn(searchWrap, 2);
            bar.Children.Add(searchWrap);

            _filterMeta = Ui.Mono("", T.FsNano, T.Mute);
            _filterMeta.VerticalAlignment = VerticalAlignment.Center;
            _filterMeta.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(_filterMeta, 3);
            bar.Children.Add(_filterMeta);
            return bar;
        }

        private Border BuildEmptyState()
        {
            Btn reset = new Btn("重置筛选与搜索", BtnKind.Ghost);
            reset.Height = T.BtnH;
            reset.Click += delegate
            {
                _filterKey = "all";
                _searchQuery = "";
                if (_searchBox != null && _searchBox.Text.Length > 0) _searchBox.Text = "";
                if (_tabs != null) _tabs.SelectKey("all", false);
                ApplyFilter();
            };
            UIElement inner = Ui.Empty(
                Glyph.Tune,
                "当前筛选分类下暂无匹配项",
                "可以切换顶部的「全部 / 未应用 / 已应用 / 仅核心项」标签，或重置为显示全部。",
                T.AccentDim,
                reset);
            Border wrap = Ui.Panel(inner, 28);
            return wrap;
        }

        // ---- 页头：进度胶囊 ------------------------------------------------

        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            right.Children.Add(BuildStat());

            if (!c.Engine.Elevated)
            {
                Border ro = Chip.Make("只读 · 需管理员", T.Warn, T.ChipHLg);
                ro.Margin = new Thickness(0, 0, 12, 6);
                right.Children.Add(ro);
            }

            HoldBtn revert = new HoldBtn("全部还原", 1100, 0x40FF5C63);
            revert.Confirmed += delegate
            {
                ApplyReport r = c.Engine.RevertAll();
                c.Shell.Toast(r.Ok ? ("已还原 " + r.Applied + " 项") : ("还原有 " + r.Failed + " 项失败"), r.Ok);
                Refresh();
            };
            right.Children.Add(revert);
            return Ui.PageHeader("MODULE / 02", "调优矩阵", "每个开关先读原值、写前落账、写后校验；随时可逐项或整体还原", right);
        }

        // ---- 预设：三张卡（数据只来自现有三个预设的选取规则）-----------------

        /// <summary>
        /// 预设组合 = 一条工具带，不是"三张飘在空白里的卡 + 左边一个洞"。
        /// 旧构图最刺眼的地方不是卡片本身，而是左列只有三行字、没有任何容器：
        /// 三张卡各有边框，第四格什么都没有，读者的第一反应是"这张卡加载失败了"。
        /// 现在四段同处一条带子里，用 1px 竖线分隔：它读作一个控件，而不是四个碎片。
        /// </summary>
        private UIElement BuildPresets(Core c)
        {
            Grid belt = new Grid();
            belt.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(206) });
            for (int i = 0; i < 3; i++)
            {
                belt.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                belt.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            StackPanel head = new StackPanel();
            head.VerticalAlignment = VerticalAlignment.Center;
            head.Margin = new Thickness(2, 2, 10, 2);
            head.Children.Add(Ui.Label("TACTICAL PRESETS", T.Accent));

            TextBlock h = Ui.Tx("一键战术预设", T.FsTitle, T.Hi, T.FCn);
            h.FontWeight = FontWeights.SemiBold;
            h.Margin = new Thickness(0, 6, 0, 0);
            head.Children.Add(h);

            TextBlock note = Ui.Tx("只写尚未应用项 · 原子落账不重复", T.FsMicro, T.Mute, T.FCn);
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 16;
            note.Margin = new Thickness(0, 5, 0, 0);
            head.Children.Add(note);
            belt.Children.Add(head);

            string[] kinds = new string[] { "fix", "latency", "gfx" };
            string[] names = new string[] { "修复第三方残留", "延迟优先", "图形稳定与 VRR" };
            string[] whats = new string[] {
                "将脚本改坏的量子、MMCSS、预取与定时恢复默认",
                "游戏模式、DVR、网络节流、鼠标去加速与 USB 防休眠",
                "HAGS、窗口化翻转优化、VRR、MPO 与定时器协同稳定"
            };
            string[] glyphs = new string[] { Glyph.Shield, Glyph.Bolt, Glyph.Graphics };
            uint[] tints = new uint[] { T.Ok, T.Accent, T.Violet };

            for (int i = 0; i < kinds.Length; i++)
            {
                UIElement seg = PresetSeg(c, kinds[i], names[i], whats[i], glyphs[i], tints[i]);
                Grid.SetColumn(seg, i * 2 + 2);
                belt.Children.Add(seg);
            }
            return Ui.HeroPanel(belt, 16);
        }

        private UIElement PresetSeg(Core c, string kind, string name, string what, string glyph, uint tint)
        {
            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition());

            Grid ico = Ui.IconTile(glyph, 30, tint, Ui.Wash(tint, 0x22), 8);
            ico.VerticalAlignment = VerticalAlignment.Top;
            ico.Margin = new Thickness(0, 1, 10, 0);
            topRow.Children.Add(ico);

            StackPanel top = new StackPanel();
            Grid.SetColumn(top, 1);
            TextBlock nm = Ui.Tx(name, T.FsBody, T.Hi, T.FCn);
            nm.FontWeight = FontWeights.SemiBold;
            top.Children.Add(nm);

            TextBlock d = Ui.Tx(what, T.FsMicro, T.Dim, T.FCn);
            d.Margin = new Thickness(0, 4, 0, 0);
            d.TextWrapping = TextWrapping.Wrap;
            d.LineHeight = 15.5;
            top.Children.Add(d);
            topRow.Children.Add(top);
            body.Children.Add(topRow);

            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(foot, 2);

            TextBlock cnt = Ui.Mono("", T.FsNano, T.Dim);
            cnt.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(cnt);

            TextBlock go = Ui.Tx("套用 →", T.FsNano, tint, T.FCn);
            go.FontWeight = FontWeights.SemiBold;
            go.HorizontalAlignment = HorizontalAlignment.Right;
            go.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(go, 1);
            foot.Children.Add(go);
            body.Children.Add(foot);

            Grid barWrap = new Grid { Height = 4, Margin = new Thickness(0, 7, 0, 0), IsHitTestVisible = false };
            barWrap.Children.Add(new Border { Background = T.S(T.LineSoft), CornerRadius = new CornerRadius(2) });
            Border fill = new Border { Background = T.S(tint), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            barWrap.Children.Add(fill);
            Grid.SetRow(barWrap, 3);
            body.Children.Add(barWrap);

            CardRef cr = new CardRef();
            cr.Kind = kind;
            cr.Count = cnt;
            cr.Go = go;
            cr.Fill = fill;

            barWrap.SizeChanged += delegate { UpdatePresetBar(cr, barWrap.ActualWidth); };

            Border card = Ui.GlassTile(body, 13, tint);
            card.Cursor = Cursors.Hand;
            card.MouseEnter += delegate
            {
                card.Background = T.S(T.Raised);
                card.BorderBrush = T.S(Ui.Wash(tint, 0x88));
                go.Foreground = T.S(T.Hi);
            };
            card.MouseLeave += delegate
            {
                card.Background = T.SubCardBg();
                card.BorderBrush = T.S(Ui.Wash(tint, 0x36));
                if (cr.Pending == 0) go.Foreground = T.S(T.Ok);
                else if (cr.Ready == 0) go.Foreground = T.S(T.Warn);
                else go.Foreground = T.S(T.Accent);
            };
            Pressable.On(card, delegate { RunPreset(c, kind); });

            _cards.Add(cr);
            return card;
        }

        /// <summary>预设的选取规则（与旧按钮内联的判定逐条一致，只是抽出来给卡片计数复用）。</summary>
        private static bool InPreset(ActionDef a, string kind)
        {
            if (kind == "fix") return a.Remediation;
            if (kind == "latency")
                return a.Id == "game-mode" || a.Id == "game-dvr" || a.Id == "maint-off" ||
                       a.Id == "mouse-accel" || a.Id == "net-throttle" || a.Id == "fix-prio" ||
                       a.Id == "fix-mmcss" || a.Id == "usb-suspend" || a.Id == "usb-power-ac";
            if (kind == "gfx")
                return a.Id == "hags" || a.Id == "win-opt-dx" || a.Id == "vrr-opt-dx" ||
                       a.Id == "fix-mpo" || a.Id == "fix-gtr";
            return false;
        }

        /// <summary>该预设覆盖、且当前尚未应用的动作数（只为说明"为什么点不动"）。</summary>
        private static int PendingIn(Core c, string kind)
        {
            int n = 0;
            foreach (ActionDef a in c.Engine.Actions)
            {
                if (!InPreset(a, kind)) continue;
                if (c.Engine.ProbeForUi(a) == AState.Applied) continue;
                n++;
            }
            return n;
        }

        private void RunPreset(Core c, string kind)
        {
            List<ActionDef> sel = new List<ActionDef>();
            foreach (ActionDef a in c.Engine.Actions)
            {
                if (!InPreset(a, kind)) continue;
                if (c.Engine.ProbeForUi(a) == AState.Applied) continue;
                if (!c.Engine.CanWrite(a)) continue;
                sel.Add(a);
            }
            if (sel.Count == 0)
            {
                // 卡片上写着「N 项 · 需管理员」时，点下去不能说「无需变更」——
                // 两种零结果的原因不同，话就得不同。
                int pend = PendingIn(c, kind);
                c.Shell.Toast(pend > 0
                    ? "需要管理员权限：点左下角状态行提权重启"
                    : "该预设当前无需变更", pend == 0);
                return;
            }
            ApplyReport r = c.Engine.ApplyMany(sel);
            c.Shell.Toast(r.Ok ? ("已应用 " + r.Applied + " 项更改") : ("部分失败：" + r.Failed + " 项"), r.Ok);
            Refresh();
        }

        private void PaintCards()
        {
            foreach (CardRef cr in _cards)
            {
                if (cr.Pending == 0)
                {
                    cr.Count.Text = "已全部应用";
                    cr.Count.Foreground = T.S(T.Ok);
                    if (cr.Go != null)
                    {
                        cr.Go.Text = "已就绪 \u2713";
                        cr.Go.Foreground = T.S(T.Ok);
                    }
                }
                else if (cr.Ready == 0)
                {
                    cr.Count.Text = cr.Pending + " 项 · 需管理员";
                    cr.Count.Foreground = T.S(T.Warn);
                    if (cr.Go != null)
                    {
                        cr.Go.Text = "需提权 \u00B7";
                        cr.Go.Foreground = T.S(T.Warn);
                    }
                }
                else
                {
                    cr.Count.Text = cr.Ready + " 项待应用";
                    cr.Count.Foreground = T.S(T.Dim);
                    if (cr.Go != null)
                    {
                        cr.Go.Text = "套用 \u2192";
                        cr.Go.Foreground = T.S(T.Accent);
                    }
                }
                FrameworkElement parent = cr.Fill == null ? null : (cr.Fill.Parent as FrameworkElement);
                UpdatePresetBar(cr, parent == null ? 0 : parent.ActualWidth);
            }
        }

        // ---- 分组 ----------------------------------------------------------

        private UIElement BuildGroup(Core c, string group, out GroupRef gr)
        {
            gr = new GroupRef();
            List<ActionDef> items = c.Engine.GroupOf(group);
            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel hl = new StackPanel();
            hl.Orientation = Orientation.Horizontal;
            TextBlock t = Ui.Tx(group, T.FsTitle, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            hl.Children.Add(t);
            Border cnt = Chip.Make(items.Count + " 项", group == "修复残留" ? T.Warn : T.Dim, T.ChipH);
            cnt.Margin = new Thickness(10, 0, 0, 0);
            gr.CountText = cnt.Child as TextBlock;
            hl.Children.Add(cnt);
            head.Children.Add(hl);

            // 英文 label 退到几乎不可见：它是图纸编号，不是标题。
            TextBlock hint = Ui.Label(GroupHint(group), T.LineHi);
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hint, 1);
            head.Children.Add(hint);
            body.Children.Add(head);

            body.Children.Add(Gap(9));
            body.Children.Add(Ui.Rule(T.Line, 0));

            bool firstRow = true;
            foreach (ActionDef a in items)
            {
                Border sep = null;
                if (!firstRow)
                {
                    sep = Ui.Rule(T.LineSoft, 0);
                    body.Children.Add(sep);
                }
                Ui.ActionRow row;
                body.Children.Add(BuildRow(c, a, out row));
                RowRef rr = new RowRef();
                rr.Row = row;
                rr.Sep = sep;
                gr.Rows.Add(rr);
                firstRow = false;
            }
            return Ui.Panel(body, T.PadPanel);
        }

        private static string GroupHint(string g)
        {
            if (g == "修复残留") return "THIRD-PARTY LEFTOVER";
            if (g == "延迟与调度") return "LATENCY";
            if (g == "图形栈") return "GRAPHICS";
            if (g == "内存与存储") return "MEMORY";
            if (g == "网络") return "NETWORK";
            if (g == "输入与外设") return "INPUT";
            return "SYSTEM";
        }

        // ---- 行：左 / 中 / 右 -------------------------------------------------
        //  行本身住在 Ui.ActionRow 里：判读、写入、失败回滚、状态语言都只有那一份；
        //  本页只决定额外给一条什么信息 —— 批量标记（×N：该动作一次写几个键）。

        private UIElement BuildRow(Core c, ActionDef a, out Ui.ActionRow row)
        {
            Ui.ActionRowSpec spec = new Ui.ActionRowSpec();
            spec.Core = c;
            spec.Def = a;
            spec.Kind = Ui.ActionRowKind.Columns;
            spec.Chip = a.Restart ? "重启生效" : null;
            spec.BatchLabel = a.Ops != null && a.Ops.Length > 1 ? ("×" + a.Ops.Length) : null;
            spec.RowClick = true;
            spec.Committed = delegate { Refresh(); if (_inspected != null) InspectAction(_inspected); };
            row = Ui.Row(spec);
            ActionDef capDef = a;
            row.Root.MouseEnter += delegate { InspectAction(capDef); };
            _rows.Add(row);
            return row.Root;
        }

        /// <summary>窄窗时把「账本」这一段收进胶囊以外，给页头副题留出横向空间。</summary>
        private void FitHeader(double w)
        {
            bool tight = w < 900;
            if (tight == _statTight) return;
            _statTight = tight;
            Visibility v = tight ? Visibility.Collapsed : Visibility.Visible;
            if (_statSep != null) _statSep.Visibility = v;
            if (_statLdg != null) _statLdg.Visibility = v;
        }

        // ---- 状态回显 --------------------------------------------------------

        private void Refresh()
        {
            if (C == null) return;
            int applied = 0, total = 0, pending = C.Engine.PendingCount;
            System.Text.StringBuilder sig = new System.Text.StringBuilder();
            foreach (CardRef cr in _cards) { cr.Total = 0; cr.Pending = 0; cr.Ready = 0; }

            foreach (Ui.ActionRow r in _rows)
            {
                total++;
                string cur0;
                AState st = C.Engine.ProbeForUi(r.Def, out cur0);
                bool can = C.Engine.CanWrite(r.Def);
                sig.Append((int)st).Append(':').Append(cur0 ?? "").Append(',');
                // 行内的开关位置 / 状态文字 / 读数胶囊全部由这一处读数决定。
                r.Paint(st, can, cur0);
                if (st == AState.Applied) applied++;

                foreach (CardRef cr in _cards)
                {
                    if (!InPreset(r.Def, cr.Kind)) continue;
                    cr.Total++;
                    if (st == AState.Applied) continue;
                    cr.Pending++;
                    if (can) cr.Ready++;
                }
            }

            PaintCards();

            if (_statNum != null)
            {
                _statNum.Text = applied + " / " + total;
                _statLdg.Text = "账本 " + pending + " 项";
            }
            if (_statFill != null)
                _statFill.Height = total <= 0 ? 0 : Math.Round(16.0 * applied / total);

            // 旧实现在这里又 Probe 了整整一遍（每行一次注册表打开）来算签名；
            // 现在直接复用上面已经拿到的状态，刷新成本从 3N 次探针降到 2N。
            sig.Append(pending);
            _lastSig = sig.ToString();
            ApplyFilter();
        }

        private static bool IsCoreAction(ActionDef a)
        {
            if (a == null) return false;
            if (a.Remediation) return true;
            string id = a.Id;
            return id == "game-mode" || id == "game-dvr" || id == "maint-off" ||
                   id == "prio-boost" || id == "cpu-core-park" || id == "cpu-min-state" ||
                   id == "hags" || id == "win-opt-dx" || id == "vrr-opt-dx" ||
                   id == "fse-on" || id == "pcie-aspm" || id == "disk-idle-off" ||
                   id == "net-throttle" || id == "mouse-accel" || id == "usb-suspend" ||
                   id == "usb-power-ac" || id == "power-throttle-off";
        }

        private bool MatchFilter(ActionDef a, AState st)
        {
            if (_filterKey == "pending" && st == AState.Applied) return false;
            if (_filterKey == "applied" && st != AState.Applied) return false;
            if (_filterKey == "core" && !IsCoreAction(a)) return false;
            return MatchSearch(a);
        }

        private void ApplyFilter()
        {
            if (_groups.Count == 0) return;
            int shownTotal = 0;
            int allTotal = _rows.Count;
            for (int gi = 0; gi < _groups.Count; gi++)
            {
                GroupRef gr = _groups[gi];
                int vis = 0;
                bool first = true;
                for (int ri = 0; ri < gr.Rows.Count; ri++)
                {
                    RowRef rr = gr.Rows[ri];
                    bool show = MatchFilter(rr.Row.Def, rr.Row.State);
                    rr.Row.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (rr.Sep != null) rr.Sep.Visibility = (show && !first) ? Visibility.Visible : Visibility.Collapsed;
                    if (show)
                    {
                        first = false;
                        vis++;
                        shownTotal++;
                    }
                }
                Visibility gv = vis > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (gr.TopGap != null) gr.TopGap.Visibility = gv;
                if (gr.Cap != null) gr.Cap.Visibility = gv;
                if (gr.InnerGap != null) gr.InnerGap.Visibility = gv;
                if (gr.Panel != null) gr.Panel.Visibility = gv;
                if (gr.CountText != null)
                {
                    gr.CountText.Text = vis == gr.Rows.Count
                        ? (gr.Rows.Count + " 项")
                        : (vis + "/" + gr.Rows.Count + " 项");
                }
            }
            if (_filterMeta != null)
            {
                _filterMeta.Text = shownTotal == allTotal
                    ? ("共 " + allTotal + " 项")
                    : ("筛选显示 " + shownTotal + " / " + allTotal + " 项");
            }
            Visibility ev = shownTotal == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_emptyGap != null) _emptyGap.Visibility = ev;
            if (_emptyBox != null) _emptyBox.Visibility = ev;
            SyncInspectedToVisible();
        }

        private DispatcherTimer _sync;
        private string _lastSig = "";

        private void OnBus(Dirty d)
        {
            if (C == null || _rows.Count == 0) return;
            if (_active) Refresh();
            else _dirtyRefresh = true;
        }

        public override void OnEnter()
        {
            _active = true;
            AttachStickyScroll();
            UpdateInspectorSticky();
            if (_skipNextEnterRefresh)
            {
                _skipNextEnterRefresh = false;
                if (_dirtyRefresh)
                {
                    _dirtyRefresh = false;
                    Refresh();
                }
            }
            else
            {
                _dirtyRefresh = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (_active) Refresh();
                }));
                if (C != null && C.Engine != null) C.Engine.RefreshPowerAcCacheAsync(null);
            }
            if (_sync == null)
            {
                _sync = new DispatcherTimer();
                _sync.Interval = TimeSpan.FromSeconds(3);
                _sync.Tick += delegate { SoftSync(); };
            }
            _sync.Start();
        }

        public override void OnLeave()
        {
            _active = false;
            if (_sync != null) _sync.Stop();
        }

        /// <summary>周期性复判：只有真实状态发生变化时才重绘，避免开关闪烁。</summary>
        private void SoftSync()
        {
            if (C == null) return;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Ui.ActionRow r in _rows)
            {
                string cur0;
                AState st = C.Engine.ProbeForUi(r.Def, out cur0);
                sb.Append((int)st).Append(':').Append(cur0 ?? "").Append(',');
            }
            sb.Append(C.Engine.PendingCount);
            string sig = sb.ToString();
            if (sig == _lastSig) return;
            _lastSig = sig;
            Refresh();
        }
    }
}