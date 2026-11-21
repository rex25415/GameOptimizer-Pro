// ============================================================================
//  Graphics.Canvas.cs — Rev130 图形栈：实测坐标虚拟多屏蓝图画布 (DisplayStageCanvas)
//  按 EnumDisplayDevices / EnumDisplaySettingsEx 返回的真实 (PosX, PosY, W, H, Hz, bpp)
//  等比例绘制活动多屏拓扑，配玻璃屏框、屏幕径向微光、主屏光棱与机加工支架。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Go.Pages
{
    internal sealed class DisplayStageCanvas : FrameworkElement
    {
        private List<Hardware.DisplayInfo> _displays = new List<Hardware.DisplayInfo>();

        public DisplayStageCanvas()
        {
            Height = 166;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        public void SetDisplays(List<Hardware.DisplayInfo> list)
        {
            _displays = list ?? new List<Hardware.DisplayInfo>();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 80 || h < 60) return;
            double dpi = Gfx.Dpi(this);

            // 1) 深空仪器蓝图底板 + 细微坐标网格
            Rect outer = new Rect(0.5, 0.5, w - 1, h - 1);
            dc.DrawRoundedRectangle(T.S(0xC8070C17), Gfx.P(T.LineSoft, 1), outer, 8, 8);

            Pen gridPen = Gfx.P(0x0CCFE6FF, 1);
            for (double gx = 28; gx < w - 10; gx += 28)
                dc.DrawLine(gridPen, new Point(gx, 6), new Point(gx, h - 6));
            for (double gy = 28; gy < h - 10; gy += 28)
                dc.DrawLine(gridPen, new Point(6, gy), new Point(w - 6, gy));

            if (_displays == null || _displays.Count == 0)
            {
                FormattedText emp = Gfx.Text("未检测到活动显示输出", T.FsSmall, T.Mute, 0, dpi);
                dc.DrawText(emp, new Point((w - emp.Width) / 2, (h - emp.Height) / 2));
                return;
            }

            // 2) 计算所有显示器的真实桌面包围盒
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < _displays.Count; i++)
            {
                Hardware.DisplayInfo d = _displays[i];
                double dw = d.Width > 0 ? d.Width : 1920;
                double dh = d.Height > 0 ? d.Height : 1080;
                if (d.PosX < minX) minX = d.PosX;
                if (d.PosY < minY) minY = d.PosY;
                if (d.PosX + dw > maxX) maxX = d.PosX + dw;
                if (d.PosY + dh > maxY) maxY = d.PosY + dh;
            }
            double spanW = Math.Max(1.0, maxX - minX);
            double spanH = Math.Max(1.0, maxY - minY);

            double padX = 28, padT = 18, padB = 26;
            double availW = Math.Max(40, w - padX * 2);
            double availH = Math.Max(40, h - padT - padB);
            double scale = Math.Min(availW / spanW, availH / spanH);
            // 单屏时不撑到畸形巨宽：封顶高度约 116px 保持精致比例
            if (_displays.Count == 1 && spanH * scale > 116) scale = 116.0 / spanH;

            double drawW = spanW * scale;
            double drawH = spanH * scale;
            double ox = (w - drawW) / 2.0;
            double oy = padT + (availH - drawH) / 2.0;

            for (int i = 0; i < _displays.Count; i++)
            {
                Hardware.DisplayInfo d = _displays[i];
                double dw = (d.Width > 0 ? d.Width : 1920) * scale;
                double dh = (d.Height > 0 ? d.Height : 1080) * scale;
                double rx = ox + (d.PosX - minX) * scale;
                double ry = oy + (d.PosY - minY) * scale;
                if (dw < 48) dw = 48;
                if (dh < 32) dh = 32;

                Rect screen = new Rect(rx, ry, dw, dh);
                uint edgeTint = d.Main ? T.Accent : T.Accent2;

                // 外发光框线 + 立体玻璃屏面
                dc.DrawRoundedRectangle(null, Gfx.P(Ui.Wash(edgeTint, 0x36), 4.0), screen, 7, 7);
                Brush face = d.Main
                    ? (Brush)T.Grad(Ui.Wash(T.Accent, 0x26), Ui.Wash(T.Accent2, 0x10), 135)
                    : T.Grad(0xFF131D30, 0xFF0A101E, 135);
                dc.DrawRoundedRectangle(face, Gfx.P(Ui.Wash(edgeTint, d.Main ? (byte)0xB8 : (byte)0x66), 1.4), screen, 6, 6);

                // 顶部光学折射高光条
                dc.DrawLine(Gfx.P(Ui.Wash(edgeTint, 0xCC), 1.4),
                    new Point(rx + 12, ry + 1), new Point(rx + dw - 12, ry + 1));

                // 屏幕支架（底座颈部 + 机加工底板）
                double cx = rx + dw / 2.0;
                dc.DrawRectangle(T.S(T.LineMid), null, new Rect(cx - 5, ry + dh, 10, 7));
                dc.DrawRoundedRectangle(T.S(Ui.Wash(edgeTint, 0x66)), null,
                    new Rect(cx - Math.Min(36, dw * 0.24), ry + dh + 7, Math.Min(72, dw * 0.48), 3), 1.5, 1.5);

                // 屏幕内部精密遥测文字
                string resStr = (d.Width > 0 && d.Height > 0) ? (d.Width + " \u00D7 " + d.Height) : "\u2014";
                string specStr = (d.RefreshHz > 0 ? (d.RefreshHz + " Hz") : "\u2014")
                    + (d.BitsPerPel > 0 ? (" \u00B7 " + d.BitsPerPel + " bit") : "");
                FormattedText ftRes = Gfx.Text(resStr, dw >= 140 ? 18 : 13, T.Hi, 1, dpi);
                FormattedText ftSpec = Gfx.Text(specStr, T.FsNano, d.RefreshHz >= 120 ? T.Ok : T.Dim, 0, dpi);

                double blockH = ftRes.Height + 3 + ftSpec.Height;
                double ty = ry + (dh - blockH) / 2.0 + (d.Main && dh >= 84 ? 4 : 0);
                dc.DrawText(ftRes, new Point(cx - ftRes.Width / 2.0, ty));
                dc.DrawText(ftSpec, new Point(cx - ftSpec.Width / 2.0, ty + ftRes.Height + 3));

                if (d.Main && dh >= 76)
                {
                    FormattedText ftMain = Gfx.Text("PRIMARY \u00B7 " + (i + 1), 9, T.Accent, 0, dpi);
                    Rect pill = new Rect(rx + 8, ry + 7, ftMain.Width + 12, ftMain.Height + 4);
                    dc.DrawRoundedRectangle(T.S(Ui.Wash(T.Accent, 0x22)), Gfx.P(Ui.Wash(T.Accent, 0x70), 1), pill, 4, 4);
                    dc.DrawText(ftMain, new Point(pill.X + 6, pill.Y + 2));
                }
            }

            // 右下角比例尺坐标注记
            FormattedText foot = Gfx.Text("VIRTUAL DESKTOP \u00B7 " + ((int)spanW) + "\u00D7" + ((int)spanH) + " PX", 9.5, T.Mute, 0, dpi);
            dc.DrawText(foot, new Point(w - foot.Width - 10, h - foot.Height - 5));
        }
    }
}
