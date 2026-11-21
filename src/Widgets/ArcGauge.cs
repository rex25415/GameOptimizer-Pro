// ============================================================================
//  ArcGauge.cs — 弧形仪表（244° 刻度弧 + 圆头进度 + 前端光点）
//  零分配：49 根刻度只用 4 支缓存画笔；背景弧按尺寸缓存；文本走 Ui。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go
{
    public class ArcGauge : Grid
    {
        private readonly ArcRender _render;
        public TextBlock ValueText;
        public TextBlock UnitText;
        public TextBlock Caption;

        public ArcGauge(double value, string big, string unit, string caption)
        {
            _render = new ArcRender();
            Children.Add(_render);

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Margin = new Thickness(0, 6, 0, 0);

            ValueText = new TextBlock();
            ValueText.Text = big;
            ValueText.FontSize = T.FsNumXL;
            ValueText.FontFamily = T.FDisplay;
            ValueText.FontWeight = FontWeights.SemiBold;
            ValueText.Foreground = T.S(T.Hi);
            ValueText.HorizontalAlignment = HorizontalAlignment.Center;
            TextOptions.SetTextFormattingMode(ValueText, TextFormattingMode.Ideal);
            sp.Children.Add(ValueText);

            UnitText = new TextBlock();
            UnitText.Text = Ui.Spaced(unit.ToUpperInvariant());
            UnitText.FontSize = T.FsNano;
            UnitText.FontFamily = T.FDisplayReg;
            UnitText.Foreground = T.S(T.Dim);
            UnitText.HorizontalAlignment = HorizontalAlignment.Center;
            UnitText.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(UnitText);

            Border sep = new Border();
            sep.Width = 22;
            sep.Height = 1;
            sep.Background = T.S(0x28FFFFFF);
            sep.HorizontalAlignment = HorizontalAlignment.Center;
            sep.Margin = new Thickness(0, 6, 0, 0);
            sep.IsHitTestVisible = false;
            sp.Children.Add(sep);

            Caption = new TextBlock();
            Caption.Text = caption;
            Caption.FontSize = T.FsSmall;
            Caption.FontFamily = T.FCn;
            Caption.Foreground = T.S(T.Mute);
            Caption.HorizontalAlignment = HorizontalAlignment.Center;
            Caption.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(Caption);

            Children.Add(sp);
            _render.Value = value;
        }

        public double Value
        {
            get { return _render.Value; }
            set { _render.Value = value; }
        }

        public uint Tint
        {
            get { return _render.Tint; }
            set { _render.Tint = value; }
        }
    }

    /// <summary>仪表盘绘制层。OnRender 内不产生任何画笔/画刷对象。</summary>
    public class ArcRender : FrameworkElement
    {
        private const double StartDeg = 148, SweepDeg = 244;
        private const int Ticks = 49;

        private double _value;
        public double Value
        {
            get { return _value; }
            set
            {
                double v = Math.Max(0, Math.Min(1, value));
                if (v == _value) return;
                _value = v;
                InvalidateVisual();
            }
        }

        /// <summary>是否刻 0/25/50/75/100 量纲数字。小尺寸仪表关掉，避免糊成一团。</summary>
        public bool _showScale = true;

        private uint _tint = T.Accent;
        public uint Tint
        {
            get { return _tint; }
            set { if (_tint == value) return; _tint = value; InvalidateVisual(); }
        }

        // 背景弧随尺寸变化，缓存一份
        private Geometry _bgArc;
        private double _bgR;

        // 前景弧：按 1% 量化缓存。仪表上的数字本来就只有 0..100 这 101 个取值，
        // 之前每帧新建一条 StreamGeometry（238 B/帧），是全部残差的来源。
        private readonly Geometry[] _fg = new Geometry[101];
        private int _fgQ = -1;
        private double _fgR;
        private Point _bgC;
        // 0/25/50/75/100 精密刻度微标线与外圈分度：按 (c, r) 冻结缓存，OnRender 零分配。
        private Geometry _subMarkLo;
        private Geometry _subMarkHi;
        private Geometry _bezelMarks;
        private double _markR;
        private Point _markC;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            // 半径必须给外圈留够位置：刻度圈在 r+9..r+18、量纲数字在 r+25、机加工环在 r+34。
            // 旧值是 -10，加上外圈之后整圈会被元素边界切掉（顶部和底部各少一截），
            // 看起来像"仪表装歪了"。这里按最外层的需要反推。
            double r = Math.Min(w, h) / 2 - 32;
            Point c = new Point(w / 2, h / 2 + 2);

            // 几何是烘在具体坐标上的：只按半径失效的话，尺寸变了（同一半径、不同圆心）
            // 弧会画在旧圆心上——仪表看起来"装歪了"，而且只在某些窗口尺寸下出现。
            if (_bgArc == null || Math.Abs(_bgR - r) > 0.5 || Math.Abs(_bgC.X - c.X) > 0.5 || Math.Abs(_bgC.Y - c.Y) > 0.5)
            {
                _bgArc = Arc(c, r, StartDeg, SweepDeg);
                _bgR = r;
                _bgC = c;
            }
            // Rev120 HYPERION：双层内凹精密表盘 + 状态色径向中心辉光 + 内轨道标尺（全零分配）
            double innerR = Math.Max(12, r - 9.5);
            dc.DrawEllipse(T.S(0xB8060B16), Gfx.P(0x1ECFE6FF, 1.0), c, innerR, innerR);
            dc.DrawEllipse(Gfx.RadialAura(_tint, 0x3E), null, c, innerR - 1, innerR - 1);
            if (innerR > 20)
                dc.DrawEllipse(null, Gfx.Dash(TintA(_tint, 0x36), 1.0, 2), c, innerR - 6, innerR - 6);
            dc.DrawGeometry(null, Gfx.P(0x22CFE6FF, 3.0), _bgArc);
            DrawSubMarks(dc, c, r);

            // ---- 分区带（Rev79）------------------------------------------
            // 0-50 红 / 50-75 琥珀 / 75-100 绿，画在刻度圈外侧的细带上。
            // 它回答的是"这个刻度是好还是坏"——纯数字的弧回答不了这个问题，
            // 而就绪度这个指标的全部意义恰恰在于"够不够"。
            DrawZones(dc, c, r + 15);

            // 刻度：4 支画笔覆盖「主/次 × 亮/暗」，无逐根分配
            Pen majorOn = Gfx.P(TintA(_tint, 0xCC), 1.4);
            Pen minorOn = Gfx.P(TintA(_tint, 0x77), 1.0);
            Pen majorOff = Gfx.P(0x2AFFFFFF, 1.4);
            Pen minorOff = Gfx.P(T.Line, 1.0);
            for (int i = 0; i < Ticks; i++)
            {
                double t = (double)i / (Ticks - 1);
                double ang = (StartDeg + SweepDeg * t) * Math.PI / 180.0;
                bool lit = t <= _value;
                bool major = (i % 4 == 0);
                double len = major ? 9 : 5;
                double r1 = r + 9, r2 = r + 9 + len;
                double ca = Math.Cos(ang), sa = Math.Sin(ang);
                Pen p = lit ? (major ? majorOn : minorOn) : (major ? majorOff : minorOff);
                dc.DrawLine(p, new Point(c.X + ca * r1, c.Y + sa * r1),
                               new Point(c.X + ca * r2, c.Y + sa * r2));
            }

            if (_value > 0.001)
            {
                int q = (int)Math.Round(_value * 100);
                if (q < 1) q = 1; else if (q > 100) q = 100;
                if (Math.Abs(_fgR - r) > 0.5)
                {
                    // 半径变了，全部缓存作废（否则会画出一个旧尺寸的弧）
                    Array.Clear(_fg, 0, _fg.Length);
                    _fgR = r;
                    _fgQ = -1;
                }
                if (q != _fgQ || _fg[q] == null)
                {
                    _fg[q] = Arc(c, r, StartDeg, SweepDeg * (q / 100.0));
                    _fgQ = q;
                }
                // 四层光学进度弧：广域大气晕染 + 中辉光 + 锐利主芯 + 白炽内折光丝（复用同一冻结几何，0 额外分配）
                dc.DrawGeometry(null, Gfx.Round(TintA(_tint, 0x1A), 12.5), _fg[q]);
                dc.DrawGeometry(null, Gfx.Round(TintA(_tint, 0x42), 7.0), _fg[q]);
                dc.DrawGeometry(null, Gfx.Round(_tint, 3.6), _fg[q]);
                dc.DrawGeometry(null, Gfx.Round(0x98FFFFFF, 1.1), _fg[q]);

                double ang2 = (StartDeg + SweepDeg * _value) * Math.PI / 180.0;
                Point tip = new Point(c.X + Math.Cos(ang2) * r, c.Y + Math.Sin(ang2) * r);
                dc.DrawEllipse(T.S(TintA(_tint, 0x2A)), null, tip, 11.5, 11.5);
                dc.DrawEllipse(T.S(TintA(_tint, 0x56)), null, tip, 6.8, 6.8);
                dc.DrawEllipse(T.S(0xFF070C17), null, tip, 4.2, 4.2);
                dc.DrawEllipse(T.S(_tint), null, tip, 2.8, 2.8);
                dc.DrawEllipse(T.S(0xF5FFFFFF), null, tip, 1.3, 1.3);
            }

            // 外圈精密双环：让仪表拥有高级机械腕表级光学外框
            double bezR = r + 32;
            dc.DrawEllipse(null, Gfx.P(0x20CFE6FF, 1.1), c, bezR, bezR);
            dc.DrawEllipse(null, Gfx.P(TintA(_tint, 0x26), 1.0), c, bezR + 2.5, bezR + 2.5);
            if (_showScale) DrawScale(dc, c, r, Gfx.Dpi(this));
        }

        private static uint TintA(uint c, byte a) { return ((uint)a << 24) | (c & 0x00FFFFFFu); }

        private void DrawSubMarks(DrawingContext dc, Point c, double r)
        {
            if (_subMarkLo == null || Math.Abs(_markR - r) > 0.5 || Math.Abs(_markC.X - c.X) > 0.5 || Math.Abs(_markC.Y - c.Y) > 0.5)
            {
                _subMarkLo = BuildSubMarks(c, r, 0, 2);
                _subMarkHi = BuildSubMarks(c, r, 3, 4);
                _bezelMarks = BuildBezelMarks(c, r + 32);
                _markR = r;
                _markC = c;
            }
            if (_subMarkLo != null) dc.DrawGeometry(null, Gfx.P(0x46FFFFFF, 1.1), _subMarkLo);
            if (_subMarkHi != null) dc.DrawGeometry(null, Gfx.P(0x663FE0A4, 1.2), _subMarkHi);
            if (_bezelMarks != null) dc.DrawGeometry(null, Gfx.P(0x1CFFFFFF, 1.0), _bezelMarks);
        }

        private static Geometry BuildSubMarks(Point c, double r, int qStart, int qEnd)
        {
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                for (int q = qStart; q <= qEnd; q++)
                {
                    double deg = StartDeg + SweepDeg * (q / 4.0);
                    double rad = deg * Math.PI / 180.0;
                    double ca = Math.Cos(rad), sa = Math.Sin(rad);
                    // 内侧机械定位微标 + 主弧与刻度环之间的短分度针
                    ctx.BeginFigure(new Point(c.X + ca * (r - 6.5), c.Y + sa * (r - 6.5)), false, false);
                    ctx.LineTo(new Point(c.X + ca * (r - 2.6), c.Y + sa * (r - 2.6)), true, false);
                    ctx.BeginFigure(new Point(c.X + ca * (r + 5.0), c.Y + sa * (r + 5.0)), false, false);
                    ctx.LineTo(new Point(c.X + ca * (r + 8.6), c.Y + sa * (r + 8.6)), true, false);

                    // 25/50/75 两侧微翼线，增强机械表盘刻线层级
                    if (q > 0 && q < 4)
                    {
                        double fl = (deg - 2.2) * Math.PI / 180.0;
                        double fr = (deg + 2.2) * Math.PI / 180.0;
                        ctx.BeginFigure(new Point(c.X + Math.Cos(fl) * (r - 4.6), c.Y + Math.Sin(fl) * (r - 4.6)), false, false);
                        ctx.LineTo(new Point(c.X + Math.Cos(fl) * (r - 2.8), c.Y + Math.Sin(fl) * (r - 2.8)), true, false);
                        ctx.BeginFigure(new Point(c.X + Math.Cos(fr) * (r - 4.6), c.Y + Math.Sin(fr) * (r - 4.6)), false, false);
                        ctx.LineTo(new Point(c.X + Math.Cos(fr) * (r - 2.8), c.Y + Math.Sin(fr) * (r - 2.8)), true, false);
                    }
                }
            }
            g.Freeze();
            return g;
        }

        private static Geometry BuildBezelMarks(Point c, double bezR)
        {
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                for (int i = 0; i < 24; i++)
                {
                    double a = i * 15.0 * Math.PI / 180.0;
                    double ca = Math.Cos(a), sa = Math.Sin(a);
                    double len = (i % 6 == 0) ? 3.5 : 2.0;
                    ctx.BeginFigure(new Point(c.X + ca * (bezR - len), c.Y + sa * (bezR - len)), false, false);
                    ctx.LineTo(new Point(c.X + ca * bezR, c.Y + sa * bezR), true, false);
                }
            }
            g.Freeze();
            return g;
        }

        // 三条分区带按半径缓存。画一条弧要开一个 StreamGeometry —— 如果每帧重建，
        // 单这一项就是 60 B/帧（验收里从 225 涨到 286 就是这么来的）。
        // 半径只在改变窗口大小时才会变，所以缓存窗口是完整的。
        private readonly Geometry[] _zones = new Geometry[3];
        private readonly Pen[] _zonePens = new Pen[3];
        private double _zoneR;

        private static readonly double[] ZoneFrom = new double[] { 0.00, 0.50, 0.75 };
        private static readonly double[] ZoneTo   = new double[] { 0.50, 0.75, 1.00 };
        private static readonly uint[]   ZoneCol  = new uint[] { 0x3EFF5C63, 0x3EFFB13C, 0x3E3FE0A4 };

        private void DrawZones(DrawingContext dc, Point c, double r)
        {
            if (_zones[0] == null || Math.Abs(_zoneR - r) > 0.5)
            {
                for (int i = 0; i < 3; i++)
                {
                    _zones[i] = ZoneArc(c, r, ZoneFrom[i], ZoneTo[i]);
                    _zonePens[i] = Gfx.Round(ZoneCol[i], 2.5);
                }
                _zoneR = r;
            }
            for (int i = 0; i < 3; i++) dc.DrawGeometry(null, _zonePens[i], _zones[i]);
        }

        private static Geometry ZoneArc(Point c, double r, double from, double to)
        {
            double a0 = (StartDeg + SweepDeg * from) * Math.PI / 180.0;
            double a1 = (StartDeg + SweepDeg * to) * Math.PI / 180.0;
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r), false, false);
                ctx.ArcTo(new Point(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r),
                          new Size(r, r), 0, (to - from) > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        // 刻度数字必须是常量表：i.ToString() 每帧会分配 5 个字符串，
        // 在 60 次/秒的重绘里就是 300 个/秒的稳定垃圾 —— 验收读数直接从 225 涨到 286 就是这么来的。
        private static readonly string[] ScaleText = new string[] { "0", "25", "50", "75", "100" };

        /// <summary>把 0 / 50 / 100 刻在刻度圈外侧：读者要先知道量纲，才知道 68 算高还是低。</summary>
        private void DrawScale(DrawingContext dc, Point c, double r, double dpi)
        {
            for (int i = 0; i <= 4; i++)
            {
                double v = i / 4.0;
                int pct = i * 25;
                FormattedText ft = Gfx.Text(ScaleText[i], 8.5, pct >= 75 ? T.Ok : (pct >= 50 ? T.Warn : T.Mute), 0, dpi);
                double ang = (StartDeg + SweepDeg * v) * Math.PI / 180.0;
                double rr = r + 22;
                dc.DrawText(ft, new Point(c.X + Math.Cos(ang) * rr - ft.Width / 2,
                                          c.Y + Math.Sin(ang) * rr - ft.Height / 2));
            }
        }

        private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
        {
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                double a0 = startDeg * Math.PI / 180.0;
                double a1 = (startDeg + sweepDeg) * Math.PI / 180.0;
                Point p0 = new Point(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r);
                Point p1 = new Point(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r);
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
