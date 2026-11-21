// ============================================================================
//  Benchmark.Compare.cs — 历史对比 + 底部双栏（Benchmark 分片）
//
//  历史列表回答的是"这台机器最近几次是什么样"，所以它必须：
//   · 给走势，不只给数字（14 点迷你趋势带，越低越好）；
//   · 可点：点一条就把它的分布切到上面的直方图；
//   · 老实：历史档案只存聚合读数，没有样本明细的记录点开后不画柱，而是说明原因。
//
//  Rev86：这一块原来是 7 个手拼列宽（3/30/100/66/62/54/*），和其它页的行网格对不上，
//  也和本页 hero 的三格读数对不上。现在改用 DESIGN-86 §3 的同一套数字：
//  读数 110 / 状态 92 / 计数 56，行高 50（单行文字），列宽全部是 4 的倍数。
//  表头与行共用同一个 HistoryGrid()，因此列标题不可能再和列内容错位。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Benchmark
    {
        private StackPanel _histList;
        private TextBlock _histMeta, _histSub, _histCol3Head, _histCol5Head;
        private Border _sparkHost;

        // ---- 列宽：全页一套（留足列间边距，避免 N 与 NOTE 粘连）-----------
        private const double ColMark = 4;
        private const double ColIdx = 32;
        private const double ColTime = 96;
        private const double ColP99 = 112;
        private const double ColDelta = 96;
        private const double ColCount = 68;
        private const double RowH = 50;

        /// <summary>底部双栏：历史（可点） + 判定。窄窗自动堆叠成一行。</summary>
        private UIElement BuildBottom(Core c)
        {
            Reflow cols = new Reflow(T.Gap, 1, 1, 2, 2);
            cols.Add(BuildHistoryPanel(c));
            cols.Add(BuildVerdictPanel(c));
            return cols;
        }

        // ---- 历史面板 --------------------------------------------------------
        private UIElement BuildHistoryPanel(Core c)
        {
            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel hl = new StackPanel();
            StackPanel trow = new StackPanel();
            trow.Orientation = Orientation.Horizontal;

            Border tick = new Border();
            tick.Width = 2;
            tick.Height = 13;
            tick.Background = T.S(T.Accent);
            tick.Margin = new Thickness(0, 0, 9, 1);
            tick.VerticalAlignment = VerticalAlignment.Bottom;
            trow.Children.Add(tick);

            TextBlock t = Ui.Tx("\u5386\u53F2\u5BF9\u6BD4", T.FsTitle, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            trow.Children.Add(t);
            hl.Children.Add(trow);

            _histSub = Ui.Label("RUNS / SAME 8.0 S WINDOW", T.Mute);
            _histSub.Margin = new Thickness(11, 4, 0, 0);
            _histSub.Opacity = 0.8;
            hl.Children.Add(_histSub);
            head.Children.Add(hl);

            StackPanel hr = new StackPanel();
            hr.Orientation = Orientation.Horizontal;
            hr.VerticalAlignment = VerticalAlignment.Center;

            _sparkHost = new Border();
            _sparkHost.Width = 128;
            _sparkHost.Height = 32;
            _sparkHost.Margin = new Thickness(0, 0, 10, 0);
            hr.Children.Add(_sparkHost);

            Btn exp = new Btn("CSV", BtnKind.Quiet);
            exp.Height = T.BtnHSm;
            exp.Compact = true;
            exp.SetIcon(Glyph.Download, 11);
            exp.Click += delegate { ExportCsv(); };
            hr.Children.Add(exp);

            Grid.SetColumn(hr, 1);
            head.Children.Add(hr);
            body.Children.Add(head);

            body.Children.Add(Gap(10));
            _histMeta = Ui.Mono("", T.FsNano, T.Mute);
            _histMeta.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(_histMeta);
            body.Children.Add(Gap(7));
            body.Children.Add(HistoryHead());
            body.Children.Add(Ui.Cap(T.Line, Ui.Wash(T.Accent, 0x55), 22));

            _histList = new StackPanel();
            _histList.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(_histList);

            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>表头与数据行共用的列模板——列标题不可能再和列内容错位。</summary>
        private static Grid HistoryGrid()
        {
            Grid g = new Grid();
            g.Height = RowH;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColMark) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColIdx) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTime) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColP99) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColDelta) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColCount) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            return g;
        }

        private Grid HistoryHead()
        {
            Grid g = HistoryGrid();
            g.Height = 20;
            Head(g, "TIME", 2);
            _histCol3Head = Head(g, "P99 \u00B5s", 3);
            Head(g, "DELTA", 4);
            _histCol5Head = Head(g, "N", 5);
            Head(g, "NOTE", 6);
            return g;
        }

        private static TextBlock Head(Grid host, string text, int col)
        {
            TextBlock t = Ui.Mono(text, T.FsNano, T.Mute);
            t.Opacity = 0.75;
            bool right = col >= 3 && col <= 5;
            t.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            if (right) t.Margin = new Thickness(0, 0, 12, 0);
            else if (col == 6) t.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(t, col);
            host.Children.Add(t);
            return t;
        }

        private void UpdateHistoryModeChrome()
        {
            if (_histSub != null)
            {
                _histSub.Text = Ui.Spaced(_mode == "wake" ? "RUNS / SAME 8.0 S WINDOW"
                    : (_mode == "mem" ? "RUNS / MEMORY BANDWIDTH & LATENCY" : "RUNS / THREAD PING-PONG"));
            }
            if (_histCol3Head != null) _histCol3Head.Text = _mode == "mem" ? "COPY MB/s" : "P99 \u00B5s";
            if (_histCol5Head != null) _histCol5Head.Text = _mode == "mem" ? "LAT ns" : "N";
        }

        private void RefreshHistory()
        {
            if (_histList == null) return;

            UpdateHistoryModeChrome();
            _histList.Children.Clear();

            List<RunData> same = ModeRuns(_mode);
            PaintSpark(same);

            if (same.Count == 0)
            {
                _histMeta.Text = "\u5C1A\u65E0\u540C\u53E3\u5F84\u8BB0\u5F55 \u00B7 \u5B8C\u6210\u4E00\u6B21\u6D4B\u8BD5\u81EA\u52A8\u5F52\u6863";
                string desc = _mode == "wake"
                    ? "\u8DD1\u6EE1\u4E00\u6B21 8.0 \u79D2\u7A97\u53E3\u540E\uFF0C\u8FD9\u91CC\u4F1A\u51FA\u73B0\u53EF\u70B9\u51FB\u7684\u8BB0\u5F55\u884C\uFF1B\u70B9\u9009\u53EF\u8BBE A/B \u5BF9\u7167\u57FA\u7EBF\u5E76\u56DE\u770B\u76F4\u65B9\u56FE\u3002"
                    : "\u5B8C\u6210\u4E00\u6B21\u6D4B\u8BD5\u540E\u8FD9\u91CC\u4F1A\u51FA\u73B0\u540C\u53E3\u5F84\u8BB0\u5F55\uFF1B\u70B9\u9009\u4EFB\u610F\u5386\u53F2\u53EF\u8BBE\u4E3A A/B \u5BF9\u7167\u57FA\u7EBF\u3002";
                UIElement empty = Ui.Empty(Glyph.Benchmark, "\u8FD8\u6CA1\u6709\u53EF\u6BD4\u8F83\u7684\u8FD0\u884C", desc, T.Mute, StartBtn());
                _histList.Children.Add(empty);
                return;
            }

            bool hib = HigherIsBetter(_mode);
            double best = hib ? -double.MaxValue : double.MaxValue;
            for (int i = 0; i < same.Count; i++)
            {
                double v = PrimaryValue(same[i], _mode);
                if (v <= 0) continue;
                if (hib ? v > best : v < best) best = v;
            }
            if (best == double.MaxValue || best == -double.MaxValue) best = 0;

            string unit = _mode == "mem" ? " MB/s" : " \u00B5s";
            _histMeta.Text = same.Count + " \u6B21\u8BB0\u5F55 \u00B7 \u6700\u4F73 "
                + FormatPrimary(best, _mode) + unit + " \u00B7 \u663E\u793A\u6700\u8FD1 "
                + Math.Min(ListMax, same.Count) + " \u6B21\uFF08\u70B9\u9009\u8BBE BASE\uFF09";

            RunData cur = ActiveRunForMode(same);
            RunData cmp = CompareBaseForMode(same, cur);
            int start = Math.Max(0, same.Count - ListMax);
            for (int i = same.Count - 1; i >= start; i--)
                _histList.Children.Add(BuildRunRow(same, i, cur, cmp, best));
        }

        /// <summary>空态里的"下一步"：直接开跑，而不是让读者自己找按钮。</summary>
        private UIElement StartBtn()
        {
            Btn b = new Btn(_mode == "wake" ? "\u5F00\u59CB\u91C7\u6837" : (_mode == "mem" ? "\u5F00\u59CB\u6D4B\u901F" : "\u5F00\u59CB\u6D4B\u8BD5"), BtnKind.Accent);
            b.Height = T.BtnH;
            b.Click += delegate { StartSample(); };
            return b;
        }

        /// <summary>一行历史记录：可点设为 A/B 对照基线，选中行左缘亮强调色刻度。</summary>
        private UIElement BuildRunRow(List<RunData> same, int index, RunData cur, RunData cmp, double best)
        {
            RunData r = same[index];
            RunData prev = index > 0 ? same[index - 1] : null;
            bool isBase = (_baseRun != null && r == _baseRun) || (_baseRun == null && r == cmp);
            bool isChart = _mode == "wake" && r == _chartRun;
            bool sel = isBase || isChart || (r == cur && _baseRun == null && _sel < 0);

            Grid row = HistoryGrid();
            row.Background = isBase ? T.S(Ui.Wash(T.SeriesB, 0x16)) : (sel ? T.S(T.LineFaint) : Brushes.Transparent);
            row.Cursor = Cursors.Hand;

            Border mark = new Border();
            mark.Width = 2;
            mark.HorizontalAlignment = HorizontalAlignment.Left;
            mark.Margin = new Thickness(0, 9, 0, 9);
            mark.Background = T.S(isBase ? T.SeriesB : (sel ? T.Accent : 0x00000000u));
            row.Children.Add(mark);

            Cell(row, "#" + (index + 1).ToString(), T.FsNano, sel ? T.Dim : T.Mute, 1);
            Cell(row, ShortTime(r.Time), T.FsNano, sel ? T.Text : T.Dim, 2);

            double val = PrimaryValue(r, _mode);
            bool hib = HigherIsBetter(_mode);
            double tol = Math.Max(0.05, Math.Min(0.5, best * 0.005));
            bool isBest = best > 0 && val > 0 && Math.Abs(val - best) <= tol;
            uint pcol = isBest ? T.Ok : (sel ? T.Hi : T.Text);
            Cell(row, FormatPrimary(val, _mode), T.FsNano, pcol, 3);

            string ds = "\u2014";
            uint dc = T.Mute;
            double pVal = prev != null ? PrimaryValue(prev, _mode) : 0;
            if (prev != null && pVal > 0 && val > 0)
            {
                double d = (val - pVal) / pVal * 100.0;
                bool better = hib ? d > 0 : d < 0;
                ds = Arrow(d) + Math.Abs(d).ToString("0.0") + "%";
                dc = Math.Abs(d) < 0.05 ? T.Dim : (better ? T.Ok : T.Warn);
            }
            Cell(row, ds, T.FsNano, dc, 4);

            string col5Text = _mode == "mem"
                ? (r.MemLatNs > 0 ? r.MemLatNs.ToString("0.0") : "\u2014")
                : r.Count.ToString("#,##0");
            Cell(row, col5Text, T.FsNano, T.Mute, 5);

            StackPanel tailHost = new StackPanel();
            tailHost.Orientation = Orientation.Horizontal;
            tailHost.VerticalAlignment = VerticalAlignment.Center;
            tailHost.Margin = new Thickness(12, 0, 0, 0);
            if (r == cur)
            {
                Border tTag = Ui.Tag("THIS", T.Accent);
                tTag.Margin = new Thickness(0, 0, 6, 0);
                tailHost.Children.Add(tTag);
            }
            else if (isBase)
            {
                Border bTag = Ui.Tag("BASE", T.SeriesB);
                bTag.Margin = new Thickness(0, 0, 6, 0);
                tailHost.Children.Add(bTag);
            }

            string tail = _mode == "wake"
                ? (r.TimerMs.ToString("0.00") + " ms \u00B7 " + (r.Sorted != null ? "\u6709\u6837\u672C\u660E\u7EC6" : "\u4EC5\u805A\u5408"))
                : (string.IsNullOrEmpty(r.Note) ? (r.TimerMs.ToString("0.00") + " ms") : r.Note);
            TextBlock meta = Ui.Mono(tail, T.FsNano, (r.Sorted != null || _mode != "wake") ? T.Dim : T.Mute);
            meta.VerticalAlignment = VerticalAlignment.Center;
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            tailHost.Children.Add(meta);
            Grid.SetColumn(tailHost, 6);
            row.Children.Add(tailHost);

            bool keepBg = isBase || sel;
            row.MouseEnter += delegate { if (!keepBg) row.Background = T.S(T.LineFaint); };
            row.MouseLeave += delegate { if (!keepBg) row.Background = Brushes.Transparent; };

            RunData clicked = r;
            int idx2 = index;
            row.MouseLeftButtonUp += delegate { SelectRun(clicked, idx2); };
            return row;
        }

        private static void Cell(Grid row, string text, double size, uint tint, int col)
        {
            TextBlock t = Ui.Mono(text, size, tint);
            bool right = col >= 3 && col <= 5;
            t.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, right ? 12 : 0, 0);
            Grid.SetColumn(t, col);
            row.Children.Add(t);
        }

        private void SelectRun(RunData r, int modeIndex)
        {
            if (r == null) return;
            List<RunData> same = ModeRuns(_mode);
            RunData cur = ActiveRunForMode(same);

            if (r == _baseRun)
            {
                _baseRun = null;
                _sel = -1;
                if (_mode == "wake")
                    _chartRun = (_curRun != null && _curRun.Mode == "wake") ? _curRun : LastWithSamples();
            }
            else if (r == cur)
            {
                _baseRun = null;
                _sel = -1;
                if (_mode == "wake") _chartRun = r;
            }
            else
            {
                _baseRun = r;
                _sel = modeIndex;
                if (_mode == "wake") _chartRun = r;
            }
            PaintAll();
            RefreshHistory();
        }

        // ---- 迷你走势 --------------------------------------------------------
        private void PaintSpark(List<RunData> same)
        {
            if (_sparkHost == null) return;

            int n = same.Count;
            if (n == 0)
            {
                _sparkHost.Child = null;
                return;
            }

            int start = Math.Max(0, n - 14);
            int cap = n - start;
            if (cap < 2) cap = 2;

            Sparkline sp = new Sparkline(cap);
            sp.Height = 30;
            sp.Tint = T.SeriesA;

            double lo = double.MaxValue;
            double hi = -double.MaxValue;
            for (int i = start; i < n; i++)
            {
                double v = PrimaryValue(same[i], _mode);
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            double pad = (hi - lo) * 0.18;
            if (pad <= 0.0001) pad = Math.Max(1.0, hi * 0.005);
            double a = lo - pad;
            double b = hi + pad;
            double span = b - a;

            for (int i = start; i < n; i++)
                sp.Push(span > 0 ? (PrimaryValue(same[i], _mode) - a) / span : 0.5);

            _sparkHost.Child = sp;
        }

        // ---- 导出 ------------------------------------------------------------
        private void ExportCsv()
        {
            List<BenchRecord> l = BenchHistory.Load();
            if (l.Count == 0)
            {
                if (C != null && C.Shell != null) C.Shell.Toast("\u5C1A\u65E0\u53EF\u5BFC\u51FA\u7684\u5386\u53F2", false);
                return;
            }
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Title = "\u5BFC\u51FA\u57FA\u51C6\u5386\u53F2";
            dlg.Filter = "CSV (*.csv)|*.csv";
            dlg.FileName = "gameoptimizer-bench.csv";
            if (dlg.ShowDialog() != true) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, BenchHistory.ToCsv(l), new System.Text.UTF8Encoding(true));
                if (C != null && C.Shell != null) C.Shell.Toast("\u5DF2\u5BFC\u51FA " + l.Count + " \u6761\u8BB0\u5F55", true);
            }
            catch (Exception ex)
            {
                if (C != null && C.Shell != null) C.Shell.Toast("\u5BFC\u51FA\u5931\u8D25\uFF1A" + ex.Message, false);
            }
        }
    }
}
