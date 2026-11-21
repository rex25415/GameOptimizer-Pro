// ============================================================================
//  Ui.Instrument.cs — Rev86 · 读数语言
//
//  设计立场：仪器感不来自装饰，来自三件事——同一基线、同一栅格、同一语义。
//  这个文件把"一个数怎么被读出来"从 11 个页面各自的手工拼装，收敛成 5 个原子：
//      Readout  数值 + 单位（同基线，单位退一档）
//      Delta    变化量（▲/▼ + 语义色：只有"变好/变坏"，没有"上升/下降"）
//      Tag      徽标（描边 / 实底 两档权重，取代原来四种各自为政的胶囊）
//      RowShell 行外壳（左缘 2px 风险色：颜色本身就是字段，不必再写一个字母）
//      Stat     统计格（标签 → 刻度 → 读数 → 趋势 → 注脚，五层同一垂直节奏）
//
//  为什么值得一个独立文件：这些不是"组件库"，是**读法**。散在页面里各写一遍，
//  读者在每个页面都要重新学一次"这个数字带不带单位、徽标是几号字、风险在哪看"。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Go
{
    public static partial class Ui
    {
        // ── 0. 行网格列宽（全应用唯一真源）──────────────────────────────────
        // 三个页面各自写过 110/92/56 与 112/96/56 两套数，跨页差 2~4px。
        // 2px 单看无所谓，但它让"11 页是同一台仪器"这句话不成立——
        // 读者的眼睛在切页时能感到那一点点错位，却说不出哪里不对。
        // 取 112/96/56：都是 8 的倍数，且 96 能放下 16 字符的 WDDM 版本号。
        public const double ColValue = 112;
        public const double ColTag = 96;
        public const double ColAction = 56;

        // ── 1. 读数 ────────────────────────────────────────────────────────

        /// <summary>
        /// 数值 + 单位。单位沉到底线、退一档字号、降一级明度——
        /// 它要读得出来，但不许和数值抢同一层注意力。
        /// 返回 Grid 而不是 StackPanel：Grid 不参与"按内容撑高"，嵌进等高单元时不会把行撑歪。
        /// </summary>
        public static Grid Readout(TextBlock val, string unit)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.HorizontalAlignment = HorizontalAlignment.Left;
            g.Children.Add(val);

            if (!string.IsNullOrEmpty(unit))
            {
                g.Children.Add(PutUnit(Mono(unit, T.FsNano, T.Dim)));
            }
            return g;
        }

        /// <summary>同一读数，单位由调用方给（要单独测量 / 复用同一个 TextBlock 时用）。</summary>
        public static Grid Readout(TextBlock val, TextBlock unit)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.HorizontalAlignment = HorizontalAlignment.Left;
            g.Children.Add(val);
            if (unit != null) g.Children.Add(PutUnit(unit));
            return g;
        }

        /// <summary>单位落位：底对齐 + 5px（Bahnschrift 基线到字框底的距离），与数值同底不齐顶。</summary>
        private static TextBlock PutUnit(TextBlock u)
        {
            u.VerticalAlignment = VerticalAlignment.Bottom;
            u.Margin = new Thickness(6, 0, 0, 5);
            u.IsHitTestVisible = false;
            Grid.SetColumn(u, 1);
            return u;
        }

        // ── 2. 变化量 ──────────────────────────────────────────────────────

        /// <summary>
        /// 变化量芯片。dir: +1 升 / 0 平 / -1 降。
        /// higherIsBetter 决定颜色：CPU 上升是坏事，内存余量上升是好事——同一支箭头两种含义，
        /// 所以颜色不能由方向决定，只能由"这个指标向哪个方向算好"决定。
        /// 平局不画箭头：给 0 变化配一个中性色块，等于制造了一行没有信息的像素。
        /// </summary>
        public static Border Delta(int dir, string text, bool higherIsBetter)
        {
            uint tint = T.Mute;
            string mark = "\u00B7";
            if (dir > 0) { mark = "\u25B2"; tint = higherIsBetter ? T.Ok : T.Bad; }
            else if (dir < 0) { mark = "\u25BC"; tint = higherIsBetter ? T.Bad : T.Ok; }

            Border b = new Border();
            b.CornerRadius = new CornerRadius(3);
            b.Background = T.S(Wash(tint, 0x14));
            b.Padding = new Thickness(4, 1, 4, 1);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.IsHitTestVisible = false;
            b.Child = Mono(mark + " " + text, T.FsNano, tint);
            return b;
        }

        // ── 3. 徽标 ────────────────────────────────────────────────────────

        /// <summary>描边徽标：低权重，用于"×3""A""重启生效"这类属性标签。</summary>
        public static Border Tag(string text, uint tint)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(3);
            b.BorderBrush = T.S(Wash(tint, 0x38));
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(5, 0, 5, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.IsHitTestVisible = false;
            b.Child = Mono(text, T.FsNano, tint);
            return b;
        }

        /// <summary>实底徽标：高权重，一屏最多出现一处——它表示"不满足这条就做不了"。</summary>
        public static Border TagSolid(string text, uint tint)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(3);
            b.Background = T.S(tint);
            b.Padding = new Thickness(5, 1, 5, 1);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.IsHitTestVisible = false;
            b.Child = Mono(text, T.FsNano, T.Root);
            return b;
        }

        // ── 4. 行外壳 ──────────────────────────────────────────────────────

        /// <summary>
        /// 列表行外壳：左缘 2px 风险色。
        /// 原来每行都用一枚 "A"/"B"/"C" 徽标表示风险等级——读者得先记住字母表，
        /// 再逐行翻译。颜色是前置注意，字母是后置阅读；风险等级属于前者。
        /// 注意：会覆盖 content 的 Margin（左侧让出 14px 给色条）。
        /// </summary>
        public static Border RowShell(UIElement content, uint risk, double padV)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            Rectangle bar = new Rectangle();
            bar.Fill = T.S(risk);
            bar.RadiusX = 1;
            bar.RadiusY = 1;
            bar.Opacity = 0.9;
            bar.Margin = new Thickness(0, 1, 0, 1);
            bar.IsHitTestVisible = false;
            g.Children.Add(bar);

            FrameworkElement fe = content as FrameworkElement;
            if (fe != null)
            {
                fe.Margin = new Thickness(14, 0, 0, 0);
                Grid.SetColumn(fe, 1);
                g.Children.Add(fe);
            }

            Border host = new Border();
            host.Padding = new Thickness(0, padV, 0, padV);
            host.Child = g;
            return host;
        }

        public static Border RowShell(UIElement content, uint risk) { return RowShell(content, risk, 9); }

        // ── 5. 统计格 ──────────────────────────────────────────────────────

        /// <summary>
        /// 一格读数：标签 → 刻度 → 数值(+单位) → 中间层（趋势/量条/变化量）→ 注脚。
        /// 五层的垂直节奏固定为 6 / 8 / 7，页面不许各自调——这就是"11 页是一台仪器"的来源。
        /// </summary>
        public static StackPanel Stat(string label, TextBlock val, string unit, UIElement mid, string note)
        {
            StackPanel sp = new StackPanel();

            sp.Children.Add(Label(label, T.Mute));

            Border rule = new Border();
            rule.Height = 1;
            rule.Width = 34;
            rule.HorizontalAlignment = HorizontalAlignment.Left;
            rule.Background = T.S(T.LineSoft);
            rule.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(rule);

            Grid ro = Readout(val, unit);
            ro.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(ro);

            if (mid != null)
            {
                FrameworkElement me = mid as FrameworkElement;
                if (me != null) me.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(mid);
            }

            if (!string.IsNullOrEmpty(note))
            {
                sp.Children.Add(Note(note));
            }
            return sp;
        }

        /// <summary>与上面同构，但注脚是外部持有的 TextBlock（活体读数用这个）。</summary>
        public static StackPanel Stat(string label, TextBlock val, string unit, UIElement mid, TextBlock note)
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Label(label, T.Mute));

            Border rule = new Border();
            rule.Height = 1;
            rule.Width = 34;
            rule.HorizontalAlignment = HorizontalAlignment.Left;
            rule.Background = T.S(T.LineSoft);
            rule.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(rule);

            Grid ro = Readout(val, unit);
            ro.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(ro);

            if (mid != null)
            {
                FrameworkElement me = mid as FrameworkElement;
                if (me != null) me.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(mid);
            }
            if (note != null) sp.Children.Add(note);
            return sp;
        }

        /// <summary>读数头由调用方给（数值 + 单位 + 变化量挤同一行时用这个）。</summary>
        public static StackPanel Stat(string label, UIElement head, UIElement mid, TextBlock note)
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Label(label, T.Mute));

            Border rule = new Border();
            rule.Height = 1;
            rule.Width = 34;
            rule.HorizontalAlignment = HorizontalAlignment.Left;
            rule.Background = T.S(T.LineSoft);
            rule.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(rule);

            FrameworkElement he = head as FrameworkElement;
            if (he != null) he.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(head);

            if (mid != null)
            {
                FrameworkElement me = mid as FrameworkElement;
                if (me != null) me.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(mid);
            }
            if (note != null) sp.Children.Add(note);
            return sp;
        }

        /// <summary>注脚统一节奏：7px，单行截断（换行会让整页高度随窗口宽度跳）。</summary>
        public static TextBlock Note(string s)
        {
            TextBlock n = Mono(s, T.FsNano, T.Mute);
            return Note(n);
        }

        public static TextBlock Note(TextBlock n)
        {
            n.Margin = new Thickness(0, 7, 0, 0);
            n.TextTrimming = TextTrimming.CharacterEllipsis;
            n.TextWrapping = TextWrapping.NoWrap;
            return n;
        }
    }

    /// <summary>
    /// 活体变化量：每秒被重写一次，但只在"方向或文本真的变了"时才碰 UI。
    /// 用 Border+TextBlock 而不是每次重建一个 Border：后者 1Hz × 4 格 × 每页都在跑，
    /// 一天就是几十万个短命边框对象——它们不会泄漏，但会把 GC 的 Gen0 抬起来。
    /// </summary>
    public sealed class DeltaChip : Border
    {
        private readonly TextBlock _text;
        private string _shown;
        private bool _hasShown;
        private int _lastDir;
        private bool _lastHigher;
        private string _lastBody;

        public DeltaChip()
        {
            CornerRadius = new CornerRadius(3);
            Padding = new Thickness(4, 1, 4, 1);
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;
            _text = new TextBlock();
            _text.FontSize = T.FsNano;
            _text.FontFamily = T.FMono;
            _text.VerticalAlignment = VerticalAlignment.Center;
            Child = _text;
        }

        /// <summary>dir: +1 升 / 0 平 / -1 降；higherIsBetter 决定颜色，不决定箭头。</summary>
        public void Set(int dir, string body, bool higherIsBetter)
        {
            string b = body ?? "";
            if (_hasShown && _lastDir == dir && _lastHigher == higherIsBetter && string.Equals(_lastBody, b, StringComparison.Ordinal))
                return;
            _hasShown = true;
            _lastDir = dir;
            _lastHigher = higherIsBetter;
            _lastBody = b;
            _shown = dir.ToString() + "|" + (higherIsBetter ? "1" : "0") + "|" + b;

            // 空 body = "这一拍没有可比基准"：留位置不显示，避免出现"\u00B7 0.0"这种伪像素。
            if (string.IsNullOrEmpty(b)) { Visibility = Visibility.Hidden; return; }
            Visibility = Visibility.Visible;

            uint tint = T.Mute;
            string mark = "\u00B7";
            if (dir > 0) { mark = "\u25B2"; tint = higherIsBetter ? T.Ok : T.Bad; }
            else if (dir < 0) { mark = "\u25BC"; tint = higherIsBetter ? T.Bad : T.Ok; }

            _text.Text = mark + " " + b;
            _text.Foreground = T.S(tint);
            Background = T.S(Ui.Wash(tint, 0x14));
        }
    }
}