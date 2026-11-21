// ============================================================================
//  Theme.cs — 设计令牌：色彩 / 字体 / 尺度 / 笔触
//  设计语言：PRECISION INSTRUMENT —— 石墨底色 · 冰蓝信号色 · 1px 极细几何
//  所有画刷与画笔全部 Freeze()，避免渲染线程重复封送。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Go
{
    public static class T
    {
        // ---- 色彩（Rev120 HYPERION GLASS：深邃蓝曜玻璃空间层次）-------------
        public const uint Root        = 0xFF04070E; // 最深底色（窗口外沿）
        public const uint Rail        = 0xFF0B1221; // 左导轨基底
        public const uint Panel       = 0xFF10192B; // 标准玻璃面板
        public const uint PanelTop    = 0xFF17243B; // 面板受光上端
        public const uint PanelBottom = 0xFF0B111F; // 面板背光下端
        public const uint Inset       = 0xFF070C17; // 凹陷仪器井（图表底 / 输入框）
        public const uint Raised      = 0xFF1B2942; // 悬浮抬升层 / 选中行
        public const uint Hover       = 0xFF233452;
        public const uint CloseHot    = 0xFFC42B1C; // Windows 关闭红（悬停）
        public const uint CloseDown   = 0xFF8E1B12; // Windows 关闭红（按下）

        public const uint Line        = 0x22CFE6FF; // 冷光玻璃发丝边
        public const uint LineSoft    = 0x14CFE6FF; // 次级玻璃边
        public const uint LineFaint   = 0x0CCFE6FF; // 行悬停柔光底
        public const uint LineMid     = 0x36CFE6FF; // 中强调玻璃边
        public const uint LineHi      = 0x5ACFE6FF; // 强悬停棱边

        public const uint Hi          = 0xFFF7FAFF; // 最高对比文本
        public const uint Text        = 0xFFCBD8EA; // 正文
        public const uint Dim         = 0xFF8A9DB8; // 次级
        public const uint Mute        = 0xFF667A96; // 三级 / 禁用（高可读对比度）

        // 强调色：运行时可切换的双色相光学主题（冰蓝 / 琥珀 / 紫晶 / 薄荷），持久化在 ui.json。
        private static uint _accent     = 0xFF5FD9FF;
        private static uint _accent2    = 0xFF3B82F6;
        private static uint _accentDim  = 0xFF2E9AC4;
        private static uint _accentGlow = 0x4A5FD9FF;
        private static uint _accentWash = 0x1E5FD9FF;
        private static uint _accentRing = 0xAA5FD9FF;
        private static string _themeId  = "ice";

        public static uint Accent     { get { return _accent; } }
        public static uint Accent2    { get { return _accent2; } }
        public static uint AccentDim  { get { return _accentDim; } }
        public static uint AccentGlow { get { return _accentGlow; } }
        public static uint AccentWash { get { return _accentWash; } }
        public static uint AccentRing { get { return _accentRing; } }
        public static string ThemeId  { get { return _themeId; } }

        /// <summary>主题切换通知：控件静态捕获的强调色画刷据此重建（否则换主题后陈旧）。</summary>
        public static event Action ThemeChanged;

        /// <summary>主色叠加指定透明度（辉光 / 洗涤 / 焦点环等派生层）。</summary>
        public static uint AccentA(byte a) { return ((uint)a << 24) | (_accent & 0x00FFFFFFu); }

        /// <summary>伴生副强调色叠加指定透明度（双色相光学渐变与对角环境光场）。</summary>
        public static uint Accent2A(byte a) { return ((uint)a << 24) | (_accent2 & 0x00FFFFFFu); }

        public struct AccentTheme
        {
            public string Id;
            public string Name;
            public uint Accent, Accent2, Dim, Glow, Wash, Ring;
        }

        public static readonly AccentTheme[] Themes = new AccentTheme[]
        {
            new AccentTheme { Id="ice",    Name="冰蓝", Accent=0xFF5FD9FF, Accent2=0xFF3B82F6, Dim=0xFF2E9AC4, Glow=0x4A5FD9FF, Wash=0x1E5FD9FF, Ring=0xAA5FD9FF },
            new AccentTheme { Id="ember",  Name="琥珀", Accent=0xFFFFAD54, Accent2=0xFFFF5A6E, Dim=0xFFC98032, Glow=0x4AFFAD54, Wash=0x1EFFAD54, Ring=0xAAFFAD54 },
            new AccentTheme { Id="violet", Name="紫晶", Accent=0xFFB9A0FF, Accent2=0xFF6366F1, Dim=0xFF816AD1, Glow=0x4AB9A0FF, Wash=0x1EB9A0FF, Ring=0xAAB9A0FF },
            new AccentTheme { Id="mint",   Name="薄荷", Accent=0xFF44E4AC, Accent2=0xFF0EA5E9, Dim=0xFF2FA47D, Glow=0x4A44E4AC, Wash=0x1E44E4AC, Ring=0xAA44E4AC },
        };

        /// <summary>切换主题：联动双色相派生色、清画刷/画笔/渐变缓存、重建热力色阶。返回是否真的变了。</summary>
        public static bool ApplyTheme(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string key = id.Trim().ToLowerInvariant();
            if (key == "cobalt") key = "ice";
            else if (key == "verdant") key = "mint";

            AccentTheme th = Themes[0];
            bool found = false;
            for (int i = 0; i < Themes.Length; i++)
                if (Themes[i].Id == key) { th = Themes[i]; found = true; break; }
            if (!found) return false;
            if (th.Accent == _accent && th.Id == _themeId) return false;
            _themeId = th.Id;
            _accent = th.Accent;
            _accent2 = th.Accent2;
            _accentDim = th.Dim;
            _accentGlow = th.Glow;
            _accentWash = th.Wash;
            _accentRing = th.Ring;
            lock (_bcache) _bcache.Clear();
            lock (_pcache) _pcache.Clear();
            lock (_gcache) _gcache.Clear();
            _accentBevel = null;
            _heroGlow = null;
            _heroGlow2 = null;
            _capGrad = null;
            _accentGrad = null;
            _accentGradBright = null;
            _accentWashGrad = null;
            _heroPanelBg = null;
            Gfx.RebuildAccent(th.Accent);
            if (ThemeChanged != null) ThemeChanged();
            return true;
        }

        // ---- Rev78 · 数据语义色 ---------------------------------------------
        // 以前所有图表都是同一个青色：读者无法区分"这是主指标"还是"这只是个数"。
        // 现在按语义分配：主指标=强调色，健康=绿，劣化=琥珀，故障=红，中性序列=冷灰蓝。
        public const uint SeriesA   = 0xFF6E8BB0; // 中性序列（无好坏之分）
        public const uint SeriesB   = 0xFFA98BFF; // 第三序列
        public const uint GridMinor = 0x06FFFFFF; // 蓝图基底 · 细格
        public const uint GridMajor = 0x0FFFFFFF; // 蓝图基底 · 粗格

        // ---- Rev78 · 动效令牌 -----------------------------------------------
        // 散落在各处的 90/110/120/160/180/220/260ms 收敛为三档。
        public const int DurFast = 110;   // 状态反馈：按下、悬停
        public const int DurBase = 170;   // 控件过渡：开关、选中
        public const int DurSlow = 260;   // 入场、页面切换

        public const uint Ok        = 0xFF3FE0A4;
        public const uint OkWash    = 0x1A3FE0A4;
        public const uint Warn      = 0xFFFFB13C;
        public const uint WarnWash  = 0x1AFFB13C;
        public const uint Bad       = 0xFFFF5C63;
        public const uint BadWash   = 0x1AFF5C63;
        public const uint Violet    = 0xFFA98BFF;

        // ---- 字体 ----------------------------------------------------------
        // Bahnschrift = DIN 血统的工业无衬线，本设计的主力字体；
        // 中文回落 Microsoft YaHei UI；等宽数字用 Cascadia Mono。
        // 惰性初始化：字体族解析会触发 GDI 字体加载，全部预建会拖慢启动。
        private static FontFamily _fDisplay, _fDisplayReg, _fBody, _fCn, _fMono;


        public static FontFamily FDisplay
        {
            get { if (_fDisplay == null) _fDisplay = new FontFamily("Bahnschrift SemiBold, Microsoft YaHei UI"); return _fDisplay; }
        }
        public static FontFamily FDisplayReg
        {
            get { if (_fDisplayReg == null) _fDisplayReg = new FontFamily("Bahnschrift, Microsoft YaHei UI"); return _fDisplayReg; }
        }
        public static FontFamily FBody
        {
            get { if (_fBody == null) _fBody = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"); return _fBody; }
        }
        public static FontFamily FCn
        {
            get { if (_fCn == null) _fCn = new FontFamily("Microsoft YaHei UI"); return _fCn; }
        }
        public static FontFamily FMono
        {
            get { if (_fMono == null) _fMono = new FontFamily("Cascadia Mono, Consolas"); return _fMono; }
        }

        // ---- 尺度（DIP，设计基准 150% 缩放下 1DIP≈1.5px）-------------------
        public const double RailW     = 240;
        public const double TitleH    = 46;
        public const double PadX      = 26;
        public const double Gap       = 14;
        public const double Radius    = 12;
        public const double RadiusSm  = 6;
        public const double RadiusMd  = 8;
        public const double RadiusLg  = 14;

        // 字号 9 档（Rev120：主标题与数据读数更具视觉张力）
        public const double FsH1      = 20;
        public const double FsBody    = 13;
        public const double FsSmall   = 12;
        public const double FsMicro   = 11;
        public const double FsNano    = 10;
        public const double FsTitle   = 15;
        public const double FsPage    = 26;
        public const double FsNumL    = 32;
        public const double FsNumXL   = 40;
        public const double FsMega    = 62;
        public const double FsHero    = 78;

        // ---- 材质（Rev120 HYPERION GLASS）-----------------------------------
        /// <summary>面板受光面：明显抬升于深空画布之上，建立清晰前后景深。</summary>
        public const uint PlateLit   = 0xFF172338;
        /// <summary>面板背光面：下缘压深形成玻璃体量。</summary>
        public const uint PlateShade = 0xFF0A101D;
        /// <summary>底面：深空蓝曜画布。</summary>
        public const uint Deck       = 0xFF060A13;

        // 间距栅格（Rev67 收敛）：布局级 {8,16,18,24}，组件级 {4,8,12,16}
        public const double PadPanel  = 18;
        public const double PadInset  = 12;
        public const double BtnH      = 28;
        public const double BtnHSm    = 24;
        public const double BtnHLg    = 30;
        public const double BtnHXs    = 22;
        public const double ChipHSm   = 15;
        public const double ChipH     = 18;
        public const double ChipHLg   = 22;
        public const double GapIcon   = 8;
        public const double DisabledOpacity = 0.4;

        // ---- 工具 ----------------------------------------------------------
        private static readonly Dictionary<uint, SolidColorBrush> _bcache = new Dictionary<uint, SolidColorBrush>();

        // 画笔缓存键用 struct（颜色 + 宽度的精确位），取代旧的哈希折叠 uint——
        // 折叠值理论上会碰撞，碰撞时静默取到另一支笔。
        private struct PenKey : IEquatable<PenKey>
        {
            public uint Argb;
            public long W;
            public bool Equals(PenKey o) { return Argb == o.Argb && W == o.W; }
            public override bool Equals(object obj) { return obj is PenKey && Equals((PenKey)obj); }
            public override int GetHashCode() { unchecked { return (int)(Argb * 397) ^ (int)W ^ (int)(W >> 32); } }
        }
        private static readonly Dictionary<PenKey, Pen> _pcache = new Dictionary<PenKey, Pen>();

        public static Color C(uint argb)
        {
            return Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
                                  (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(uint argb, byte alpha)
        {
            Color c = C(argb);
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        /// <summary>取冻结的纯色画刷（带缓存）。</summary>
        public static SolidColorBrush S(uint argb)
        {
            SolidColorBrush b;
            lock (_bcache)
            {
                if (_bcache.TryGetValue(argb, out b)) return b;
                b = new SolidColorBrush(C(argb));
                b.Freeze();
                _bcache[argb] = b;
            }
            return b;
        }

        public static SolidColorBrush S(int argb) { return S((uint)argb); }

        public static Pen P(uint argb, double width)
        {
            PenKey key = new PenKey();
            key.Argb = argb;
            key.W = BitConverter.DoubleToInt64Bits(width);
            Pen p;
            lock (_pcache)
            {
                if (_pcache.TryGetValue(key, out p)) return p;
                p = new Pen(S(argb), width);
                p.Freeze();
                _pcache[key] = p;
            }
            return p;
        }

        public static Pen Pd(uint argb, double width, double[] dashes)
        {
            Pen p = new Pen(S(argb), width);
            p.DashStyle = new DashStyle(dashes, 0);
            p.Freeze();
            return p;
        }

        private struct GradKey : IEquatable<GradKey>
        {
            public uint From, To;
            public long Angle;
            public bool Equals(GradKey o) { return From == o.From && To == o.To && Angle == o.Angle; }
            public override bool Equals(object obj) { return obj is GradKey && Equals((GradKey)obj); }
            public override int GetHashCode() { unchecked { return (int)(From * 397) ^ (int)To ^ (int)Angle ^ (int)(Angle >> 32); } }
        }
        private static readonly Dictionary<GradKey, LinearGradientBrush> _gcache = new Dictionary<GradKey, LinearGradientBrush>();

        /// <summary>两段线性渐变（冻结 + 缓存）。面板底色只有几组固定组合，不值得每次重建。</summary>
        public static LinearGradientBrush Grad(uint from, uint to, double angle)
        {
            GradKey key = new GradKey();
            key.From = from; key.To = to;
            key.Angle = BitConverter.DoubleToInt64Bits(angle);
            lock (_gcache)
            {
                LinearGradientBrush hit;
                if (_gcache.TryGetValue(key, out hit)) return hit;
                LinearGradientBrush g = new LinearGradientBrush(C(from), C(to), angle);
                g.Freeze();
                _gcache[key] = g;
                return g;
            }
        }

        private static LinearGradientBrush _bevel;
        private static LinearGradientBrush _accentBevel;
        private static RadialGradientBrush _heroGlow;
        private static RadialGradientBrush _heroGlow2;
        private static LinearGradientBrush _capGrad;
        private static LinearGradientBrush _accentGrad;
        private static LinearGradientBrush _accentGradBright;
        private static LinearGradientBrush _accentWashGrad;
        private static LinearGradientBrush _glassPanelBg;
        private static LinearGradientBrush _heroPanelBg;
        private static LinearGradientBrush _subCardBg;
        private static LinearGradientBrush _railBg;

        /// <summary>从左至右的"机加工高光"：玻璃面板顶部 1px 折光亮边。</summary>
        public static LinearGradientBrush Bevel()
        {
            if (_bevel != null) return _bevel;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0); g.EndPoint = new Point(1, 0);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 210, 234, 255), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(88, 224, 240, 255), 0.38));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(42, 210, 234, 255), 0.72));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 210, 234, 255), 1));
            g.Freeze();
            _bevel = g;
            return g;
        }

        /// <summary>主视口面板专属顶部双色相光学折射棱（随主题联动、冻结共享）。</summary>
        public static LinearGradientBrush AccentBevel()
        {
            if (_accentBevel != null) return _accentBevel;
            Color ac = C(_accent);
            Color ac2 = C(_accent2);
            Color hi = Mix(ac, Color.FromArgb(255, 255, 255, 255), 0.55);
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0); g.EndPoint = new Point(1, 0);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac.R, ac.G, ac.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xE8, hi.R, hi.G, hi.B), 0.22));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xB4, ac.R, ac.G, ac.B), 0.48));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x52, ac2.R, ac2.G, ac2.B), 0.76));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac2.R, ac2.G, ac2.B), 1));
            g.Freeze();
            _accentBevel = g;
            return g;
        }

        /// <summary>双色相对角能量渐变（主按钮、激活态开关、高亮指标条共享）。</summary>
        public static LinearGradientBrush AccentGrad()
        {
            if (_accentGrad != null) return _accentGrad;
            Color ac = C(_accent);
            Color ac2 = C(_accent2);
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0); g.EndPoint = new Point(1, 1);
            g.GradientStops.Add(new GradientStop(Mix(ac, Color.FromArgb(255, 255, 255, 255), 0.14), 0));
            g.GradientStops.Add(new GradientStop(ac, 0.48));
            g.GradientStops.Add(new GradientStop(ac2, 1));
            g.Freeze();
            _accentGrad = g;
            return g;
        }

        /// <summary>悬停提亮版双色相能量渐变。</summary>
        public static LinearGradientBrush AccentGradBright()
        {
            if (_accentGradBright != null) return _accentGradBright;
            Color ac = C(_accent);
            Color ac2 = C(_accent2);
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0); g.EndPoint = new Point(1, 1);
            g.GradientStops.Add(new GradientStop(Mix(ac, Color.FromArgb(255, 255, 255, 255), 0.36), 0));
            g.GradientStops.Add(new GradientStop(Mix(ac, Color.FromArgb(255, 255, 255, 255), 0.16), 0.55));
            g.GradientStops.Add(new GradientStop(Mix(ac2, Color.FromArgb(255, 255, 255, 255), 0.14), 1));
            g.Freeze();
            _accentGradBright = g;
            return g;
        }

        /// <summary>导航选中行 / 特色卡片的横向微光渐隐底。</summary>
        public static LinearGradientBrush AccentWashGrad()
        {
            if (_accentWashGrad != null) return _accentWashGrad;
            Color ac = C(_accent);
            Color ac2 = C(_accent2);
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0.5); g.EndPoint = new Point(1, 0.5);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x36, ac.R, ac.G, ac.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, ac2.R, ac2.G, ac2.B), 0.62));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x06, ac2.R, ac2.G, ac2.B), 1));
            g.Freeze();
            _accentWashGrad = g;
            return g;
        }

        /// <summary>标准玻璃卡片底色：深邃蓝曜渐变，与深空底色拉开清晰空间层级。</summary>
        public static LinearGradientBrush GlassPanelBg()
        {
            if (_glassPanelBg != null) return _glassPanelBg;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0.2, 0); g.EndPoint = new Point(0.8, 1);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x16, 0x22, 0x38), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x10, 0x18, 0x29), 0.52));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x0B, 0x11, 0x1F), 1));
            g.Freeze();
            _glassPanelBg = g;
            return g;
        }

        /// <summary>主焦点 Hero 玻璃卡片底色：微带主题色温的深邃高透玻璃。</summary>
        public static LinearGradientBrush HeroPanelBg()
        {
            if (_heroPanelBg != null) return _heroPanelBg;
            Color ac2 = C(_accent2);
            Color top = Mix(Color.FromArgb(0xFF, 0x19, 0x27, 0x41), ac2, 0.08);
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0.15, 0); g.EndPoint = new Point(0.85, 1);
            g.GradientStops.Add(new GradientStop(top, 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x11, 0x1A, 0x2D), 0.52));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x0B, 0x11, 0x20), 1));
            g.Freeze();
            _heroPanelBg = g;
            return g;
        }

        /// <summary>嵌套 Bento 指标子卡底色：略微内凹的光学玻璃单元。</summary>
        public static LinearGradientBrush SubCardBg()
        {
            if (_subCardBg != null) return _subCardBg;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0.5, 0); g.EndPoint = new Point(0.5, 1);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xE8, 0x0E, 0x16, 0x27), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0x09, 0x0E, 0x1A), 1));
            g.Freeze();
            _subCardBg = g;
            return g;
        }

        /// <summary>左侧导航坞磨砂深空渐变。</summary>
        public static LinearGradientBrush RailBg()
        {
            if (_railBg != null) return _railBg;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0); g.EndPoint = new Point(0.3, 1);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x0B, 0x11, 0x20), 0.55));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x07, 0x0B, 0x16), 1));
            g.Freeze();
            _railBg = g;
            return g;
        }

        /// <summary>主视口面板左上极光氛围光场（冻结共享，切主题自动清空重建）。</summary>
        public static RadialGradientBrush HeroGlow()
        {
            if (_heroGlow != null) return _heroGlow;
            Color ac = C(_accent);
            RadialGradientBrush rgb = new RadialGradientBrush();
            rgb.Center = new Point(0.11, 0.08);
            rgb.GradientOrigin = new Point(0.11, 0.08);
            rgb.RadiusX = 0.68;
            rgb.RadiusY = 0.94;
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, ac.R, ac.G, ac.B), 0));
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, ac.R, ac.G, ac.B), 0.44));
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac.R, ac.G, ac.B), 1));
            rgb.Freeze();
            _heroGlow = rgb;
            return rgb;
        }

        /// <summary>主视口面板右下伴生副色反光场（与左上主光场形成对角景深）。</summary>
        public static RadialGradientBrush HeroGlow2()
        {
            if (_heroGlow2 != null) return _heroGlow2;
            Color ac2 = C(_accent2);
            RadialGradientBrush rgb = new RadialGradientBrush();
            rgb.Center = new Point(0.92, 0.95);
            rgb.GradientOrigin = new Point(0.92, 0.95);
            rgb.RadiusX = 0.58;
            rgb.RadiusY = 0.80;
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(0x28, ac2.R, ac2.G, ac2.B), 0));
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(0x0C, ac2.R, ac2.G, ac2.B), 0.50));
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac2.R, ac2.G, ac2.B), 1));
            rgb.Freeze();
            _heroGlow2 = rgb;
            return rgb;
        }

        /// <summary>页头标尺端帽双色相光学渐变画刷。</summary>
        public static LinearGradientBrush CapGrad()
        {
            if (_capGrad != null) return _capGrad;
            Color ac = C(_accent);
            Color ac2 = C(_accent2);
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0); g.EndPoint = new Point(1, 0);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, ac.R, ac.G, ac.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xDC, ac2.R, ac2.G, ac2.B), 0.58));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, ac2.R, ac2.G, ac2.B), 1));
            g.Freeze();
            _capGrad = g;
            return g;
        }

        private static LinearGradientBrush _sheen;
        private static LinearGradientBrush _shade;

        /// <summary>斜向玻璃反光：左上角柔白漫反射，让面板呈现立体光学玻璃质感。</summary>
        public static LinearGradientBrush Sheen()
        {
            if (_sheen != null) return _sheen;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0);
            g.EndPoint = new Point(0.62, 1);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0xD8, 0xEC, 0xFF), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x07, 0xD8, 0xEC, 0xFF), 0.36));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xD8, 0xEC, 0xFF), 1));
            g.Freeze();
            _sheen = g;
            return g;
        }

        /// <summary>下缘内阴影：赋予玻璃卡片清晰的厚度与悬浮感。</summary>
        public static LinearGradientBrush Shade()
        {
            if (_shade != null) return _shade;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0.5, 0);
            g.EndPoint = new Point(0.5, 1);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x4E, 0x00, 0x00, 0x00), 1));
            g.Freeze();
            _shade = g;
            return g;
        }

        private static LinearGradientBrush _deck;

        /// <summary>整窗深空蓝曜基底：微带宇宙深蓝纵深，杜绝死板纯黑。</summary>
        public static LinearGradientBrush DeckBrush()
        {
            if (_deck != null) return _deck;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0.3, 0);
            g.EndPoint = new Point(0.7, 1);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x0A, 0x10, 0x20), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x06, 0x0A, 0x14), 0.55));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x04, 0x07, 0x0F), 1));
            g.Freeze();
            _deck = g;
            return g;
        }

        private static readonly Dictionary<string, Geometry> _geo = new Dictionary<string, Geometry>();

        /// <summary>解析 SVG 迷你路径语言并冻结（用于图标与曲线）。</summary>
        public static Geometry G(string data)
        {
            Geometry g;
            lock (_geo)
            {
                if (_geo.TryGetValue(data, out g)) return g;
                g = Geometry.Parse(data);
                g.Freeze();
                _geo[data] = g;
            }
            return g;
        }

    }
}