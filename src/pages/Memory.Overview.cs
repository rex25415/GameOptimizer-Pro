// ============================================================================
//  Memory.Overview.cs — 04 内存与提交：顶部四格读数带 + 提交判词（Memory 分片）
//  为什么切：四格的量纲、变化量基准、阈值→判词是这一页唯一的"结论性"代码，
//  留在页根里改一个阈值要滚半页，也没法对着"哪些格子吃哪个字段"逐条核对。
//
//  Rev86：数值从"一个大 hero + 三格次级"改成四格等权的读数带。
//  四格回答的是同一层的四个问题（已用 / 可用 / 待机 / 已修改），就该同字号、同基线、
//  同结构：值 + 单位 + 变化量 → 趋势带 → 量条 → 注脚。提交压力降为一行判词，
//  它给的是结论（够不够），不是读数。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;

namespace Go.Pages
{
    public partial class Memory : Page
    {
        /// <summary>一格活体读数：所有会被每秒重写的控件都在这里复用。</summary>
        private sealed class MemCell
        {
            public TextBlock Val;
            public TextBlock Note;
            public DeltaChip Delta;
            public Sparkline Trend;
            public SegBar Bar;
            public bool UpIsGood;
            // 读数与注脚的"上次显示刻度"：变了才拼字符串，稳态下这一格 0 次分配。
            public int ValKey = int.MinValue;
            public long NoteKey = long.MinValue;
        }

        // 变化量基准：9 拍（约 9 秒）前。比 1 拍前稳（不会因为一次回收抖动乱跳），
        // 又比 30 拍前贴近当下。24 格环形足够覆盖一屏趋势带。
        private const int TrendN = 24;
        private const int TrendBack = 9;

        private readonly double[] _hUsed = new double[TrendN];
        private readonly double[] _hAvail = new double[TrendN];
        private readonly double[] _hStby = new double[TrendN];
        private readonly double[] _hMod = new double[TrendN];
        private int _hHead, _hCount;

        // 变化量只在"刻度真的变了"时才改控件：4 格 × 1Hz 的稳态是 0 次文本重排。
        private int _dUsed = int.MinValue, _dAvail = int.MinValue;
        private int _dStby = int.MinValue, _dMod = int.MinValue;

        // ====================================================================
        //  Rev130 2×2 四象限物理页 Bento 读数矩阵 + 提交判词
        // ====================================================================
        private UIElement BuildReadoutBand(Core c)
        {
            StackPanel body = new StackPanel();
            body.Children.Add(Ui.PanelHead("内存读数", "MEMORY READOUT · PHYSICAL PAGES",
                Src("GLOBALMEMORYSTATUSEX · NtQuerySystemInformation")));
            body.Children.Add(Gap(10));

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition());

            _cellUsed = BandCell(grid, 0, 0, "已用", "IN USE", T.Accent, "GB", false);
            _cellAvail = BandCell(grid, 2, 0, "可用", "AVAILABLE", T.Ok, "GB", true);
            _cellStby = BandCell(grid, 0, 2, "待机", "STANDBY", T.SeriesA, "GB", true);
            _cellMod = BandCell(grid, 2, 2, "已修改", "MODIFIED", T.SeriesB, "MB", false);
            body.Children.Add(grid);

            body.Children.Add(Gap(10));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(8));
            body.Children.Add(BuildVerdict());
            return Ui.Panel(body, T.PadPanel);
        }

        private MemCell BandCell(Grid host, int col, int row, string cn, string en, uint tint, string unit, bool upIsGood)
        {
            MemCell c = new MemCell();
            c.UpIsGood = upIsGood;
            c.Val = Ui.Num("\u2014", T.FsNumL, T.Hi);
            c.Delta = new DeltaChip();
            c.Delta.Set(0, "", upIsGood);
            c.Trend = new Sparkline(32);
            c.Trend.Height = 20;
            c.Trend.Tint = tint;
            c.Trend.Opacity = 0.92;
            c.Bar = new SegBar(0);
            c.Bar.Height = 3;
            c.Bar.Tint = tint;
            c.Note = Ui.Mono("等待采样", T.FsNano, T.Mute);

            Border tile = Ui.GlassTile(Ui.Stat(cn + " " + en, Head(c.Val, unit, c.Delta), Trend(c.Trend, c.Bar), c.Note), 12, tint);
            Grid.SetColumn(tile, col);
            Grid.SetRow(tile, row);
            host.Children.Add(tile);
            return c;
        }

        /// <summary>
        /// 读数头：数值 · 单位 · 变化量挤同一行。
        /// 变化量单独占一行会把每格撑高 21px——四格一起高，整条读数带就多出一条空白。
        /// 仪表的惯例本来就是"读数旁边挂一个偏差"，不是"读数下面另起一行"。
        /// 这段与 Overview.Hero 同构；共享文件 Ui.Instrument 里没有它，只能局部复刻。
        /// </summary>
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

        /// <summary>格子中段：趋势带在上、量条压底，四格共用一条左对齐基线。</summary>
        private static StackPanel Trend(Sparkline sp, SegBar bar)
        {
            StackPanel box = new StackPanel();
            box.Children.Add(sp);
            bar.Margin = new Thickness(0, 7, 0, 0);
            box.Children.Add(bar);
            return box;
        }

        // ====================================================================
        //  提交判词（读数带的地脚）
        // ====================================================================
        private UIElement BuildVerdict()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel word = new StackPanel();
            word.Orientation = Orientation.Horizontal;
            word.VerticalAlignment = VerticalAlignment.Center;

            _verdictDot = new Border();
            _verdictDot.Width = 6;
            _verdictDot.Height = 6;
            _verdictDot.CornerRadius = new CornerRadius(3);
            _verdictDot.Background = T.S(T.Mute);
            _verdictDot.VerticalAlignment = VerticalAlignment.Center;
            _verdictDot.Margin = new Thickness(0, 0, 8, 0);
            word.Children.Add(_verdictDot);

            _verdictWord = Ui.Tx("未测量", T.FsBody, T.Mute, T.FCn);
            _verdictWord.FontWeight = FontWeights.SemiBold;
            word.Children.Add(_verdictWord);
            g.Children.Add(word);

            _verdictLine = Ui.Tx("等待采样", T.FsSmall, T.Dim, T.FCn);
            _verdictLine.TextTrimming = TextTrimming.CharacterEllipsis;
            _verdictLine.TextWrapping = TextWrapping.NoWrap;
            _verdictLine.VerticalAlignment = VerticalAlignment.Center;
            _verdictLine.Margin = new Thickness(14, 0, 20, 0);
            Grid.SetColumn(_verdictLine, 1);
            g.Children.Add(_verdictLine);

            _commitLine = Ui.Mono("COMMIT — / — GB", T.FsNano, T.Mute);
            _commitLine.VerticalAlignment = VerticalAlignment.Center;
            _commitLine.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_commitLine, 2);
            g.Children.Add(_commitLine);

            Tip.Attach(word, "提交 = 系统已经答应给程序、但还没真正落到物理页的内存量。\n" +
                             "触到上限时新申请会失败（或系统扩容页面文件）。\n" +
                             "判词阈值：≥90% 危险 / ≥75% 偏紧 / 其余充足。");
            return g;
        }

        // ====================================================================
        //  读数写入
        // ====================================================================
        private void ApplyBand(double total, double used, double avail, double standby, double modified, bool memListOk)
        {
            if (_cellUsed == null) return;

            // 每个读数都按整数刻度比较后再生效：1 Hz × 四格 × 十二个数值，
            // 稳态下这里应该一次字符串都不拼、一次 Text 写入都不发生。
            SetGbVal(_cellUsed, used);
            int pct = total > 0 ? (int)Math.Round(used / total * 100) : -1;
            long usedKey = ((long)pct << 32) ^ (memListOk ? (long)Math.Round(modified / 102.4) : -1L);
            if (usedKey != _cellUsed.NoteKey)
            {
                _cellUsed.NoteKey = usedKey;
                _cellUsed.Note.Text = "含已修改 " + (memListOk ? SizeText(modified) : "\u2014") + " · " +
                                      (pct >= 0 ? pct + "%" : "\u2014");
            }

            SetGbVal(_cellAvail, avail);
            long availKey = ((long)Math.Round(Math.Max(0, avail - standby) / 102.4) << 32) ^
                            (memListOk ? (long)Math.Round(standby / 102.4) : -1L);
            if (availKey != _cellAvail.NoteKey)
            {
                _cellAvail.NoteKey = availKey;
                _cellAvail.Note.Text = "空闲 " + Gb(Math.Max(0, avail - standby)) + " + 待机 " +
                                       (memListOk ? Gb(standby) : "—");
            }

            SetKnownVal(_cellStby, memListOk, standby, false);
            SetKnownNote(_cellStby, memListOk, "缓存页 · 可回收", "内存列表查询不可用");
            SetKnownVal(_cellMod, memListOk, modified, true);
            SetKnownNote(_cellMod, memListOk, "已改动 · 待写回磁盘", "内存列表查询不可用");

            // 未测量的格子把量条压暗：空白轨道读作"这一格没有结论"，不是"这里是 0"。
            _cellUsed.Bar.Opacity = 1.0;
            _cellAvail.Bar.Opacity = 1.0;
            _cellStby.Bar.Opacity = memListOk ? 1.0 : 0.35;
            _cellMod.Bar.Opacity = memListOk ? 1.0 : 0.35;
            if (!memListOk)
            {
                _cellStby.Delta.Set(0, "", true);
                _cellMod.Delta.Set(0, "", false);
            }
        }

        /// <summary>四格的趋势 / 变化量。数值本身由 ApplyBand 直接写，这里只喂基线与走势。</summary>
        private void PushTrends(double total, double used, double avail, double standby, double modified, bool memListOk)
        {
            if (_cellUsed == null || total <= 0) return;

            double totalGb = total / 1024.0;
            double usedGb = used / 1024.0;
            double availGb = avail / 1024.0;
            double stbyGb = standby / 1024.0;
            double modGb = modified / 1024.0;

            int back = (_hHead - TrendBack + TrendN) % TrendN;
            bool has = _hCount >= TrendBack;
            double pUsed = has ? _hUsed[back] : usedGb;
            double pAvail = has ? _hAvail[back] : availGb;
            double pStby = has ? _hStby[back] : stbyGb;
            double pMod = has ? _hMod[back] : modGb;

            _hUsed[_hHead] = usedGb;
            _hAvail[_hHead] = availGb;
            _hStby[_hHead] = stbyGb;
            _hMod[_hHead] = modGb;
            _hHead = (_hHead + 1) % TrendN;
            if (_hCount < TrendN) _hCount++;

            _cellUsed.Trend.Push(usedGb / totalGb);
            _cellAvail.Trend.Push(availGb / totalGb);
            _cellUsed.Bar.Value = usedGb / totalGb;
            _cellAvail.Bar.Value = availGb / totalGb;

            SetDelta(_cellUsed, ref _dUsed, has, usedGb - pUsed, "GB", 10);
            SetDelta(_cellAvail, ref _dAvail, has, availGb - pAvail, "GB", 10);

            if (memListOk)
            {
                _cellStby.Trend.Push(stbyGb / totalGb);
                _cellMod.Trend.Push(modGb / totalGb);
                _cellStby.Bar.Value = stbyGb / totalGb;
                _cellMod.Bar.Value = modGb / totalGb;
                SetDelta(_cellStby, ref _dStby, has, stbyGb - pStby, "GB", 10);
                SetDelta(_cellMod, ref _dMod, has, modGb - pMod, "MB", 1);
            }
        }

        /// <summary>GB 读数：0.1 GB 刻度变化才重写（字符串也只在那一刻拼）。</summary>
        private static void SetGbVal(MemCell c, double mb)
        {
            int k = (int)Math.Round(mb / 102.4);
            if (k == c.ValKey) return;
            c.ValKey = k;
            c.Val.Text = (k / 10.0).ToString("0.0");
        }

        /// <summary>MB 读数：整数刻度变化才重写。</summary>
        private static void SetMbVal(MemCell c, double mb)
        {
            int k = (int)Math.Round(mb);
            if (k == c.ValKey) return;
            c.ValKey = k;
            c.Val.Text = Group(k);
        }

        /// <summary>可缺测的读数：有值走刻度比较；缺测时只把 — 写一次。</summary>
        private static void SetKnownVal(MemCell c, bool ok, double mb, bool mbUnit)
        {
            if (ok)
            {
                if (mbUnit) SetMbVal(c, mb);
                else SetGbVal(c, mb);
                return;
            }
            if (c.ValKey == int.MinValue) return;
            c.ValKey = int.MinValue;
            c.Val.Text = "\u2014";
        }

        private static void SetKnownNote(MemCell c, bool ok, string known, string unknown)
        {
            long k = ok ? 1L : 0L;
            if (k == c.NoteKey) return;
            c.NoteKey = k;
            c.Note.Text = ok ? known : unknown;
        }

        /// <summary>
        /// scale=10 表示按 0.1 显示，scale=1 表示整数显示。
        /// 先比较整数刻度，没变就直接返回——稳态下这一格 0 次字符串分配。
        /// </summary>
        private static void SetDelta(MemCell c, ref int last, bool has, double d, string unit, double scale)
        {
            int tick = (int)Math.Round(d * scale);
            int key = has ? tick : int.MinValue;
            if (key == last) return;
            last = key;
            if (!has) { c.Delta.Set(0, "", c.UpIsGood); return; }

            int dir = tick > 0 ? 1 : tick < 0 ? -1 : 0;
            string body = (Math.Abs(tick) / scale).ToString(scale > 1 ? "0.0" : "0") + " " + unit;
            c.Delta.Set(dir, body, c.UpIsGood);
        }

        private void ApplyVerdict(bool cmtOk, double ratio, double head, double commitTotal, double commitLimit)
        {
            // 判词只由"已提交 / 上限"两个刻度决定（余量与比例都是从它们算出来的）：
            // 这两个数没变，整段文字就不该重写。
            long key = cmtOk
                ? (((long)Math.Round(commitTotal / 102.4) << 24) ^ (long)Math.Round(commitLimit / 102.4))
                : -1L;
            if (key == _verdictKey) return;
            _verdictKey = key;

            uint hue = T.Mute;
            string word = "未测量";
            string line = "等待采样";
            if (cmtOk)
            {
                if (ratio >= 0.90) { hue = T.Bad; word = "危险"; line = "余量 " + Gb(head) + " GB：已逼近上限，系统会扩容页面文件或拒绝新申请"; }
                else if (ratio >= 0.75) { hue = T.Warn; word = "偏紧"; line = "余量 " + Gb(head) + " GB：再开几个大程序就可能触顶"; }
                else { hue = T.Ok; word = "充足"; line = "余量 " + Gb(head) + " GB：程序继续申请内存还有空间"; }
            }

            if (_verdictDot != null) _verdictDot.Background = T.S(hue);
            if (_verdictWord != null)
            {
                Set(_verdictWord, word);
                _verdictWord.Foreground = T.S(hue);
            }
            Set(_verdictLine, line);
            Set(_commitLine, cmtOk
                ? "COMMIT " + Gb(commitTotal) + " / " + Gb(commitLimit) + " GB · " + Pct1(ratio)
                : "COMMIT — / — GB");
        }
    }
}
