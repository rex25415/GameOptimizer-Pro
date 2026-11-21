// ============================================================================
//  Ui.cs — 视觉原语：排版 / 布局 / 容器 / 发丝线 / 角落刻度
//  原则：一切尺寸来自令牌，一切留白来自 4 的倍数，一切描边为 1px。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Go
{
    public static partial class Ui
    {
        /// <summary>离屏渲染时关闭动画，保证首帧即终态。</summary>
        public static bool Animate = true;

        // ---- 文本 ----------------------------------------------------------

        public static TextBlock Tx(string s, double size, uint col, FontFamily ff)
        {
            TextBlock t = new TextBlock();
            t.Text = s;
            t.FontSize = size;
            t.FontFamily = ff;
            t.Foreground = T.S(col);
            TextOptions.SetTextFormattingMode(t, size <= 13.5 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        public static TextBlock Muted(string s, double size)
        {
            TextBlock t = Tx(s, size, T.Dim, T.FCn);
            return t;
        }

        /// <summary>微标签：全大写 + 字距。用于分组名与字段名。</summary>
        public static TextBlock Label(string s, uint col)
        {
            TextBlock t = new TextBlock();
            t.Text = Spaced(s.ToUpperInvariant());
            t.FontSize = T.FsNano;
            t.FontFamily = T.FDisplayReg;
            t.Foreground = T.S(col);
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        /// <summary>等宽读数：数值/单位/键值，使用 Cascadia Mono 保证跳动时不抖动。</summary>
        public static TextBlock Mono(string s, double size, uint col)
        {
            TextBlock t = Tx(s, size, col, T.FMono);
            return t;
        }

        /// <summary>仪表大数字</summary>
        public static TextBlock Num(string s, double size, uint col)
        {
            TextBlock t = Tx(s, size, col, T.FDisplay);
            t.FontWeight = FontWeights.SemiBold;
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
            return t;
        }

        /// <summary>在 ASCII 字符串的字符之间插入细空格模拟字距（WPF 无 letter-spacing）。
        /// 单词间的普通空格映射为 en-space + thin-space，拉开单词边界避免紧排粘连。</summary>
        public static string Spaced(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            for (int i = 0; i < s.Length; i++)
                if (s[i] > 127) return s; // 含中文则不加字距
            StringBuilder sb = new StringBuilder(s.Length * 2);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == ' ')
                {
                    sb.Append('\u2002');
                    sb.Append('\u2009');
                    continue;
                }
                if (i > 0 && s[i - 1] != ' ') sb.Append('\u2009');
                sb.Append(ch);
            }
            return sb.ToString();
        }

        // ---- 布局 ----------------------------------------------------------

        /// <summary>带顶部外边距的 1px 发丝分割线（供多页面板内部统一调用）。</summary>
        public static Border Hairline(double topMargin)
        {
            Border b = Rule(T.Line, 0);
            b.Margin = new Thickness(0, topMargin, 0, 0);
            return b;
        }

        /// <summary>水平发丝线</summary>
        public static Border Rule(uint col, double margin)
        {
            Border b = new Border();
            b.Height = 1;
            b.Background = T.S(col);
            b.Margin = new Thickness(margin, 0, margin, 0);
            b.SnapsToDevicePixels = true;
            return b;
        }

        // ---- 容器 ----------------------------------------------------------

        /// <summary>标准面板：石墨底 + 1px 边 + 顶部机加高光。</summary>
        public static Border Panel(UIElement content, double pad)
        {
            return PanelCore(content, pad, false);
        }

        /// <summary>主视口面板：在标准玻璃材质上叠加双色相对角极光与顶部折射光棱，用于页面唯一视觉重心。</summary>
        public static Border HeroPanel(UIElement content, double pad)
        {
            return PanelCore(content, pad, true);
        }

        /// <summary>嵌套 Bento 指标玻璃单元：带顶部微语义光丝与悬停提亮，用于四格读数与硬件卡片。</summary>
        public static Border GlassTile(UIElement content, double pad, uint topTint)
        {
            Border outer = new Border();
            outer.Background = T.SubCardBg();
            outer.BorderBrush = T.S(topTint != 0 ? Wash(topTint, 0x36) : T.LineSoft);
            outer.BorderThickness = new Thickness(1);
            outer.CornerRadius = new CornerRadius(T.RadiusMd + 2);
            outer.SnapsToDevicePixels = true;

            Grid g = new Grid();

            if (topTint != 0)
            {
                Rectangle strip = new Rectangle();
                strip.Height = 2;
                strip.RadiusX = 1;
                strip.RadiusY = 1;
                strip.Fill = T.Grad(topTint, Wash(topTint, 0x22), 0);
                strip.VerticalAlignment = VerticalAlignment.Top;
                strip.Margin = new Thickness(12, 0, 12, 0);
                strip.IsHitTestVisible = false;
                g.Children.Add(strip);
            }
            else
            {
                Rectangle bevel = new Rectangle();
                bevel.Height = 1;
                bevel.Fill = T.Bevel();
                bevel.VerticalAlignment = VerticalAlignment.Top;
                bevel.Margin = new Thickness(10, 0, 10, 0);
                bevel.Opacity = 0.65;
                bevel.IsHitTestVisible = false;
                g.Children.Add(bevel);
            }

            if (content != null)
            {
                Border host = new Border();
                host.Padding = new Thickness(pad);
                host.Child = content;
                g.Children.Add(host);
            }

            outer.Child = g;
            return outer;
        }

        private static Border PanelCore(UIElement content, double pad, bool heroGlow)
        {
            double rad = heroGlow ? T.RadiusLg : T.Radius;
            Border outer = new Border();
            outer.Background = heroGlow ? T.HeroPanelBg() : T.GlassPanelBg();
            outer.CornerRadius = new CornerRadius(rad);
            outer.SnapsToDevicePixels = true;

            Grid g = new Grid();

            if (heroGlow)
            {
                Rectangle glow = new Rectangle();
                glow.Fill = T.HeroGlow();
                glow.RadiusX = rad;
                glow.RadiusY = rad;
                glow.IsHitTestVisible = false;
                g.Children.Add(glow);

                Rectangle glow2 = new Rectangle();
                glow2.Fill = T.HeroGlow2();
                glow2.RadiusX = rad;
                glow2.RadiusY = rad;
                glow2.IsHitTestVisible = false;
                g.Children.Add(glow2);
            }

            // 1) 斜向光学玻璃漫反射
            Rectangle sheen = new Rectangle();
            sheen.Fill = T.Sheen();
            sheen.RadiusX = rad;
            sheen.RadiusY = rad;
            sheen.IsHitTestVisible = false;
            g.Children.Add(sheen);

            // 2) 下缘内压深：赋予玻璃卡清晰的浮雕厚度
            Rectangle shade = new Rectangle();
            shade.Fill = T.Shade();
            shade.Height = 22;
            shade.VerticalAlignment = VerticalAlignment.Bottom;
            shade.Margin = new Thickness(1, 0, 1, 1);
            shade.RadiusX = rad - 1;
            shade.RadiusY = rad - 1;
            shade.IsHitTestVisible = false;
            g.Children.Add(shade);

            // 3) 玻璃外框（HeroPanel 叠加主题色折射边缘）
            Border line = new Border();
            line.BorderBrush = T.S(heroGlow ? T.AccentA(0x4C) : T.Line);
            line.BorderThickness = new Thickness(1);
            line.CornerRadius = new CornerRadius(rad);
            line.IsHitTestVisible = false;
            g.Children.Add(line);

            // 4) 顶部折射高光棱：HeroPanel 使用 1.5px 双色相光棱，标准面板使用冷白玻璃高光
            Rectangle bevel = new Rectangle();
            bevel.Height = heroGlow ? 1.5 : 1;
            bevel.Fill = heroGlow ? T.AccentBevel() : T.Bevel();
            bevel.VerticalAlignment = VerticalAlignment.Top;
            bevel.Margin = new Thickness(16, 0, 16, 0);
            bevel.IsHitTestVisible = false;
            g.Children.Add(bevel);

            if (content != null)
            {
                Border host = new Border();
                host.Padding = new Thickness(pad);
                host.Child = content;
                g.Children.Add(host);
            }
            outer.Child = g;
            return outer;
        }

        /// <summary>凹陷仪器井：深空蓝黑底 + 冷玻璃细边，用于图表与检索输入区。</summary>
        public static Border Inset(UIElement content, double pad)
        {
            Border b = new Border();
            b.Background = T.S(T.Inset);
            b.BorderBrush = T.S(T.LineSoft);
            b.BorderThickness = new Thickness(1);
            b.CornerRadius = new CornerRadius(T.RadiusMd);
            b.SnapsToDevicePixels = true;
            if (content != null)
            {
                b.Padding = new Thickness(pad);
                b.Child = content;
            }
            return b;
        }

        /// <summary>四角刻度（HUD 准星语言），仅在主模块使用。</summary>
        public static Grid CornerTicks(uint col, double len, double thick)
        {
            Grid g = new Grid();
            g.IsHitTestVisible = false;
            for (int i = 0; i < 4; i++)
            {
                bool top = i < 2;
                bool left = (i % 2) == 0;
                Rectangle h = new Rectangle();
                h.Height = thick; h.Width = len;
                h.Fill = T.S(col);
                h.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                h.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                g.Children.Add(h);

                Rectangle v = new Rectangle();
                v.Width = thick; v.Height = len;
                v.Fill = T.S(col);
                v.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                v.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                g.Children.Add(v);
            }
            return g;
        }

        private struct IconKey : IEquatable<IconKey>
        {
            public string Data;
            public long Size;
            public bool Equals(IconKey o) { return Size == o.Size && Data == o.Data; }
            public override bool Equals(object obj) { return obj is IconKey && Equals((IconKey)obj); }
            public override int GetHashCode() { unchecked { return (Data == null ? 0 : Data.GetHashCode()) * 397 ^ (int)Size; } }
        }
        private static readonly Dictionary<IconKey, Geometry> _iconCache = new Dictionary<IconKey, Geometry>();

        /// <summary>
        /// 取（路径, 尺寸）对应的已缩放冻结几何。
        /// 抽出来是为了能在后台线程预热：Geometry.Parse + Freeze 不依赖 UI 线程，
        /// 而首次解析 + 首次缩放实测要 132~250ms。字体不能后台预热（文本测量是线程级缓存），
        /// 几何可以——这是冷启动里唯一能真正提前拿到的收益。
        /// </summary>
        internal static Geometry IconGeometry(string data, double size)
        {
            IconKey key = new IconKey();
            key.Data = data;
            key.Size = BitConverter.DoubleToInt64Bits(size);
            Geometry hit;
            lock (_iconCache) { if (_iconCache.TryGetValue(key, out hit)) return hit; }

            Geometry g = T.G(data).Clone();
            double k = size / 24.0;
            g.Transform = new ScaleTransform(k, k);
            g.Freeze();
            lock (_iconCache) { _iconCache[key] = g; }
            return g;
        }

        /// <summary>
        /// 图标（24 网格线性几何，描边不随尺寸缩放）。
        /// 旧实现每次调用都 Clone() 一份几何再挂一个 ScaleTransform——导航栏一项就一个，
        /// 全应用上百个图标，启动时白造上百个可冻结却没冻结的几何对象。
        /// 现在按 (路径, 尺寸) 缓存已经缩放并冻结的那一份。
        /// </summary>
        public static Path Icon(string data, double size, uint col, double stroke)
        {
            Geometry g = IconGeometry(data, size);
            Path p = new Path();
            p.Data = g;
            p.Stroke = T.S(col);
            p.StrokeThickness = stroke;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.Width = size; p.Height = size;
            p.Stretch = Stretch.None;
            p.HorizontalAlignment = HorizontalAlignment.Center;
            p.VerticalAlignment = VerticalAlignment.Center;
            return p;
        }

        public static Grid IconTile(string data, double box, uint fg, uint bg, double radius)
        {
            Grid g = new Grid();
            g.Width = box; g.Height = box;
            Border b = new Border();
            b.CornerRadius = new CornerRadius(radius);
            b.Background = T.S(bg);
            b.BorderBrush = T.S(T.LineSoft);
            b.BorderThickness = new Thickness(1);
            g.Children.Add(b);
            g.Children.Add(Icon(data, box * 0.46, fg, 1.35));
            return g;
        }

        // ---- 动效 ----------------------------------------------------------

        /// <summary>入场：120ms 淡入 + 6px 上浮（GPU 合成的 Opacity/Transform，不触发布局）。</summary>
        public static void Enter(UIElement el, int delayMs, double rise)
        {
            // 入场动效统一走 Motion：只碰 RenderTransform / Opacity，永不触发 measure/arrange。
            Motion.SlideIn(el, 0, rise, delayMs);
        }

        public static void Fade(UIElement el, double to, int ms)
        {
            if (!Animate || Rs.ReduceMotion || ms <= 0) { el.Opacity = to; return; }
            DoubleAnimation a = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }


        // ---- 共享页头 / 区块头 / 文本助手（MAGNUM 架构收敛）-------------------

        /// <summary>
        /// 统一页头。11 页同构脚手架一次收编。
        ///
        /// Rev80 重构：原来 kick 独占一行压在标题上方，标题下面再跟一行副题，
        /// 于是页头是"三行越来越小的字"，读起来像三级标题，没有任何一处是"模块的入口"。
        /// 现在改成仪表铭牌的做法 —— 标题与模块号同一条基线（标题是主语，编号是它的序列号），
        /// 下面一条贯通全宽的刻度线把页头与内容切开，线头带 56px 强调色端帽。
        /// 读者的视线会先落在标题上，再顺着刻度线滑到右侧动作区：这就是页头的全部工作。
        /// </summary>
        public static Grid PageHeader(string kick, string title, string sub, UIElement right)
        {
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel left = new StackPanel();

            Grid trow = new Grid();
            trow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            trow.ColumnDefinitions.Add(new ColumnDefinition());

            TextBlock t = Tx(title, T.FsPage, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            t.VerticalAlignment = VerticalAlignment.Bottom;
            trow.Children.Add(t);

            if (!string.IsNullOrEmpty(kick))
            {
                Border kb = new Border();
                kb.CornerRadius = new CornerRadius(10);
                kb.Background = T.S(T.AccentA(0x1E));
                kb.BorderBrush = T.S(T.AccentA(0x52));
                kb.BorderThickness = new Thickness(1);
                kb.Padding = new Thickness(8, 2, 9, 2);
                kb.VerticalAlignment = VerticalAlignment.Bottom;
                kb.HorizontalAlignment = HorizontalAlignment.Left;
                kb.Margin = new Thickness(12, 0, 0, 5);
                kb.IsHitTestVisible = false;
                StackPanel ksp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Border kdot = Dot(T.Accent, 5);
                kdot.Margin = new Thickness(0, 0, 6, 0);
                ksp.Children.Add(kdot);
                ksp.Children.Add(Ui.Mono(kick, T.FsNano, T.Accent));
                kb.Child = ksp;
                Grid.SetColumn(kb, 1);
                trow.Children.Add(kb);
            }
            left.Children.Add(trow);

            TextBlock s = Tx(sub, T.FsSmall, T.Dim, T.FCn);
            s.Margin = new Thickness(0, 7, 0, 0);
            s.TextTrimming = TextTrimming.CharacterEllipsis;
            s.TextWrapping = TextWrapping.NoWrap;
            left.Children.Add(s);
            head.Children.Add(left);

            if (right != null)
            {
                Grid.SetColumn(right, 1);
                head.Children.Add(right);
            }

            // 贯通全宽的光学标尺线：页头下边界，2px 双色相渐隐光带端帽。
            Grid rule = new Grid();
            rule.Height = 2;
            rule.SnapsToDevicePixels = true;
            Border rlBase = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = T.S(T.Line) };
            rule.Children.Add(rlBase);
            Border rlCap = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = T.CapGrad(), Width = 128, HorizontalAlignment = HorizontalAlignment.Left };
            rule.Children.Add(rlCap);
            rule.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(rule, 1);
            Grid.SetColumnSpan(rule, 2);
            head.Children.Add(rule);
            return head;
        }

        /// <summary>区块头：标题 + 英文小标签 + 右侧读数。</summary>
        public static Grid PanelHead(string title, string label, TextBlock meta)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel hl = new StackPanel();

            // 标题前的 3px 胶囊双色光柱：全应用区块头共用的高亮视觉锚点。
            StackPanel trow = new StackPanel();
            trow.Orientation = Orientation.Horizontal;
            Border tick = new Border();
            tick.Width = 3;
            tick.Height = 14;
            tick.CornerRadius = new CornerRadius(1.5);
            tick.Background = T.AccentGrad();
            tick.Margin = new Thickness(0, 0, 10, 1);
            tick.VerticalAlignment = VerticalAlignment.Bottom;
            trow.Children.Add(tick);

            TextBlock t = Tx(title, T.FsTitle, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            trow.Children.Add(t);
            hl.Children.Add(trow);

            TextBlock s = Label(label, T.Mute);
            s.Margin = new Thickness(13, 4, 0, 0);
            s.Opacity = 0.85;
            hl.Children.Add(s);
            g.Children.Add(hl);
            if (meta != null)
            {
                meta.VerticalAlignment = VerticalAlignment.Center;
                meta.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(meta, 1);
                g.Children.Add(meta);
            }
            return g;
        }

        /// <summary>单行截断（空值哨兵由调用方定）。</summary>
        public static string Trunc(string s, int n, string empty)
        {
            if (string.IsNullOrEmpty(s)) return empty;
            s = s.Trim();
            if (s.Length <= n) return s;
            return s.Substring(0, n - 1) + "…";
        }

        /// <summary>路径中段省略。</summary>
        public static string ShortPath(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return "";
            if (exe.Length <= 46) return exe;
            return exe.Substring(0, 22) + " … " + exe.Substring(exe.Length - 20);
        }

        /// <summary>动作风险色三元式归一。</summary>
        public static uint RiskTint(string risk)
        {
            if (risk == "C") return T.Bad;
            if (risk == "B") return T.Warn;
            return T.Ok;
        }

        // ---- 滚动 ----------------------------------------------------------

        /// <summary>隐藏系统滚动条、保留滚轮/键盘滚动的最小化滚动视图。</summary>
        public static ScrollViewer Scroll(UIElement content)
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            sv.CanContentScroll = false;
            sv.Focusable = false;
            sv.PanningMode = PanningMode.VerticalOnly;
            sv.Content = content;
            return sv;
        }

        // ---- 紧凑指示微件 --------------------------------------------------

        /// <summary>紧凑圆形状态光点。</summary>
        public static Border Dot(uint col, double size)
        {
            Border b = new Border();
            b.Width = size;
            b.Height = size;
            b.CornerRadius = new CornerRadius(size * 0.5);
            b.Background = T.S(col);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.SnapsToDevicePixels = true;
            b.IsHitTestVisible = false;
            return b;
        }

        /// <summary>光点 + 文本的紧凑水平状态条。</summary>
        public static StackPanel DotLabel(uint col, string text, uint textCol)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;
            Border dot = Dot(col, 6);
            dot.Margin = new Thickness(0, 0, 6, 0);
            sp.Children.Add(dot);
            TextBlock t = Tx(text ?? "", T.FsNano, textCol, T.FCn);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            return sp;
        }
    }
}
