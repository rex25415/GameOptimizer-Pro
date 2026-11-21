// ============================================================================
//  Restore.cs — 10 还原中心：账本时间轴 / 逐条还原 / 一键回到原始状态
//
//  Rev86：这一页以前是"一条竖向时间线，行里塞了时间、动作、变化、重试次数、按钮"，
//  每行自己算 x，四种信息挤在同一段里没有列对齐。读者要判断"这一条该不该还原"，
//  眼睛得在行内来回找。现在改成【时间线 + 行网格】：
//
//    列 0  2px 风险色条（Ui.RowShell 提供，A=Ok / B=Warn / C=Bad）
//    列 1  76   时间 HH:mm:ss
//    列 2  20   时间线轨道（细线 + 5px 节点，节点颜色 = 风险）
//    列 3  自适应  动作名（FsBody）+ 种类 · 键路径（FsNano）
//    列 4  110  原值 → 新值（等宽，右对齐）
//    列 5  92   徽标组：风险字母 + 重复次数（最多 2 枚）
//    列 6  56   「还原」按钮（Btn 56×28，右对齐）
//
//  行高 54（含两行文字），列宽全部是 4 的倍数，和基准测试页的历史表用同一套数字。
//  时间线保留在列 2——它回答"我什么时候动了什么"，这个语义不能因为加了表格就丢掉。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Go.Pages
{
    public class Restore : Page
    {
        public override string Key { get { return "restore"; } }
        public override string Idx { get { return "10"; } }
        public override string Title { get { return "\u8FD8\u539F\u4E2D\u5FC3"; } }
        public override string En { get { return "Restore"; } }
        public override string Ico { get { return Glyph.Restore; } }

        // ---- 行网格：表头与行共用这一套（一页只许有一套列宽）---------------
        private const double ColTime = 76;
        private const double ColRail = 20;
        private const double ColChange = 156;
        private const double ColBadge = 92;
        private const double ColCtrl = 104;
        /// <summary>行高 54：行里有两行文字（动作名 + 键路径），对应 DESIGN-86 §1.2。</summary>
        private const double RowH = 54;
        /// <summary>RowShell 恒定让出 2px 色条 + 14px 边距，表头必须让出同样的 16px。</summary>
        private const double ShellInset = 16;

        private StackPanel _list, _mid;
        private TextBlock _path;
        private TextBlock _kRecords, _kActions, _kLast;
        private TextBlock _termText, _filterInfo;
        private Grid _term;
        private Ui.SegTabs _tabs;
        private Core _c;
        private string _filter = "all";

        public override UIElement Build(Core c)
        {
            _c = c;
            // Rev130：顶部独立安全金库 Hero 舞台 + 下方写前账本差异时间轴面板
            StackPanel page = new StackPanel();

            Grid vaultStage = new Grid();
            vaultStage.Children.Add(Ui.HeroPanel(BuildVaultHero(), T.PadPanel));
            vaultStage.Children.Add(Ui.CornerTicks(T.AccentA(0x80), 10, 1.5));
            page.Children.Add(vaultStage);
            page.Children.Add(Gap(14));

            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 分类筛选
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 列头
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 时间轴
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 地脚

            Grid filterBar = BuildFilterBar();
            body.Children.Add(filterBar);

            Border headRow = BuildColumnHead();
            headRow.Margin = new Thickness(0, 12, 0, 6);
            Grid.SetRow(headRow, 1);
            body.Children.Add(headRow);

            Grid listHost = new Grid();
            listHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            listHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _list = new StackPanel();
            listHost.Children.Add(_list);

            _mid = new StackPanel();
            Grid.SetRow(_mid, 1);
            listHost.Children.Add(_mid);

            _term = BuildTerminator();
            Grid.SetRow(_term, 2);
            listHost.Children.Add(_term);

            Grid.SetRow(listHost, 2);
            body.Children.Add(listHost);

            Grid foot = BuildFoot();
            Grid.SetRow(foot, 3);
            body.Children.Add(foot);

            page.Children.Add(Ui.Panel(body, T.PadPanel));

            UIElement root = Ui.PageRoot(BuildHeader(c), page);
            Refresh(c);
            return root;
        }

        private UIElement BuildVaultHero()
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.PanelHead("\u5199\u524D\u5B89\u5168\u91D1\u5E93", "ATOMIC PRE-WRITE VAULT \u00B7 REVERSIBLE LEDGER",
                Ui.Mono("\u5148\u843D\u76D8 \u00B7 \u540E\u5199\u5165 \u00B7 \u53EF\u9010\u6761\u56DE\u653E", T.FsNano, T.Ok)));
            sp.Children.Add(Gap(12));
            sp.Children.Add(BuildStrip());
            return sp;
        }

        // ---- 读数带：三格 Ui.Stat -------------------------------------------
        // 这一页的主指标是"还有多少条可还原"：记录数占最左、也是唯一被读到的位置，
        // 动作数与最近写入回答"这些记录是谁写进来的、什么时候"。
        private UIElement BuildStrip()
        {
            Grid g = new Grid();
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());

            _kRecords = Ui.Mono("\u2014", T.FsNumL, T.Hi);
            _kActions = Ui.Mono("\u2014", T.FsNumL, T.Hi);
            _kLast = Ui.Mono("\u2014", T.FsNumL, T.Hi);

            FrameworkElement[] cells = new FrameworkElement[3];
            cells[0] = Ui.GlassTile(Ui.Stat("RECORDS", _kRecords, "\u6761", null, "\u8D26\u672C\u91CC\u7684\u5199\u524D\u8BB0\u5F55 \u00B7 \u53EF\u9010\u6761\u56DE\u653E"), 14, T.Accent);
            cells[1] = Ui.GlassTile(Ui.Stat("ACTIONS", _kActions, "\u4E2A", null, "\u4EA7\u751F\u8FD9\u4E9B\u8BB0\u5F55\u7684\u52A8\u4F5C"), 14, T.Accent2);
            cells[2] = Ui.GlassTile(Ui.Stat("LAST WRITE", _kLast, "", null, "\u6700\u8FD1\u4E00\u6B21\u5199\u5165\u7CFB\u7EDF\u7684\u65F6\u95F4"), 14, T.Ok);

            for (int i = 0; i < 3; i++)
            {
                if (i > 0) cells[i].Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }

        // ---- 列头 -----------------------------------------------------------
        /// <summary>列头只让出 RowShell 的那 16px（2px 色条 + 14px 边距），列宽与行同源。</summary>
        private static Border BuildColumnHead()
        {
            Grid row = RowGrid();
            row.Height = 20;
            Col(row, "\u65F6\u95F4", 0, false);
            Col(row, "\u52A8\u4F5C / \u952E", 2, false);
            Col(row, "\u539F\u503C \u2192 \u65B0\u503C", 3, true);
            Col(row, "\u72B6\u6001", 4, true);

            Border wrap = new Border();
            wrap.Padding = new Thickness(ShellInset, 0, 0, 0);
            wrap.Child = row;
            return wrap;
        }

        private static void Col(Grid row, string text, int col, bool right)
        {
            TextBlock t = Ui.Label(text, T.Mute);
            t.Opacity = 0.75;
            if (right) t.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(t, col);
            row.Children.Add(t);
        }

        private static Grid RowGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTime) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColRail) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColChange) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColBadge) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColCtrl) });
            return g;
        }

        // ---- 末尾终止符 ------------------------------------------------------
        private Grid BuildTerminator()
        {
            Grid term = new Grid();
            term.Margin = new Thickness(0, 6, 0, 6);
            term.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ShellInset + ColTime + ColRail) });
            term.ColumnDefinitions.Add(new ColumnDefinition());

            TextBlock tick = Ui.Mono("\u2014", T.FsNano, T.Mute);
            tick.HorizontalAlignment = HorizontalAlignment.Right;
            tick.Margin = new Thickness(0, 0, 8, 0);
            term.Children.Add(tick);

            _termText = Ui.Label("END OF LEDGER", T.Mute);
            Grid.SetColumn(_termText, 1);
            term.Children.Add(_termText);
            return term;
        }

        // ---- 页脚 -----------------------------------------------------------
        private Grid BuildFoot()
        {
            Grid foot = new Grid();
            foot.Margin = new Thickness(0, 18, 0, 0);
            foot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            foot.Children.Add(Ui.Cap(T.Line, Ui.Wash(T.Accent, 0x66), 26));

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(sp, 1);

            _path = Ui.Mono("", T.FsNano, T.Mute);
            _path.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(_path);

            Grid notes = new Grid();
            notes.Margin = new Thickness(0, 12, 0, 0);
            for (int i = 0; i < 3; i++) notes.ColumnDefinitions.Add(new ColumnDefinition());
            notes.Children.Add(Note(0, "\u5199\u524D\u65E5\u5FD7", "\u6539\u4EFB\u4F55\u503C\u4E4B\u524D\uFF0C\u5148\u628A\u539F\u503C\u4E0E\u300C\u539F\u672C\u662F\u5426\u5B58\u5728\u300D\u5199\u8FDB\u8D26\u672C\uFF1B\u843D\u76D8\u5931\u8D25\u5C31\u4E2D\u6B62\u672C\u6B21\u6539\u52A8\u3002"));
            notes.Children.Add(Note(1, "\u539F\u5B50\u5199\u5165", "\u4E34\u65F6\u6587\u4EF6 + File.Replace + fsync\uFF0C\u65AD\u7535\u4E0D\u4F1A\u7559\u4E0B\u534A\u4E2A JSON\uFF1B\u635F\u574F\u65F6\u81EA\u52A8\u9694\u79BB\u5B58\u8BC1\u3002"));
            notes.Children.Add(Note(2, "\u8FD8\u539F\u8BED\u4E49", "\u53EA\u56DE\u653E\u8D26\u672C\u8BB0\u5F55\u8FC7\u7684\u539F\u503C\uFF0C\u4E0D\u4F1A\u8986\u76D6\u4F60\u5728\u672C\u5DE5\u5177\u4E4B\u5916\u540E\u6765\u505A\u7684\u4FEE\u6539\u3002"));
            sp.Children.Add(notes);

            foot.Children.Add(sp);
            return foot;
        }

        private static UIElement Note(int col, string title, string text)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(col == 0 ? 0 : 22, 0, 0, 0);
            TextBlock t = Ui.Tx(title, T.FsSmall, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(t);
            TextBlock d = Ui.Tx(text, T.FsMicro, T.Mute, T.FCn);
            d.Margin = new Thickness(0, 6, 0, 0);
            d.TextWrapping = TextWrapping.Wrap;
            d.LineHeight = 16;
            sp.Children.Add(d);
            Grid.SetColumn(sp, col);
            return sp;
        }

        // ---- 页头 -----------------------------------------------------------
        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            Btn setupBtn = new Btn("\u5B89\u88C5 / \u5378\u8F7D", BtnKind.Ghost);
            setupBtn.Height = T.BtnHLg;
            setupBtn.Margin = new Thickness(0, 0, 8, 0);
            setupBtn.VerticalAlignment = VerticalAlignment.Bottom;
            setupBtn.Click += delegate { if (c != null && c.Shell != null) c.Shell.OpenSetupDialog(false); };
            Tip.Attach(setupBtn, "\u7BA1\u7406\u672C\u673A\u5B89\u88C5\u3001\u81EA\u9009\u684C\u9762/\u5F00\u59CB\u83DC\u5355\u5FEB\u6377\u65B9\u5F0F\uFF0C\u6216\u5378\u8F7D\u672C\u8F6F\u4EF6");
            right.Children.Add(setupBtn);

            Btn acceptAll = new Btn("\u5168\u90E8\u4FDD\u7559", BtnKind.Ghost);
            acceptAll.Height = T.BtnHLg;
            acceptAll.Margin = new Thickness(0, 0, 8, 0);
            acceptAll.VerticalAlignment = VerticalAlignment.Bottom;
            Tip.Attach(acceptAll, "\u786E\u8BA4\u4FDD\u7559\u5168\u90E8\u5F53\u524D\u7CFB\u7EDF\u8BBE\u7F6E\uFF0C\u6E05\u7A7A\u5F85\u8FD8\u539F\u8D26\u672C\uFF08\u4E0D\u56DE\u6EDA\u7CFB\u7EDF\uFF09");
            acceptAll.Click += delegate
            {
                ApplyReport r = c.Engine.AcceptAll();
                c.Shell.Toast(r.Ok ? ("\u5DF2\u5F52\u6863 " + r.Applied + " \u9879\uFF08\u4FDD\u7559\u5F53\u524D\u503C\uFF09") : ("\u5F52\u6863\u5931\u8D25\uFF1A" + FirstLine(r)), r.Ok);
                Refresh(c);
            };
            right.Children.Add(acceptAll);

            Btn exportSnap = new Btn("\u5BFC\u51FA\u5FEB\u7167", BtnKind.Quiet);
            exportSnap.Height = T.BtnHLg;
            exportSnap.VerticalAlignment = VerticalAlignment.Bottom;
            exportSnap.Margin = new Thickness(0, 0, 8, 0);
            exportSnap.Click += delegate { if (c != null && c.Shell != null) c.Shell.ExportLedgerSnapshot(); };
            Tip.Attach(exportSnap, "\u5C06\u5F53\u524D\u5199\u524D\u8D26\u672C\u5BFC\u51FA\u4E3A\u5E26\u65F6\u95F4\u6233\u7684 JSON \u5907\u4EFD");
            right.Children.Add(exportSnap);

            HoldBtn revertAll = new HoldBtn("\u957F\u6309\u5168\u90E8\u8FD8\u539F", 1200, 0x40FF5C63);
            revertAll.VerticalAlignment = VerticalAlignment.Bottom;
            revertAll.Confirmed += delegate
            {
                ApplyReport r = c.Engine.RevertAll();
                c.Shell.Toast(r.Ok ? ("\u5168\u90E8\u8FD8\u539F\u5B8C\u6210 \u00B7 " + r.Applied + " \u9879") : ("\u6709 " + r.Failed + " \u9879\u672A\u80FD\u8FD8\u539F"), r.Ok);
                Refresh(c);
            };
            right.Children.Add(revertAll);

            return Ui.PageHeader("LEDGER / 10", "\u8FD8\u539F\u4E2D\u5FC3", "\u6BCF\u4E00\u6B21\u5199\u7CFB\u7EDF\u4E4B\u524D\uFF0C\u539F\u503C\u90FD\u5DF2\u539F\u5B50\u843D\u76D8\uFF1B\u8FD9\u91CC\u53EF\u4EE5\u9010\u6761\u56DE\u653E\uFF0C\u4E5F\u53EF\u4EE5\u6574\u4F53\u56DE\u5230\u63A5\u7BA1\u524D\u7684\u72B6\u6001", right);
        }

        // ---- 分类筛选条 -------------------------------------------------------
        private Grid BuildFilterBar()
        {
            Grid bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition());

            Ui.SegTabItem[] items = new Ui.SegTabItem[]
            {
                new Ui.SegTabItem("all", "\u5168\u90E8", "\u663E\u793A\u5168\u90E8\u5199\u524D\u8BB0\u5F55"),
                new Ui.SegTabItem("reg", "\u6CE8\u518C\u8868", "\u56DE\u653E HKLM / HKCU \u6CE8\u518C\u8868\u952E\u503C\u4E0E\u670D\u52A1\u914D\u7F6E\u53D8\u66F4\uFF08\u542B Service \u7C7B\u8BB0\u5F55\uFF09"),
                new Ui.SegTabItem("power", "\u7535\u6E90\u65B9\u6848", "\u7535\u6E90\u8BA1\u5212\u3001AC \u8282\u80FD\u4E0E\u4F11\u7720\u914D\u7F6E"),
                new Ui.SegTabItem("daemon", "\u5B88\u62A4\u4E34\u65F6", "\u6E38\u620F\u5B88\u62A4\u6216\u6863\u6848\u6302\u8F7D\u5199\u5165\u7684\u4E34\u65F6\u8BB0\u5F55")
            };
            _tabs = new Ui.SegTabs(items, SelectedIndex(_filter));
            _tabs.Changed += delegate
            {
                _filter = _tabs.SelectedKey;
                Refresh(_c != null ? _c : C);
            };
            bar.Children.Add(_tabs);

            _filterInfo = Ui.Mono("", T.FsNano, T.Mute);
            _filterInfo.HorizontalAlignment = HorizontalAlignment.Right;
            _filterInfo.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_filterInfo, 1);
            bar.Children.Add(_filterInfo);
            return bar;
        }

        private static int SelectedIndex(string key)
        {
            if (key == "reg") return 1;
            if (key == "power") return 2;
            if (key == "daemon") return 3;
            return 0;
        }

        // ---- 时间轴 ---------------------------------------------------------
        private void Refresh(Core c)
        {
            if (c == null) c = _c;
            if (c == null || _list == null) return;
            _c = c;
            _list.Children.Clear();
            _mid.Children.Clear();

            IList<LedgerEntry> entries = c.Engine.Ledger.Entries;
            _path.Text = "\u8D26\u672C\u4F4D\u7F6E\uFF1A" + c.Engine.Ledger.FilePath;
            PaintKpi(entries);

            if (entries.Count == 0)
            {
                if (_filterInfo != null) _filterInfo.Text = "";
                _term.Visibility = Visibility.Collapsed;
                _mid.Children.Add(Ui.Empty(Glyph.Shield,
                    "\u8D26\u672C\u4E3A\u7A7A \u00B7 \u7CFB\u7EDF\u5904\u4E8E\u539F\u59CB\u72B6\u6001",
                    "\u5E94\u7528\u4EFB\u610F\u8C03\u4F18\u9879\u540E\uFF0C\u539F\u503C\u4F1A\u8FDE\u540C\u52A8\u4F5C\u540D\u4E00\u8D77\u51FA\u73B0\u5728\u8FD9\u6761\u65F6\u95F4\u8F74\u4E0A\u3002\u5230\u73B0\u5728\u4E3A\u6B62\uFF0C\u8FD9\u4E2A\u5DE5\u5177\u6CA1\u6709\u5BF9\u4F60\u7684\u7CFB\u7EDF\u5199\u5165\u8FC7\u4EFB\u4F55\u4E1C\u897F\u3002",
                    T.Ok, GoTuning(c)));
                return;
            }

            List<LedgerEntry> shown = FilterEntries(entries, _filter);
            if (_filterInfo != null)
            {
                _filterInfo.Text = _filter == "all"
                    ? (entries.Count + " \u6761\u8BB0\u5F55")
                    : ("\u7B5B\u9009 " + shown.Count + " / " + entries.Count + " \u6761");
            }

            if (shown.Count == 0)
            {
                _term.Visibility = Visibility.Collapsed;
                _mid.Children.Add(Ui.Empty(Glyph.Restore,
                    "\u5F53\u524D\u5206\u7C7B\u6682\u65E0\u8BB0\u5F55",
                    "\u300C" + FilterLabel(_filter) + "\u300D\u4E0B\u6CA1\u6709\u53EF\u8FD8\u539F\u7684\u5199\u5165\u8BB0\u5F55\uFF0C\u53EF\u5207\u56DE\u300C\u5168\u90E8\u300D\u67E5\u770B\u5B8C\u6574\u8D26\u672C\u3002",
                    T.Mute, ResetFilterBtn()));
                return;
            }

            _term.Visibility = Visibility.Visible;
            PaintRows(c, entries, shown);
        }

        private List<LedgerEntry> FilterEntries(IList<LedgerEntry> entries, string filter)
        {
            List<LedgerEntry> list = new List<LedgerEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                if (MatchesFilter(entries[i], filter)) list.Add(entries[i]);
            }
            return list;
        }

        private static bool MatchesFilter(LedgerEntry e, string filter)
        {
            if (e == null) return false;
            if (filter == "all" || string.IsNullOrEmpty(filter)) return true;
            bool daemon = IsDaemonTemp(e);
            if (filter == "daemon") return daemon;
            if (daemon) return false;
            if (filter == "power") return e.Kind == "PowerAc" || e.Kind == "Hibernate" || e.Kind == "Bcd";
            if (filter == "reg") return e.Kind == "Reg" || e.Kind == "RegKv" || string.Equals(e.Kind, "Service", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(filter, "service", StringComparison.OrdinalIgnoreCase))
                return string.Equals(e.Kind, "Service", StringComparison.OrdinalIgnoreCase)
                    || (e.Kind == "Reg" && e.Path != null && e.Path.IndexOf(@"\Services\", StringComparison.OrdinalIgnoreCase) >= 0);
            return true;
        }

        private static bool IsDaemonTemp(LedgerEntry e)
        {
            string aid = e.ActionId == null ? "" : e.ActionId;
            if (aid.StartsWith("daemon-", StringComparison.OrdinalIgnoreCase)) return true;
            if (aid.StartsWith("profile-", StringComparison.OrdinalIgnoreCase)) return true;
            string name = e.ActionName == null ? "" : e.ActionName;
            return name.StartsWith("\u5B88\u62A4", StringComparison.Ordinal) || name.StartsWith("\u6863\u6848", StringComparison.Ordinal);
        }

        private static string FilterLabel(string filter)
        {
            if (filter == "reg") return "\u6CE8\u518C\u8868";
            if (filter == "power") return "\u7535\u6E90\u65B9\u6848";
            if (filter == "daemon") return "\u5B88\u62A4\u4E34\u65F6";
            return "\u5168\u90E8";
        }

        private UIElement ResetFilterBtn()
        {
            Btn b = new Btn("\u67E5\u770B\u5168\u90E8", BtnKind.Quiet);
            b.Height = T.BtnH;
            b.Click += delegate { if (_tabs != null) _tabs.Select(0, true); };
            return b;
        }

        private void PaintKpi(IList<LedgerEntry> entries)
        {
            if (_kRecords != null) _kRecords.Text = entries.Count.ToString();
            if (_kActions != null) _kActions.Text = CountActions(entries).ToString();
            if (_kLast == null) return;

            string last = "\u2014";
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                string t = entries[i].Time;
                if (t == null || t.Length < 19) continue;
                last = t.Substring(11, 8);
                break;
            }
            _kLast.Text = last;
        }

        /// <summary>空态里的"下一步"：账本为空是因为还没动过系统，那就去能动手的地方。</summary>
        private static UIElement GoTuning(Core c)
        {
            Btn b = new Btn("\u53BB\u8C03\u4F18\u77E9\u9635", BtnKind.Accent);
            b.Height = T.BtnH;
            b.Click += delegate { if (c != null && c.Shell != null) c.Shell.Go("tuning"); };
            return b;
        }

        private void PaintRows(Core c, IList<LedgerEntry> all, IList<LedgerEntry> shown)
        {
            // 同一个动作会写多个键（例如 MMCCSS 一次改 3 个）。逐个重复动作名读起来像噪音，
            // 所以给重复出现的行加一个 ×N 标记，读者一眼知道这是一次批量写。
            Dictionary<string, int> perAction = new Dictionary<string, int>();
            for (int i = 0; i < all.Count; i++)
            {
                string aid = all[i].ActionId == null ? "" : all[i].ActionId;
                int n;
                perAction.TryGetValue(aid, out n);
                perAction[aid] = n + 1;
            }

            // 账本没有上限，而外层 ThinScroll 的 CanContentScroll=false（不虚拟化）：
            // 每条记录要建十几个元素，几百条就是几千个。只渲染最近 MaxRender 条，
            // 并在末尾说明还有多少条没画出来 —— 比"卡住几秒"和"悄悄截断"都好。
            int from = Math.Max(0, shown.Count - MaxRender);
            for (int i = shown.Count - 1; i >= from; i--)
            {
                LedgerEntry e = shown[i];
                int occ;
                perAction.TryGetValue(e.ActionId == null ? "" : e.ActionId, out occ);
                _list.Children.Add(TimelineRow(c, e, i == shown.Count - 1, i == 0, occ));
            }

            string tail = Ui.Spaced("END OF LEDGER") + "     \u00B7     \u6700\u65E9 " + Earliest(shown);
            if (_filter != "all") tail += "     \u00B7     \u5F53\u524D\u7B5B\u9009 " + shown.Count + "/" + all.Count + " \u6761";
            if (from > 0) tail += "     \u00B7     \u53E6\u6709 " + from + " \u6761\u66F4\u65E9\u8BB0\u5F55\u672A\u663E\u793A";
            if (_termText != null) _termText.Text = tail;
        }

        /// <summary>一次最多渲染多少条账本记录（同 LogPage 的口径）。</summary>
        private const int MaxRender = 200;

        /// <summary>一条账本记录：RowShell 给风险色条，里面是六列行网格。</summary>
        private UIElement TimelineRow(Core c, LedgerEntry e, bool first, bool last, int occurrences)
        {
            uint risk = RiskTint(e);
            Grid row = RowGrid();
            row.MinHeight = RowH - 18;            // RowShell 上下各 9
            row.Background = Brushes.Transparent;

            TextBlock time = Ui.Mono(TimeOf(e), T.FsNano, T.Mute);
            time.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(time);

            row.Children.Add(Rail(first, last, risk));

            StackPanel title = new StackPanel();
            title.VerticalAlignment = VerticalAlignment.Center;
            TextBlock act = Ui.Tx(e.ActionName == null ? "\u672A\u547D\u540D\u52A8\u4F5C" : e.ActionName, T.FsBody, T.Hi, T.FCn);
            act.FontWeight = FontWeights.SemiBold;
            act.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(act);
            TextBlock key = Ui.Mono(KeyOf(e), T.FsNano, T.Dim);
            key.Margin = new Thickness(0, 4, 12, 0);
            key.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(key);
            Grid.SetColumn(title, 2);
            row.Children.Add(title);

            StackPanel change = ChangeCell(e);
            Grid.SetColumn(change, 3);
            row.Children.Add(change);

            row.Children.Add(BadgeCell(risk, occurrences));

            UIElement actions = ActionCell(c, e);
            Grid.SetColumn(actions, 5);
            row.Children.Add(actions);

            Border shell = Ui.RowShell(row, risk);
            shell.Background = Brushes.Transparent;
            shell.MouseEnter += delegate { shell.Background = T.S(T.LineFaint); };
            shell.MouseLeave += delegate { shell.Background = Brushes.Transparent; };
            return shell;
        }

        /// <summary>时间线轨道：细线 + 5px 节点。首尾两行把线收在节点上（序列有端点）。</summary>
        private static Grid Rail(bool first, bool last, uint risk)
        {
            Grid rail = new Grid();
            rail.Width = ColRail;

            Border line = new Border();
            line.Width = 1;
            line.Background = T.S(0x2EFFFFFF);
            line.HorizontalAlignment = HorizontalAlignment.Center;
            line.VerticalAlignment = VerticalAlignment.Stretch;
            line.Margin = new Thickness(0, first ? RowH / 2 - 9 : 0, 0, last ? RowH / 2 - 9 : 0);
            line.SnapsToDevicePixels = true;
            RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
            rail.Children.Add(line);

            Ellipse node = new Ellipse();
            node.Width = 5;
            node.Height = 5;
            node.Fill = T.S(risk);
            node.HorizontalAlignment = HorizontalAlignment.Center;
            node.VerticalAlignment = VerticalAlignment.Center;
            rail.Children.Add(node);

            Grid.SetColumn(rail, 1);
            return rail;
        }

        /// <summary>原值 → 新值：删除型（新值为空）用 Bad，其余用正文色。</summary>
        private static StackPanel ChangeCell(LedgerEntry e)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Right;
            sp.VerticalAlignment = VerticalAlignment.Center;

            string oldStr = OldText(e);
            string newStr = NewText(e);
            TextBlock a = Ui.Mono(oldStr, T.FsNano, T.Mute);
            Limit(a);
            sp.Children.Add(a);
            sp.Children.Add(Ui.Mono(" \u2192 ", T.FsNano, T.Dim));

            TextBlock b = Ui.Mono(newStr, T.FsNano, e.NewValue == null ? T.Bad : T.Text);
            Limit(b);
            sp.Children.Add(b);

            string tip = PowerAcTip(e, oldStr, newStr);
            if (tip != null) Tip.Attach(sp, tip);
            return sp;
        }

        private static void Limit(TextBlock t)
        {
            t.MaxWidth = 68;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        /// <summary>
        /// 徽标组：风险字母（只给 B / C 两档）+ ×N（一次批量写）。
        /// A 档（注册表值原位写回）占绝大多数，每行都挂一枚绿色 A 就成了九处语义色并排，
        /// 读者会把它当装饰而不再读它。颜色的归属仍然是左缘色条。
        /// </summary>
        private static StackPanel BadgeCell(uint risk, int occurrences)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Right;
            sp.VerticalAlignment = VerticalAlignment.Center;

            if (risk != T.Ok) sp.Children.Add(Ui.Tag(risk == T.Bad ? "C" : "B", risk));
            if (occurrences > 1)
            {
                Border again = Ui.Tag("\u00D7" + occurrences, T.SeriesA);
                if (sp.Children.Count > 0) again.Margin = new Thickness(5, 0, 0, 0);
                sp.Children.Add(again);
            }
            Grid.SetColumn(sp, 4);
            return sp;
        }

        private UIElement ActionCell(Core c, LedgerEntry e)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Right;
            sp.VerticalAlignment = VerticalAlignment.Center;

            string id = e.Id;
            Btn ab = new Btn("\u4FDD\u7559", BtnKind.Quiet);
            ab.Height = T.BtnH;
            ab.Compact = true;
            ab.Margin = new Thickness(0, 0, 4, 0);
            ab.VerticalAlignment = VerticalAlignment.Center;
            Tip.Attach(ab, "\u786E\u8BA4\u4FDD\u7559\u5F53\u524D\u7CFB\u7EDF\u72B6\u6001\uFF0C\u4EC5\u4ECE\u5F85\u8FD8\u539F\u8D26\u672C\u5F52\u6863\uFF08\u4E0D\u56DE\u6EDA\u7CFB\u7EDF\uFF09");
            ab.Click += delegate
            {
                ApplyReport r = c.Engine.AcceptEntry(id);
                c.Shell.Toast(r.Ok ? "\u5DF2\u5F52\u6863 1 \u9879\uFF08\u4FDD\u7559\u5F53\u524D\u503C\uFF09" : "\u5F52\u6863\u5931\u8D25\uFF1A" + FirstLine(r), r.Ok);
                Refresh(c);
            };
            sp.Children.Add(ab);

            Btn rb = RevertBtn(c, e);
            sp.Children.Add(rb);
            return sp;
        }

        private Btn RevertBtn(Core c, LedgerEntry e)
        {
            string id = e.Id;
            Btn rb = new Btn("\u8FD8\u539F", BtnKind.Quiet);
            rb.Height = T.BtnH;
            rb.Compact = true;
            rb.VerticalAlignment = VerticalAlignment.Center;
            if (e.NewValue == null)
                Tip.Attach(rb, "\u56DE\u6EDA\u5220\u9664\u578B\u5199\u5165\uFF1A\u5C06\u91CD\u5EFA\u6B64\u524D\u5DF2\u79FB\u9664\u7684\u6CE8\u518C\u8868\u9879");
            rb.Click += delegate
            {
                ApplyReport r = c.Engine.RevertEntry(id);
                c.Shell.Toast(r.Ok ? "\u5DF2\u8FD8\u539F 1 \u9879" : "\u8FD8\u539F\u5931\u8D25\uFF1A" + FirstLine(r), r.Ok);
                Refresh(c);
            };
            return rb;
        }

        // ---- 语义 ------------------------------------------------------------
        /// <summary>
        /// 还原风险：
        ///   C = 删除型写入（账本里原值不存在）。还原等于"重建一个值"，最不可逆。
        ///   B = 走命令行工具回写（bcdedit / powercfg / powercfg /h），成功与否由工具确认。
        ///   A = 注册表值原位写回：账本里有原值与类型，写入与校验都是同一条路径。
        /// 注意：旧版按 "Svc" / "File" 分色，而账本里根本没有这两种 Kind —— 那两个判断
        /// 从来不会命中，所有行都落在同一个色上（看起来像"没有风险分级"）。
        /// </summary>
        private static uint RiskTint(LedgerEntry e)
        {
            if (e.NewValue == null) return T.Bad;
            if (e.Kind == "Hibernate" || string.Equals(e.ActionId, "hiber-off", StringComparison.OrdinalIgnoreCase))
                return T.Bad;
            if (e.Kind == "Bcd" || e.Kind == "PowerAc" || string.Equals(e.Kind, "Service", StringComparison.OrdinalIgnoreCase))
                return T.Warn;
            return T.Ok;
        }

        private static string TimeOf(LedgerEntry e)
        {
            string t = e.Time;
            if (t != null && t.Length >= 19) return t.Substring(11, 8);
            return t == null ? "" : t;
        }

        private static string KindText(LedgerEntry e)
        {
            switch (e.Kind)
            {
                case "Reg": return "REG";
                case "RegKv": return "REG·KV";
                case "Service": return "SERVICE";
                case "Bcd": return "BCD";
                case "PowerAc": return "POWER";
                case "Hibernate": return "HIBER";
                default: return e.Kind == null ? "\u2014" : e.Kind.ToUpperInvariant();
            }
        }

        private static string KeyOf(LedgerEntry e)
        {
            string body;
            if (e.Kind == "Reg" || e.Kind == "RegKv")
            {
                string path = e.Path == null ? "" : e.Path;
                int cut = path.LastIndexOf('\\');
                string tail = cut >= 0 ? path.Substring(cut + 1) : path;
                if (tail.Length > 46) tail = tail.Substring(tail.Length - 46);
                body = tail + "\\" + (e.Name == null ? "" : e.Name);
                if (e.Kind == "RegKv" && !string.IsNullOrEmpty(e.Extra))
                {
                    int bar = e.Extra.IndexOf('|');
                    string sub = bar > 0 ? e.Extra.Substring(0, bar) : e.Extra;
                    body += "[" + sub + "]";
                }
            }
            else body = e.Name == null ? "" : e.Name;
            return KindText(e) + " \u00B7 " + body;
        }

        private static string OldText(LedgerEntry e)
        {
            if (!e.Existed) return "(\u4E0D\u5B58\u5728)";
            if (string.IsNullOrEmpty(e.OldValue)) return "(\u7A7A)";
            long v;
            if (e.Kind == "PowerAc" && TryParseHex(e.OldValue, out v))
                return "0x" + v.ToString("X") + "(" + v + ")";
            return e.OldValue;
        }

        private static string NewText(LedgerEntry e)
        {
            if (e.NewValue == null) return "(\u5220\u9664)";
            if (e.NewValue.Length == 0) return "(\u7A7A)";
            long v;
            if (e.Kind == "PowerAc" && TryParsePowerValue(e.NewValue, out v))
                return "0x" + v.ToString("X") + "(" + v + ")";
            return e.NewValue;
        }

        private static string PowerAcTip(LedgerEntry e, string oldStr, string newStr)
        {
            if (e == null || e.Kind != "PowerAc") return null;
            return "AC \u7535\u6E90\u8BBE\u7F6E\u7D22\u5F15\uFF08\u5341\u516D\u8FDB\u5236 / \u5341\u8FDB\u5236\uFF09\uFF1A" + oldStr + " \u2192 " + newStr;
        }

        private static bool TryParseHex(string s, out long val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            return long.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out val);
        }

        private static bool TryParsePowerValue(string s, out long val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return TryParseHex(t, out val);
            return long.TryParse(t, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out val);
        }

        private static string Earliest(IList<LedgerEntry> entries)
        {
            string best = "";
            for (int i = 0; i < entries.Count; i++)
            {
                string t = entries[i].Time;
                if (t == null || t.Length < 19) continue;
                if (best.Length == 0 || string.CompareOrdinal(t, best) < 0) best = t;
            }
            return best.Length >= 19 ? best.Substring(0, 10) + " " + best.Substring(11, 8) : "\u2014";
        }

        private static int CountActions(IList<LedgerEntry> entries)
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                string id = entries[i].ActionId;
                if (id == null || ids.Contains(id)) continue;
                ids.Add(id);
            }
            return ids.Count;
        }

        public override void OnEnter() { Refresh(C); }
    }
}