// ============================================================================
//  Gfx.cs — 绘制原语缓存：画笔 / 画刷 / 文本 / 渐变 / 热力色阶
//  目标：OnRender 高频路径零堆分配。所有对象首次构造后冻结复用。
//  线程：仅 UI 线程访问，因此不加锁（采样线程从不碰这里）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Go
{
    public static class Gfx
    {
        // ---- 画笔 ----------------------------------------------------------
        private struct PenKey : IEquatable<PenKey>
        {
            public uint Argb;
            public int WidthX10;
            public int Dash;   // 0 = 实线，其余为虚线样式编号

            public bool Equals(PenKey o)
            {
                return Argb == o.Argb && WidthX10 == o.WidthX10 && Dash == o.Dash;
            }

            public override bool Equals(object obj)
            {
                return obj is PenKey && Equals((PenKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return (int)(Argb * 397) ^ (WidthX10 * 31) ^ Dash; }
            }
        }

        private static readonly Dictionary<PenKey, Pen> _pens = new Dictionary<PenKey, Pen>(64);
        private static readonly Dictionary<PenKey, Pen> _dashes = new Dictionary<PenKey, Pen>(16);

        /// <summary>取冻结的实线画笔（带缓存，零分配）。</summary>
        public static Pen P(uint argb, double width)
        {
            PenKey k = new PenKey();
            k.Argb = argb;
            k.WidthX10 = (int)Math.Round(width * 10);
            k.Dash = 0;
            Pen p;
            if (_pens.TryGetValue(k, out p)) return p;
            p = new Pen(T.S(argb), k.WidthX10 / 10.0);
            p.Freeze();
            _pens[k] = p;
            return p;
        }

        /// <summary>取冻结的虚线画笔（带缓存）。dash 用于区分不同虚线节奏。</summary>
        public static Pen Dash(uint argb, double width, int dash)
        {
            PenKey k = new PenKey();
            k.Argb = argb;
            k.WidthX10 = (int)Math.Round(width * 10);
            k.Dash = dash;
            Pen p;
            if (_dashes.TryGetValue(k, out p)) return p;
            double[] pattern;
            switch (dash)
            {
                case 1: pattern = new double[] { 3, 4 }; break;
                case 2: pattern = new double[] { 2, 3 }; break;
                case 3: pattern = new double[] { 1, 3 }; break;
                default: pattern = new double[] { 4, 4 }; break;
            }
            p = new Pen(T.S(argb), k.WidthX10 / 10.0);
            p.DashStyle = new DashStyle(pattern, 0);
            p.Freeze();
            _dashes[k] = p;
            return p;
        }

        // ---- 圆角端点画笔（仪表盘用）---------------------------------------
        private static readonly Dictionary<PenKey, Pen> _round = new Dictionary<PenKey, Pen>(8);

        public static Pen Round(uint argb, double width)
        {
            PenKey k = new PenKey();
            k.Argb = argb;
            k.WidthX10 = (int)Math.Round(width * 10);
            k.Dash = 99;
            Pen p;
            if (_round.TryGetValue(k, out p)) return p;
            p = new Pen(T.S(argb), k.WidthX10 / 10.0);
            p.StartLineCap = PenLineCap.Round;
            p.EndLineCap = PenLineCap.Round;
            p.Freeze();
            _round[k] = p;
            return p;
        }

        // ---- 渐变 ----------------------------------------------------------
        private struct GradKey : IEquatable<GradKey>
        {
            public uint Argb;
            public byte A0;
            public byte A1;

            public bool Equals(GradKey o) { return Argb == o.Argb && A0 == o.A0 && A1 == o.A1; }
            public override bool Equals(object obj) { return obj is GradKey && Equals((GradKey)obj); }
            public override int GetHashCode() { unchecked { return (int)(Argb * 31) ^ A0 ^ (A1 << 8); } }
        }

        private static readonly Dictionary<GradKey, LinearGradientBrush> _grads = new Dictionary<GradKey, LinearGradientBrush>(24);
        private static readonly Dictionary<ulong, LinearGradientBrush> _grads3 = new Dictionary<ulong, LinearGradientBrush>(24);
        private static readonly Dictionary<uint, RadialGradientBrush> _radials = new Dictionary<uint, RadialGradientBrush>(16);

        /// <summary>90° 垂直渐变（顶 → 底），用于波形与迷你趋势的面积填充。</summary>
        public static LinearGradientBrush Grad(uint argb, byte topAlpha, byte bottomAlpha)
        {
            GradKey k = new GradKey();
            k.Argb = argb; k.A0 = topAlpha; k.A1 = bottomAlpha;
            LinearGradientBrush b;
            if (_grads.TryGetValue(k, out b)) return b;
            Color c0 = T.C(argb); c0.A = topAlpha;
            Color c1 = T.C(argb); c1.A = bottomAlpha;
            b = new LinearGradientBrush(c0, c1, 90);
            b.Freeze();
            _grads[k] = b;
            return b;
        }

        /// <summary>三段光学垂直渐变（顶高光 → 中柔光 → 底隐没），冻结缓存零分配。</summary>
        public static LinearGradientBrush Grad3(uint argb, byte topAlpha, byte midAlpha, byte bottomAlpha)
        {
            ulong key = ((ulong)argb << 24) | ((ulong)topAlpha << 16) | ((ulong)midAlpha << 8) | bottomAlpha;
            LinearGradientBrush b;
            if (_grads3.TryGetValue(key, out b)) return b;
            Color c = T.C(argb);
            b = new LinearGradientBrush();
            b.StartPoint = new Point(0.5, 0);
            b.EndPoint = new Point(0.5, 1);
            b.GradientStops.Add(new GradientStop(Color.FromArgb(topAlpha, c.R, c.G, c.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(midAlpha, c.R, c.G, c.B), 0.42));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(bottomAlpha, c.R, c.G, c.B), 1));
            b.Freeze();
            _grads3[key] = b;
            return b;
        }

        /// <summary>仪表盘中心径向辉光画刷（冻结缓存零分配）。</summary>
        public static RadialGradientBrush RadialAura(uint argb, byte centerAlpha)
        {
            uint key = ((uint)centerAlpha << 24) | (argb & 0x00FFFFFFu);
            RadialGradientBrush b;
            if (_radials.TryGetValue(key, out b)) return b;
            Color c = T.C(argb);
            b = new RadialGradientBrush();
            b.Center = new Point(0.5, 0.5);
            b.GradientOrigin = new Point(0.5, 0.42);
            b.RadiusX = 0.52;
            b.RadiusY = 0.52;
            b.GradientStops.Add(new GradientStop(Color.FromArgb(centerAlpha, c.R, c.G, c.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(centerAlpha / 3), c.R, c.G, c.B), 0.55));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            b.Freeze();
            _radials[key] = b;
            return b;
        }

        // ---- 文本 ----------------------------------------------------------
        private struct TextKey : IEquatable<TextKey>
        {
            public string S;
            public int SizeX10;
            public uint Argb;
            public int Style;   // 0 = 等宽，1 = 显示体，2 = 中文正文
            public int DpiX4;

            public bool Equals(TextKey o)
            {
                return SizeX10 == o.SizeX10 && Argb == o.Argb && Style == o.Style
                    && DpiX4 == o.DpiX4 && string.Equals(S, o.S, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) { return obj is TextKey && Equals((TextKey)obj); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = S == null ? 0 : S.GetHashCode();
                    return h ^ (SizeX10 * 397) ^ (int)Argb ^ (Style * 17) ^ (DpiX4 * 7);
                }
            }
        }

        private static readonly Dictionary<TextKey, FormattedText> _text = new Dictionary<TextKey, FormattedText>(96);
        private static readonly Typeface _tfMono = new Typeface("Cascadia Mono, Consolas");
        private static readonly Typeface _tfDisplay = new Typeface("Bahnschrift, Microsoft YaHei UI");
        private static readonly Typeface _tfBody = new Typeface("Microsoft YaHei UI");

        /// <summary>
        /// 取缓存的 FormattedText。style：0 等宽 / 1 显示体 / 2 中文正文。
        /// 轴标签、刻度数字这类"字符串集合很小、刷新很频繁"的文本必须走这里。
        /// </summary>
        public static FormattedText Text(string s, double size, uint argb, int style, double dpi)
        {
            TextKey k = new TextKey();
            k.S = s;
            k.SizeX10 = (int)Math.Round(size * 10);
            k.Argb = argb;
            k.Style = style;
            k.DpiX4 = (int)Math.Round(dpi * 4);
            FormattedText ft;
            if (_text.TryGetValue(k, out ft)) return ft;

            Typeface tf = style == 0 ? _tfMono : style == 1 ? _tfDisplay : _tfBody;
            ft = new FormattedText(s ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, k.SizeX10 / 10.0, T.S(argb), dpi);
            // 文本缓存上界：核心编号 / CCD 标签 / 阈值标注这一类，正常不超过 128 条。
            if (_text.Count > 512) _text.Clear();
            _text[k] = ft;
            return ft;
        }

        public static double Dpi(Visual v)
        {
            return VisualTreeHelper.GetDpi(v).PixelsPerDip;
        }

        // ---- 热力色阶 ------------------------------------------------------
        // CoreGrid 每帧对 32 个核心各自算一次 T.Mix(底, 热, load)：颜色随负载连续变化，
        // 如果直接 new SolidColorBrush 就是每帧 64 个新对象。这里把负载量化到 1/32 档，
        // 预建 33×3 张冻结画刷表——颜色差异肉眼不可辨，但分配归零。
        private const int HeatSteps = 32;
        private static readonly SolidColorBrush[][] _heat;
        private static readonly SolidColorBrush[] _heatBar;
        private static readonly uint[] _heatTint = new uint[] { T.Accent, T.Warn, T.Bad };

        static Gfx()
        {
            Color baseC = T.C(0xFF0C1424);
            _heat = new SolidColorBrush[3][];
            for (int h = 0; h < 3; h++)
            {
                _heat[h] = new SolidColorBrush[HeatSteps + 1];
                Color hot = T.C(_heatTint[h]);
                for (int i = 0; i <= HeatSteps; i++)
                {
                    double t = (double)i / HeatSteps;
                    double k = t <= 0 ? 0 : Math.Pow(t, 0.78);
                    Color fill = T.Mix(baseC, hot, k);
                    SolidColorBrush b = new SolidColorBrush(fill);
                    b.Freeze();
                    _heat[h][i] = b;
                }
            }
            _heatBar = new SolidColorBrush[3];
            for (int h = 0; h < 3; h++)
            {
                SolidColorBrush b = new SolidColorBrush(T.Alpha(_heatTint[h], 220));
                b.Freeze();
                _heatBar[h] = b;
            }
        }

        /// <summary>按负载取热力填充（0..1，已量化），tier：0 冰蓝 / 1 琥珀 / 2 危险。</summary>
        public static SolidColorBrush Heat(double load, int tier)
        {
            if (tier < 0) tier = 0; else if (tier > 2) tier = 2;
            int i = (int)(load * HeatSteps + 0.5);
            if (i < 0) i = 0; else if (i > HeatSteps) i = HeatSteps;
            return _heat[tier][i];
        }

        public static SolidColorBrush HeatBar(int tier)
        {
            if (tier < 0) tier = 0; else if (tier > 2) tier = 2;
            return _heatBar[tier];
        }

        /// <summary>主题切换时重建主色档（tier 0）热力色阶；琥珀/危险档不变。</summary>
        public static void RebuildAccent(uint accent)
        {
            _heatTint[0] = accent;
            Color baseC = T.C(0xFF0C1424);
            Color hot = T.C(accent);
            for (int i = 0; i <= HeatSteps; i++)
            {
                double t = (double)i / HeatSteps;
                double k = t <= 0 ? 0 : Math.Pow(t, 0.78);
                Color fill = T.Mix(baseC, hot, k);
                SolidColorBrush b = new SolidColorBrush(fill);
                b.Freeze();
                _heat[0][i] = b;
            }
            SolidColorBrush bb = new SolidColorBrush(T.Alpha(accent, 220));
            bb.Freeze();
            _heatBar[0] = bb;
        }
    }
}
