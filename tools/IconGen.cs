// ============================================================================
//  IconGen.cs — 多尺寸 .ico 生成器
//  几何来自 src\Brand.cs（与界面同一份常量，随主程序一起编译），
//  因此「侧栏标记」与「exe/快捷方式图标」在造型上不可能漂移。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Go
{
    public static class IconGen
    {
        private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        [STAThread]
        public static int Main(string[] args)
        {
            string outPath = "app.ico";
            uint accent = Brand.Accent;
            uint dotTint = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--state" && i + 1 < args.Length)
                {
                    string st = args[++i].ToLowerInvariant();
                    if (st == "idle") { accent = 0xFF38BDF8; dotTint = 0xFF38BDF8; }
                    else if (st == "guard") { accent = 0xFF10B981; dotTint = 0xFF10B981; }
                    else if (st == "alert") { accent = 0xFFF59E0B; dotTint = 0xFFF59E0B; }
                }
                else
                {
                    outPath = args[i];
                }
            }

            List<byte[]> frames = new List<byte[]>();
            List<int> used = new List<int>();
            foreach (int sz in Sizes) { frames.Add(Render(sz, accent, dotTint)); used.Add(sz); }

            using (FileStream fs = new FileStream(outPath, FileMode.Create))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)used.Count);
                int offset = 6 + 16 * used.Count;
                for (int i = 0; i < used.Count; i++)
                {
                    w.Write((byte)(used[i] >= 256 ? 0 : used[i]));
                    w.Write((byte)(used[i] >= 256 ? 0 : used[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write((uint)frames[i].Length);
                    w.Write((uint)offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] f in frames) w.Write(f);
            }
            Console.WriteLine("ico written: " + outPath + " (" + used.Count + " sizes)");
            return 0;
        }

        private static byte[] Render(int size) { return Render(size, Brand.Accent, 0); }

        private static byte[] Render(int size, uint accent, uint dotTint)
        {
            double s = size;
            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                double radius = s * 0.225;

                // 底板：与侧栏同一族的石墨渐变
                LinearGradientBrush plate = new LinearGradientBrush(
                    Col(Brand.PlateTop), Col(Brand.PlateBottom), 90);
                plate.Freeze();
                dc.DrawRoundedRectangle(plate, null, new Rect(0, 0, s, s), radius, radius);

                // Rev110 AETHER：大尺寸图标叠加左上角冰蓝环境光场
                if (size >= 40)
                {
                    Color acCol = Col(accent);
                    RadialGradientBrush aura = new RadialGradientBrush(
                        Color.FromArgb(0x34, acCol.R, acCol.G, acCol.B),
                        Color.FromArgb(0x00, acCol.R, acCol.G, acCol.B));
                    aura.Center = new Point(0.24, 0.18);
                    aura.GradientOrigin = new Point(0.24, 0.18);
                    aura.RadiusX = 0.78;
                    aura.RadiusY = 0.72;
                    aura.Freeze();
                    dc.DrawRoundedRectangle(aura, null, new Rect(0, 0, s, s), radius, radius);
                }

                // 顶部机加高光（1px 级）
                LinearGradientBrush lip = new LinearGradientBrush(
                    Color.FromArgb(size >= 64 ? (byte)46 : (byte)18, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255), 90);
                lip.Freeze();
                dc.DrawRoundedRectangle(lip, null, new Rect(0, 0, s, s * 0.44), radius, radius);

                // 1px 边缘：16px 下这条线会显得笨重，直接省略
                if (size >= 24)
                {
                    Color acCol = Col(accent);
                    uint edgeCol = dotTint != 0 ? ((0x66u << 24) | (dotTint & 0x00FFFFFFu)) : Brand.PlateEdge;
                    Brush edgeBrush;
                    if (dotTint == 0 && size >= 40)
                    {
                        LinearGradientBrush eb = new LinearGradientBrush(
                            Color.FromArgb(0x66, acCol.R, acCol.G, acCol.B),
                            Col(edgeCol), 90);
                        eb.Freeze();
                        edgeBrush = eb;
                    }
                    else
                    {
                        SolidColorBrush sb = new SolidColorBrush(Col(edgeCol));
                        sb.Freeze();
                        edgeBrush = sb;
                    }
                    Pen edge = new Pen(edgeBrush, Math.Max(1.0, s / 128.0));
                    edge.Freeze();
                    dc.DrawRoundedRectangle(null, edge, new Rect(0.5, 0.5, s - 1, s - 1), radius, radius);
                }

                // ---- 标记：与界面完全同源 ----
                // 小尺寸留白收紧、描边加粗，保证 16px 仍可辨识；大尺寸回到母版比例。
                // 小尺寸做「光学简化」：留白更紧、描边略粗；16px 下内层人字会与主线粘连，
                // 因此只保留外轮廓——这是图标在不同尺寸下的必要取舍，不是造型漂移。
                bool tinySize = size <= 20;
                double pad = tinySize ? s * 0.10 : s * 0.175;
                double draw = s - pad * 2;
                double k = draw / 24.0;
                double boost = tinySize ? 1.15 : (size <= 48 ? 1.10 : 1.0);

                TransformGroup tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(k, k));
                tg.Children.Add(new TranslateTransform(pad, pad));

                Geometry gOuter = Geometry.Parse(Brand.MarkPath).Clone();
                gOuter.Transform = tg;
                gOuter.Freeze();

                Geometry gInner = Geometry.Parse(Brand.MarkInnerPath).Clone();
                gInner.Transform = tg;
                gInner.Freeze();

                if (size >= 40)
                {
                    Color acCol = Col(accent);
                    Pen pGlow = new Pen(new SolidColorBrush(Color.FromArgb(0x2E, acCol.R, acCol.G, acCol.B)), Brand.MarkStroke * k * boost * 2.6);
                    pGlow.LineJoin = PenLineJoin.Round;
                    pGlow.StartLineCap = PenLineCap.Round;
                    pGlow.EndLineCap = PenLineCap.Round;
                    pGlow.Freeze();
                    dc.DrawGeometry(null, pGlow, gOuter);
                }

                Pen pOuter = new Pen(new SolidColorBrush(Col(accent)), Brand.MarkStroke * k * boost);
                pOuter.LineJoin = PenLineJoin.Miter;
                pOuter.StartLineCap = PenLineCap.Round;
                pOuter.EndLineCap = PenLineCap.Round;
                pOuter.Freeze();
                dc.DrawGeometry(null, pOuter, gOuter);

                if (!tinySize)
                {
                    uint innerCol = dotTint != 0 ? ((0xAAu << 24) | (accent & 0x00FFFFFFu)) : Brand.AccentDim;
                    Pen pInner = new Pen(new SolidColorBrush(Col(innerCol)), Brand.MarkInnerStroke * k * boost);
                    pInner.LineJoin = PenLineJoin.Round;
                    pInner.StartLineCap = PenLineCap.Round;
                    pInner.EndLineCap = PenLineCap.Round;
                    pInner.Freeze();
                    dc.DrawGeometry(null, pInner, gInner);
                }

                if (dotTint != 0)
                {
                    double br = Math.Max(2.2, s * 0.16);
                    double cx = s - br - Math.Max(1.0, s * 0.06);
                    double cy = s - br - Math.Max(1.0, s * 0.06);
                    SolidColorBrush halo = new SolidColorBrush(Color.FromArgb(0xFF, 0x05, 0x07, 0x0B));
                    halo.Freeze();
                    SolidColorBrush dot = new SolidColorBrush(Col(dotTint));
                    dot.Freeze();
                    dc.DrawEllipse(halo, null, new Point(cx, cy), br + Math.Max(1.2, s * 0.05), br + Math.Max(1.2, s * 0.05));
                    dc.DrawEllipse(dot, null, new Point(cx, cy), br, br);
                }
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (MemoryStream ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        private static Color Col(uint argb)
        {
            return Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
                                  (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        }
    }
}