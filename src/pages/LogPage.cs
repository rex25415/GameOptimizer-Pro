// ============================================================================
//  LogPage.cs — 11 审计日志：追加式台账 / 结构化行 / 就地检索 / 自动跟随
//  行高 44、全等宽、三列（时间 76 / 级别 64 / 正文 *），表头与行共用 LineGrid()；
//  IO 指纹 + 内容指纹 + 增量追加保证稳态零多余重排。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public class LogPage : Page
    {
        public override string Key { get { return "log"; } }
        public override string Idx { get { return "11"; } }
        public override string Title { get { return "\u5BA1\u8BA1\u65E5\u5FD7"; } }
        public override string En { get { return "Audit Log"; } }
        public override string Ico { get { return Glyph.Log; } }

        // ---- 列宽：表头与行共用 --------------------------------------------
        // 76 / 64 都是 4 的倍数，正文列吃掉剩余宽度；行高 44 由任务书指定。
        private const double ColTime = 76;
        private const double ColLevel = 64;
        private const double RowH = 44;
        /// <summary>滚动区的左右内边距：表头必须让出同一组数，否则列标题会飘。</summary>
        private const double PadL = 16;
        private const double PadR = 14;

        // ---- 级别 -----------------------------------------------------------
        // 0 中性 / 1 好 / 2 需要看 / 3 坏了。只有这四档，页面里不许再出现第五种说法。
        private const int LvNeutral = 0;
        private const int LvOk = 1;
        private const int LvWarn = 2;
        private const int LvBad = 3;

        private StackPanel _lines;
        private TextBlock _path, _count, _filterPlaceholder, _diag;
        private ScrollViewer _scroll;
        private ThinScroll _thin;
        private TextBox _filter;
        private Ui.SegTabs _catTabs;
        private DispatcherTimer _beat;
        private DispatcherTimer _debounce;
        /// <summary>跟随最新：用户没有主动往上翻时保持贴底。</summary>
        private bool _follow = true;
        private string _cat = "all";
        private string _sig = "";
        private string _ioSig = "";
        private string _lastKey = "";
        private string _lastTail = "";
        private int _lastCount;
        private const int MaxRows = 220;

        private void DebounceReload(Core c)
        {
            if (_debounce == null)
            {
                _debounce = new DispatcherTimer();
                _debounce.Interval = TimeSpan.FromMilliseconds(250);
                _debounce.Tick += delegate { _debounce.Stop(); Reload(C); };
            }
            _debounce.Stop();
            _debounce.Start();
        }

        public override UIElement Build(Core c)
        {
            // 重建（换主题 / 命令面板动作）后 _ioSig 还是上一棵树的值，Reload 会在
            // 第一行就 return：日志页回来是空白、计数也是空，之后新记录只往空面板补一行
            // 却仍显示"共 N 条记录"。缓存必须随视觉树一起作废。
            _ioSig = "";
            _sig = "";
            _lastKey = "";
            _lastTail = "";
            _lastCount = 0;
            Grid root = Ui.PageRoot(BuildHeader(c), BuildBody(c));
            Reload(c);
            return root;
        }

        // ---- 页头 ----------------------------------------------------------
        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            Border fw = new Border();
            fw.Width = 216; fw.Height = 28; fw.CornerRadius = new CornerRadius(14);
            fw.Background = T.S(T.Inset); fw.BorderBrush = T.S(T.LineMid); fw.BorderThickness = new Thickness(1);
            fw.Padding = new Thickness(10, 0, 10, 0); fw.Margin = new Thickness(0, 0, 8, 0);
            Grid fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fg.ColumnDefinitions.Add(new ColumnDefinition());
            System.Windows.Shapes.Path fmag = Ui.Icon(Glyph.Search, 11, T.Dim, 1.35);
            fmag.Margin = new Thickness(0, 0, 6, 0); fmag.IsHitTestVisible = false;
            fg.Children.Add(fmag);
            Grid fi = new Grid(); Grid.SetColumn(fi, 1);

            _filter = new TextBox();
            _filter.FontSize = T.FsSmall; _filter.FontFamily = T.FMono;
            _filter.Foreground = T.S(T.Hi); _filter.CaretBrush = T.S(T.Accent);
            _filter.Background = Brushes.Transparent; _filter.BorderThickness = new Thickness(0);
            _filter.VerticalContentAlignment = VerticalAlignment.Center;
            _filter.TextChanged += delegate
            {
                if (_filterPlaceholder != null)
                    _filterPlaceholder.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                DebounceReload(C);
            };
            _filter.GotKeyboardFocus += delegate { fw.BorderBrush = T.S(T.AccentA(0x88)); fmag.Stroke = T.S(T.Accent); };
            _filter.LostKeyboardFocus += delegate { fw.BorderBrush = T.S(T.LineMid); fmag.Stroke = T.S(T.Dim); };
            fi.Children.Add(_filter);

            _filterPlaceholder = Ui.Mono("\u8FC7\u6EE4\uFF1A\u8FDB\u7A0B\u540D / \u52A8\u4F5C / \u7ED3\u679C", T.FsSmall, T.Mute);
            _filterPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            _filterPlaceholder.IsHitTestVisible = false;
            fi.Children.Add(_filterPlaceholder);
            fg.Children.Add(fi); fw.Child = fg;
            right.Children.Add(fw);

            Btn exportBtn = new Btn("\u5BFC\u51FA\u8BCA\u65AD\u62A5\u544A", BtnKind.Quiet);
            exportBtn.Height = T.BtnH;
            exportBtn.Margin = new Thickness(0, 0, 8, 0);
            exportBtn.Click += delegate { ExportDiagReport(C != null ? C : c); };
            right.Children.Add(exportBtn);

            Btn open = new Btn("\u6253\u5F00\u76EE\u5F55", BtnKind.Ghost);
            open.Height = T.BtnH;
            open.Click += delegate { OpenAuditDir(); };
            right.Children.Add(open);

            return Ui.PageHeader("AUDIT TRAIL / 11", "\u5BA1\u8BA1\u65E5\u5FD7", "\u6BCF\u6B21\u5E94\u7528\u3001\u8FD8\u539F\u3001\u4EB2\u548C\u6027\u8C03\u6574\u90FD\u4F1A\u7559\u4E0B\u4E00\u6761\u53EA\u8FFD\u52A0\u7684\u8BB0\u5F55", right);
        }

        // ---- 主体 ----------------------------------------------------------
        private UIElement BuildBody(Core c)
        {
            Ui.SegTabItem[] catItems = new Ui.SegTabItem[]
            {
                new Ui.SegTabItem("all", "\u5168\u90E8", "\u663E\u793A\u5168\u90E8\u5BA1\u8BA1\u8BB0\u5F55"),
                new Ui.SegTabItem("action", "\u52A8\u4F5C\u5199\u5165", "\u8C03\u4F18\u5199\u5165\u3001\u8FD8\u539F\u3001\u6863\u6848\u4E0E\u4EB2\u548C\u5EA6\u64CD\u4F5C"),
                new Ui.SegTabItem("daemon", "\u5B88\u62A4\u8109\u51B2", "\u6E38\u620F\u5B88\u62A4\u6302\u8F7D\u3001\u64A4\u79BB\u4E0E\u4F1A\u8BDD\u6218\u62A5"),
                new Ui.SegTabItem("warn", "\u544A\u8B66\u5931\u8D25", "\u56DE\u6EDA\u3001\u62D2\u7EDD\u3001\u5F02\u5E38\u4E0E\u5931\u8D25\u4E8B\u4EF6")
            };
            _catTabs = new Ui.SegTabs(catItems, CatIndex(_cat));
            _catTabs.Changed += delegate
            {
                _cat = _catTabs.SelectedKey;
                Reload(C != null ? C : c);
            };

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _path = Ui.Mono("", T.FsNano, T.Mute);
            _path.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(_path);
            _count = Ui.Mono("", T.FsNano, T.Mute);
            _count.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_count, 1);
            top.Children.Add(_count);

            StackPanel headBlock = new StackPanel();
            headBlock.Children.Add(_catTabs);
            headBlock.Children.Add(Gap(10));
            headBlock.Children.Add(top);
            headBlock.Children.Add(Gap(10));
            headBlock.Children.Add(BuildColumnHead());
            headBlock.Children.Add(Gap(6));

            _lines = new StackPanel();
            // 用 ThinScroll 而不是裸 ScrollViewer：裸的会画出系统滚动条
            // （浅色主题下是一根白色竖条，在全黑界面里非常刺眼），
            // 而 ThinScroll 自带 3px 滑块、滚轮到顶的撞墙反馈与键盘滚动。
            _thin = new ThinScroll(_lines);
            _scroll = _thin.View;
            // 关键：Hidden 只是不显示滚动条，内容仍按"无限宽"测量 —— 于是每一行的
            // TextTrimming 永远不会生效，长行被视口硬切在半个字上（看起来像渲染坏了）。
            // Disabled 才会把内容宽度钉到视口宽，省略号才有机会出现。
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            // 页面整体装在外壳的 ThinScroll 里，于是星号行在这里拿不到"剩余高度"——
            // 它被无限高度测量、退化成内容高度，220 行会撑出 4000px 的页面，
            // 而内层滚动条永远不出现（两层滚动嵌套，读起来极差）。
            // 所以显式给一个视口预算，并在窗口尺寸变化时重算。
            _thin.MinHeight = 240;
            // 先按窗口高度给一个初值：离屏渲染时 Loaded 不会触发（没有 PresentationSource），
            // 只靠 Loaded 的话截图里永远看不到页脚。真实运行里 SizeChanged 会再校准。
            double h0 = (C != null && C.Shell != null) ? C.Shell.Height : 900;
            if (h0 > ChromeHeight + 240) _thin.MaxHeight = h0 - ChromeHeight;
            _thin.Loaded += delegate { FitToViewport(); };
            _thin.SizeChanged += delegate { FitToViewport(); };
            _scroll.ScrollChanged += delegate (object s, ScrollChangedEventArgs e)
            {
                // Rev130 修复：内容首次载入或增行导致 ExtentHeight 变化时不能把 _follow 误判为 false；
                // 若此时处于跟随态则立即贴底，确保打开审计日志直接看到最新记录。
                if (Math.Abs(e.ExtentHeightChange) > 0.5 || Math.Abs(e.ViewportHeightChange) > 0.5)
                {
                    if (_follow) _scroll.ScrollToEnd();
                    return;
                }
                _follow = (_scroll.ExtentHeight - _scroll.ViewportHeight - _scroll.VerticalOffset) < 12;
            };

            // 凹陷底不画 1px 描边：描边会把行内容的起点整体右移 1px，
            // 表头（在滚动区外）就再也对不齐了。这里只用底色区分区域。
            Border box = new Border();
            box.Background = T.S(T.Inset);
            box.CornerRadius = new CornerRadius(T.RadiusSm);
            box.Padding = new Thickness(PadL, 8, PadR, 8);
            box.Child = _thin;

            Grid shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.Children.Add(headBlock);
            Grid.SetRow(box, 1);
            shell.Children.Add(box);

            // 页脚：被吞掉的异常。它平时是一行灰字（0 次），一旦有数就变成琥珀色并
            // 把 tag / 消息 / 首次堆栈放进 ToolTip。这样"某处静默失败"终于有地方看得见，
            // 而不用去翻上百处 catch。
            _diag = Ui.Mono("", T.FsNano, T.Mute);
            _diag.Margin = new Thickness(0, 8, 0, 0);
            _diag.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(_diag, 2);
            shell.Children.Add(_diag);

            return Ui.Panel(shell, T.PadPanel);
        }

        /// <summary>表头与数据行共用的列模板。</summary>
        private static Grid LineGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTime) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColLevel) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            return g;
        }

        private static Grid BuildColumnHead()
        {
            Grid row = LineGrid();
            row.Height = 20;
            row.Margin = new Thickness(PadL, 0, PadR, 0);
            Head(row, "TIME", 0);
            Head(row, "LEVEL", 1);
            Head(row, "EVENT", 2);
            return row;
        }

        private static void Head(Grid row, string text, int col)
        {
            TextBlock t = Ui.Label(text, T.Mute);
            t.FontSize = T.FsNano;
            t.Opacity = 0.75;
            Grid.SetColumn(t, col);
            row.Children.Add(t);
        }

        /// <summary>自检钩子：当前渲染出的日志行数。</summary>
        public int LineCount { get { return _lines == null ? 0 : _lines.Children.Count; } }

        // ---- 筛选与导出 ------------------------------------------------------
        private static readonly string[] CatKeys = { "all", "action", "daemon", "warn" };
        private static readonly string[] CatNames = { "\u5168\u90E8", "\u52A8\u4F5C\u5199\u5165", "\u5B88\u62A4\u8109\u51B2", "\u544A\u8B66\u5931\u8D25" };

        private static int CatIndex(string cat)
        {
            int idx = Array.IndexOf(CatKeys, cat);
            return idx < 0 ? 0 : idx;
        }

        private void ClearFilter()
        {
            _cat = "all";
            if (_catTabs != null) _catTabs.Select(0, false);
            if (_filter != null) _filter.Text = "";
            if (_filterPlaceholder != null) _filterPlaceholder.Visibility = Visibility.Visible;
            Reload(C);
        }

        private void OpenAuditDir()
        {
            if (C == null || C.Engine == null || C.Engine.Ledger == null) return;
            try { System.Diagnostics.Process.Start("explorer.exe", C.Engine.Ledger.DirectoryPath); }
            catch (Exception ex) { if (C.Shell != null) C.Shell.Toast(ex.Message, false); }
        }

        private void ExportDiagReport(Core c)
        {
            string tmp = null;
            try
            {
                string dir = (c != null && c.Engine != null && c.Engine.Ledger != null && !string.IsNullOrEmpty(c.Engine.Ledger.DirectoryPath))
                    ? c.Engine.Ledger.DirectoryPath
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOptimizer");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "diag-export-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                tmp = path + ".tmp";

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("GameOptimizer Pro \u8BCA\u65AD\u62A5\u544A  (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ")");
                if (c != null)
                {
                    string ad = c.Engine != null && c.Engine.Ledger != null ? c.Engine.Ledger.DirectoryPath : "?";
                    int lc = c.Engine != null && c.Engine.Ledger != null ? c.Engine.Ledger.Entries.Count : 0;
                    sb.AppendLine("Elevated=" + c.Elevated + "  LedgerEntries=" + lc + "  AuditDir=" + ad);
                    if (c.Topo != null) sb.AppendLine("Topology=" + c.Topo.Cores + "C/" + c.Topo.Logical + "T Ccds=" + c.Topo.Ccds + " Hybrid=" + c.Topo.Hybrid);
                }
                sb.AppendLine("Diag.Total=" + Diag.Total + "  Diag.Tags=" + Diag.Tags);
                List<string> diagRows = Diag.Rows();
                for (int i = 0; i < diagRows.Count; i++) sb.AppendLine("  " + diagRows[i]);
                AppendHealth(sb);
                sb.AppendLine().AppendLine("-- \u6700\u65B0\u5BA1\u8BA1\u6458\u8981 (cat=" + _cat + ") --");

                string[] files;
                IoSignature(c != null ? c : C, "", out files);
                List<string> lines = ReadLines(files);
                if (_cat != "all") FilterCat(lines, _cat);
                for (int i = Math.Max(0, lines.Count - 60); i < lines.Count; i++) sb.AppendLine(lines[i]);

                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                if (c != null && c.Shell != null) c.Shell.Toast("\u5DF2\u5BFC\u51FA\u8BCA\u65AD\u62A5\u544A\uFF1A" + path, true);
            }
            catch (Exception ex)
            {
                if (tmp != null && File.Exists(tmp)) { try { File.Delete(tmp); } catch (Exception ce) { Diag.Fail("log.export.tmp", ce); } }
                Diag.Fail("log.export", ex);
                if (c != null && c.Shell != null) c.Shell.Toast("\u5BFC\u51FA\u5931\u8D25\uFF1A" + ex.Message, false);
            }
        }

        // ---- 读取 ----------------------------------------------------------
        private void Reload(Core c)
        {
            if (_lines == null || c == null) return;
            _path.Text = "\u5BA1\u8BA1\u76EE\u5F55\uFF1A" + c.Engine.Ledger.DirectoryPath;

            string key = _filter == null ? "" : _filter.Text.Trim();
            string[] files;
            string ioSig = IoSignature(c, key, out files);
            if (ioSig == _ioSig) return;
            _ioSig = ioSig;

            List<string> lines = ReadLines(files);
            if (key.Length > 0) FilterText(lines, key);
            if (_cat != "all") FilterCat(lines, _cat);

            _count.Text = lines.Count > MaxRows ? ("\u6700\u8FD1 " + MaxRows + " / " + lines.Count + " \u6761\u8BB0\u5F55") : (lines.Count + " \u6761\u8BB0\u5F55");
            RefreshDiag();

            string mode = key + "|" + _cat;
            string sig = lines.Count + "|" + mode + "|" + (lines.Count > 0 ? lines[lines.Count - 1] : "");
            if (sig == _sig) return;
            _sig = sig;
            Render(lines, mode);
        }

        /// <summary>IO 指纹：文件数 + 每个文件的长度与修改时间 + 过滤条件。命中就不读文件。</summary>
        private string IoSignature(Core c, string key, out string[] files)
        {
            files = new string[0];
            try
            {
                if (Directory.Exists(c.Engine.Ledger.DirectoryPath))
                {
                    files = Directory.GetFiles(c.Engine.Ledger.DirectoryPath, "audit-*.log");
                    Array.Sort(files);
                }
            }
            catch (Exception ex) { Diag.Fail("log.files", ex); }

            StringBuilder mb = new StringBuilder();
            mb.Append(files.Length).Append('|').Append(key).Append('|').Append(_cat);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    FileInfo fi = new FileInfo(files[i]);
                    mb.Append('|').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
                }
                catch (Exception ex) { Diag.Fail("log.stat", ex); }
            }
            return mb.ToString();
        }

        /// <summary>尾读每个文件最后 400 行（最多 64 KB）：日志只追加，读尾巴就够。</summary>
        private static List<string> ReadLines(string[] files)
        {
            List<string> lines = new List<string>();
            for (int f = 0; f < files.Length; f++)
            {
                try
                {
                    string[] all = TailLines(files[f], 65536);
                    int from = Math.Max(0, all.Length - 400);
                    for (int i = from; i < all.Length; i++)
                    {
                        string ln = all[i] != null ? all[i].TrimEnd('\r') : "";
                        if (!string.IsNullOrWhiteSpace(ln)) lines.Add(ln);
                    }
                }
                catch (Exception ex) { Diag.Fail("log.tail", ex); }
            }
            return lines;
        }

        /// <summary>就地过滤（不改容量、不产生第二个 List）：把留下来的行往前压。</summary>
        private static void FilterText(List<string> lines, string key)
        {
            int w = 0;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) lines[w++] = lines[i];
            if (w < lines.Count) lines.RemoveRange(w, lines.Count - w);
        }

        private static void FilterCat(List<string> lines, string cat)
        {
            int w = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string time, kind, text;
                ParseLine(lines[i], out time, out kind, out text);
                if (!MatchesCat(kind, text, cat)) continue;
                lines[w++] = lines[i];
            }
            if (w < lines.Count) lines.RemoveRange(w, lines.Count - w);
        }

        private static bool MatchesCat(string kind, string text, string cat)
        {
            if (cat == "action")
                return kind == "OK" || kind == "APPLY" || kind == "CMD" || kind == "REVERT" || kind == "ROLLBACK"
                    || kind == "PROFILE" || kind == "AFFINITY" || kind == "MEM" || kind == "PROC" || kind == "PRIO"
                    || kind == "ECOQOS" || kind == "POWER" || kind == "SAVE-FAIL" || kind == "BATCH-STOP";
            if (cat == "daemon")
                return kind == "DAEMON" || kind == "SESSION";
            if (cat == "warn")
            {
                if (LevelOf(kind) >= LvWarn) return true;
                int f2 = text.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase);
                if (f2 >= 0 && (f2 + 4 >= text.Length || text[f2 + 4] != '=')) return true;
                if (text.IndexOf("aborted", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (text.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (text.IndexOf("ok=False", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (text.IndexOf("\u5931\u8D25", StringComparison.Ordinal) >= 0) return true;
                if (text.IndexOf("\u672A\u6B63\u5E38\u653E\u5C3E", StringComparison.Ordinal) >= 0 || text.IndexOf("\u672A\u6B63\u5E38\u6536\u5C3E", StringComparison.Ordinal) >= 0) return true;
                int fi = text.IndexOf("fail=", StringComparison.OrdinalIgnoreCase);
                return fi >= 0 && fi + 5 < text.Length && text[fi + 5] >= '1' && text[fi + 5] <= '9';
            }
            return true;
        }

        // ---- 渲染 ----------------------------------------------------------
        private void Render(List<string> lines, string mode)
        {
            string tail = lines.Count > 0 ? lines[lines.Count - 1] : "";
            int added = lines.Count - _lastCount;
            bool appendable = mode == _lastKey && added > 0 && added <= 60
                              && _lastCount > 0 && _lastCount <= lines.Count
                              && lines[_lastCount - 1] == _lastTail;

            _lastKey = mode;
            _lastCount = lines.Count;
            _lastTail = tail;

            if (appendable)
            {
                for (int i = lines.Count - added; i < lines.Count; i++) _lines.Children.Add(Row(lines[i]));
                while (_lines.Children.Count > MaxRows) _lines.Children.RemoveAt(0);
                Follow();
                return;
            }

            _lines.Children.Clear();
            if (lines.Count == 0) { _lines.Children.Add(EmptyState(mode)); return; }

            int start = Math.Max(0, lines.Count - MaxRows);
            for (int i = start; i < lines.Count; i++) _lines.Children.Add(Row(lines[i]));
            Follow();
        }

        private UIElement EmptyState(string mode)
        {
            string key = mode;
            int cut = mode.LastIndexOf('|');
            if (cut >= 0) key = mode.Substring(0, cut);

            UIElement box;
            if (key.Length == 0 && _cat == "all")
            {
                box = Ui.Empty(Glyph.Log, "\u8FD8\u6CA1\u6709\u4EFB\u4F55\u52A8\u4F5C\u88AB\u8BB0\u5F55",
                    "\u5E94\u7528\u4E00\u4E2A\u8C03\u4F18\u52A8\u4F5C\u3001\u5207\u6362\u4E00\u6B21\u7535\u6E90\u65B9\u6848\u3001\u6216\u8005\u6302\u8F7D\u4E00\u4E2A\u6E38\u620F\u6863\u6848\uFF0C\u8FD9\u91CC\u5C31\u4F1A\u7ACB\u523B\u51FA\u73B0\u7B2C\u4E00\u884C\u3002",
                    T.Mute, ActionBtn("\u6253\u5F00\u5BA1\u8BA1\u76EE\u5F55", false));
            }
            else
            {
                string why = key.Length > 0 ? "\u5173\u952E\u8BCD\u300C" + key + "\u300D" : "";
                if (_cat != "all") why += (why.Length > 0 ? " + " : "") + CatNames[CatIndex(_cat)];
                box = Ui.Empty(Glyph.Log, "\u6CA1\u6709\u5339\u914D\u7684\u8BB0\u5F55",
                    "\u5F53\u524D\u7B5B\u9009\uFF1A" + why + "\u3002\u6E05\u7A7A\u7B5B\u9009\u5373\u53EF\u770B\u5230\u5168\u90E8\u8BB0\u5F55\u3002",
                    T.Mute, ActionBtn("\u6E05\u7A7A\u7B5B\u9009", true));
            }
            box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 46, 0, 46));
            return box;
        }

        private Btn ActionBtn(string label, bool clear)
        {
            Btn b = new Btn(label, clear ? BtnKind.Accent : BtnKind.Ghost);
            b.Height = T.BtnH;
            if (clear) b.Click += delegate { ClearFilter(); };
            else b.Click += delegate { OpenAuditDir(); };
            return b;
        }

        private ScrollViewer _outerScroll;
        /// <summary>页头 + 分类条 + 列头 + 页脚 + 内外边距合计占掉的高度。</summary>
        private const double ChromeHeight = 316;

        /// <summary>把日志区高度钉在外壳滚动容器的视口上，让内层滚动条真的可用。</summary>
        private void FitToViewport()
        {
            if (_scroll == null || C == null || C.Shell == null) return;

            if (_outerScroll == null)
            {
                System.Windows.DependencyObject d = _scroll;
                while (d != null)
                {
                    ScrollViewer sv = d as ScrollViewer;
                    if (sv != null && sv != _scroll)
                    {
                        _outerScroll = sv;
                        sv.SizeChanged += delegate { FitToViewport(); };
                        break;
                    }
                    d = System.Windows.Media.VisualTreeHelper.GetParent(d);
                }
            }

            // 有外壳视口就用视口（窗口缩放会跟着变）；还没有就退回窗口高度估算。
            double h = _outerScroll != null && _outerScroll.ViewportHeight > 0
                ? _outerScroll.ViewportHeight
                : C.Shell.ActualHeight;
            if (h < 400) return;

            double box = h - ChromeHeight;
            if (box < 240) box = 240;
            if (Math.Abs(_thin.MaxHeight - box) > 1) _thin.MaxHeight = box;
        }

        /// <summary>
        /// 内部降级计数。Diag 是进程级静态表，稳态零分配；这里只在数字变化时才写文字
        /// （2 秒一拍，若每次都赋值会白白触发一次整行重排）。
        /// </summary>
        private int _diagShown = -1;
        private int _healthShown = -1;

        /// <summary>把"在下行"的采样层写进提示：层名 / 失败阶段 / 状态码 / 下次重试。</summary>
        private void AppendHealth(StringBuilder sb)
        {
            AppendBoard(sb, C == null || C.Perf == null ? null : C.Perf.Health);
            AppendBoard(sb, C == null || C.Gpu == null ? null : C.Gpu.Health);
        }

        private static void AppendBoard(StringBuilder sb, SampleHealthBoard board)
        {
            if (board == null || board.DownCount <= 0) return;
            sb.Append("-- \u91C7\u6837\u964D\u7EA7 --");
            List<string> rows = board.Rows();
            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append('\n').Append(rows[i]);
            }
        }

        private void RefreshDiag()
        {
            if (_diag == null) return;
            int n = Diag.Total;

            // 采样层的降级与"被吞掉的异常"是两件事：
            // 前者是"这台机器上某个计数器不好使了"（有状态码、有下次重试），
            // 后者是"代码里某处 catch 掉了没上报"。老面板只显示后者，
            // 于是 GPU 打不开时用户看到的是"内部降级 0 次"——一个会说谎的 0。
            int down = 0;
            if (C != null && C.Perf != null && C.Perf.Health != null) down += C.Perf.Health.DownCount;
            if (C != null && C.Gpu != null && C.Gpu.Health != null) down += C.Gpu.Health.DownCount;
            if (n == _diagShown && down == _healthShown) return;
            _diagShown = n;
            _healthShown = down;

            string tip;
            if (n == 0)
            {
                _diag.Text = "\u5185\u90E8\u964D\u7EA7 0 \u6B21 \u00B7 \u6CA1\u6709\u4EFB\u4F55\u5F02\u5E38\u88AB\u541E\u6389";
                _diag.Foreground = T.S(T.Mute);
                tip = "\u6BCF\u4E00\u5904 catch \u90FD\u5728\u8FD9\u91CC\u8BA1\u6570\uFF1A0 \u8868\u793A\u672C\u6B21\u4F1A\u8BDD\u91CC\u6CA1\u6709\u53D1\u751F\u9759\u9ED8\u5931\u8D25\u3002";
            }
            else if (down > 0)
            {
                _diag.Text = "\u91C7\u6837\u964D\u7EA7 " + down + " \u5C42 \u00B7 \u5185\u90E8\u964D\u7EA7 " + n + " \u6B21 \u00B7 \u60AC\u505C\u67E5\u770B";
                _diag.Foreground = T.S(T.Warn);
                List<string> rows = Diag.Rows();
                StringBuilder sb = new StringBuilder();
                AppendHealth(sb);
                int take = rows.Count > 10 ? 10 : rows.Count;
                for (int i = 0; i < take; i++)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(rows[i]);
                }
                if (rows.Count > take) sb.Append("\n\u2026 \u8FD8\u6709 ").Append(rows.Count - take).Append(" \u7C7B");
                tip = sb.ToString();
            }
            else
            {
                _diag.Text = "\u5185\u90E8\u964D\u7EA7 " + n + " \u6B21 \u00B7 " + Diag.Tags + " \u7C7B \u00B7 \u60AC\u505C\u67E5\u770B";
                _diag.Foreground = T.S(T.Warn);
                List<string> rows = Diag.Rows();
                StringBuilder sb = new StringBuilder();
                AppendHealth(sb);
                int take = rows.Count > 10 ? 10 : rows.Count;
                for (int i = 0; i < take; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(rows[i]);
                }
                if (rows.Count > take) sb.Append("\n\u2026 \u8FD8\u6709 ").Append(rows.Count - take).Append(" \u7C7B");
                tip = sb.ToString();
            }

            // 只挂一次提示：Tip.Attach 每调一次就多三个事件处理器，而这里的数字
            // 在一次会话里会变几十次。之后改内容 TextBlock 的文案即可。
            ToolTip tipObj = _diag.ToolTip as ToolTip;
            if (tipObj == null) { Tip.Attach(_diag, tip); return; }
            TextBlock content = tipObj.Content as TextBlock;
            if (content != null) content.Text = tip;
        }

        /// <summary>把视口贴到最新一行（等布局算完再滚，否则 ExtentHeight 还是旧值）。</summary>
        private void Follow()
        {
            if (!_follow || _scroll == null) return;
            _scroll.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                try { if (_follow) _scroll.ScrollToEnd(); } catch { /* 布局未完成时滚不动：下一拍再滚一次 */ }
            }));
        }

        // ---- 单行 ----------------------------------------------------------
        /// <summary>
        /// 一行 = 时间 + 级别徽标 + 正文，三列与表头同源。
        /// 正文统一用正文色：级别已经由徽标表达，再给正文染色就是同一句话说两遍，
        /// 而一屏里出现三种颜色的字之后，读者就不再去找颜色了。
        /// </summary>
        private static UIElement Row(string line)
        {
            string time, kind, text;
            ParseLine(line, out time, out kind, out text);

            Grid g = LineGrid();
            g.MinHeight = RowH;
            g.Background = Brushes.Transparent;

            TextBlock t = Ui.Mono(time, T.FsNano, T.Mute);
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t);

            if (kind.Length > 0)
            {
                Border tag = Ui.Tag(kind, LevelTint(LevelOf(kind)));
                tag.HorizontalAlignment = HorizontalAlignment.Left;
                tag.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(tag, 1);
                g.Children.Add(tag);
            }

            // 正文用 FsNano(10)：审计行最长约 165 字符，11 号会在末尾把
            // "-> GpuPreference=2" 这类结果截掉，而那正是这一行要读的东西。
            TextBlock m = Ui.Mono(text, T.FsNano, T.Text);
            m.TextWrapping = TextWrapping.NoWrap;
            m.TextTrimming = TextTrimming.CharacterEllipsis;
            m.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(m, 2);
            g.Children.Add(m);

            // 不给每一行挂 ToolTip：Tip.Attach 会为每个元素建一整套 ToolTip 实例
            // （模板 + 内容 TextBlock + 三个事件委托），220 行就是 220 套，
            // 而其中绝大多数永远不会被悬停。首建实测因此多花 100ms 级的时间。
            return g;
        }

        /// <summary>审计行格式："yyyy-MM-dd HH:mm:ss  LEVEL  body"。解析失败就整行当正文。</summary>
        private static void ParseLine(string line, out string time, out string kind, out string text)
        {
            time = "";
            kind = "";
            text = line == null ? "" : line;
            if (line == null || line.Length <= 20 || line[19] != ' ') return;

            time = line.Substring(11, 8);                       // HH:mm:ss
            string rest = line.Substring(20).TrimStart();
            int sp = rest.IndexOf(' ');
            if (sp <= 0 || sp > 12) { text = rest; return; }

            string tok = rest.Substring(0, sp);
            if (!IsToken(tok)) { text = rest; return; }
            kind = tok;
            text = rest.Substring(sp + 1).TrimStart();
        }

        /// <summary>级别词：只认纯字母/连字符，宁可漏判，也不要把正文首词吃掉。</summary>
        private static bool IsToken(string tok)
        {
            if (tok.Length == 0 || tok.Length > 12) return false;
            for (int i = 0; i < tok.Length; i++)
            {
                char ch = tok[i];
                bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '-' || ch == '_';
                if (!ok) return false;
            }
            return true;
        }

        private static int LevelOf(string kind)
        {
            if (kind == "OK" || kind == "APPLY") return LvOk;
            if (kind == "FAIL" || kind == "ERROR" || kind == "ABORT" || kind == "SAVE-FAIL") return LvBad;
            if (kind == "REVERT" || kind == "ROLLBACK" || kind == "MISMATCH" || kind == "BATCH-STOP") return LvWarn;
            return LvNeutral;
        }

        private static uint LevelTint(int level)
        {
            if (level == LvOk) return T.Ok;
            if (level == LvBad) return T.Bad;
            if (level == LvWarn) return T.Warn;
            return T.Dim;
        }

        /// <summary>尾读文件末 maxBytes 字节并按行切分；非从头读时丢弃可能不完整的首行。</summary>
        private static string[] TailLines(string path, int maxBytes)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                int take = (int)Math.Min(len, maxBytes);
                if (take <= 0) return new string[0];
                fs.Seek(len - take, SeekOrigin.Begin);
                byte[] buf = new byte[take];
                int read = 0;
                while (read < take)
                {
                    int n = fs.Read(buf, read, take - read);
                    if (n <= 0) break;
                    read += n;
                }
                string text = Encoding.UTF8.GetString(buf, 0, read);
                if (len > take)
                {
                    int nl = text.IndexOf('\n');
                    if (nl >= 0) text = text.Substring(nl + 1);
                }
                return text.Split('\n');
            }
        }

        public override void OnEnter()
        {
            Reload(C);
            if (_beat == null)
            {
                _beat = new DispatcherTimer();
                _beat.Interval = TimeSpan.FromSeconds(2);
                _beat.Tick += delegate { Reload(C); };
            }
            _beat.Start();
        }

        public override void OnLeave()
        {
            if (_beat != null) _beat.Stop();
            if (_debounce != null) _debounce.Stop();
        }
    }
}