// ============================================================================
//  UiTest.Rev110.cs — Rev 110 AETHER 前端交互与可视化回归自检
// ============================================================================
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Go
{
    public static partial class UiTest
    {
        private static void RunRev110UiChecks(Core core, Shell shell)
        {
            // 1) Tuning 实时关键字搜索过滤
            shell.Go("tuning");
            Pump(100);
            Go.Pages.Tuning tp = core.Get("tuning") as Go.Pages.Tuning;
            bool searchOk = false;
            int totalRows = 0, vrrRows = 0, restoredRows = 0;
            if (tp != null)
            {
                totalRows = tp.TestVisibleRowCount();
                tp.TestSetSearch("VRR");
                Pump(40);
                vrrRows = tp.TestVisibleRowCount();
                tp.TestSetSearch("");
                Pump(40);
                restoredRows = tp.TestVisibleRowCount();
                searchOk = totalRows >= 30 && vrrRows >= 1 && vrrRows < totalRows && restoredRows == totalRows;
            }
            Check(searchOk,
                  "Rev110 Tuning：实时关键字检索精准过滤并可清空还原",
                  "all=" + totalRows + " vrr=" + vrrRows + " back=" + restoredRows);

            // 2) Waveform 3.0 单调三次平滑贝塞尔 + 实测均值虚线离屏渲染无异常
            bool waveOk = false;
            try
            {
                RenderTargetBitmap bmp = new RenderTargetBitmap(360, 120, 96, 96, PixelFormats.Pbgra32);
                Waveform wf = new Waveform(32);
                wf.SmoothCurve = true;
                wf.ShowMeanLine = true;
                wf.ShowThreshold = true;
                wf.Threshold = 1000;
                double[] pts = new double[] { 200, 1500, 300, 2600, 180, 900, 420, 1200, 310, 880 };
                for (int i = 0; i < pts.Length; i++) wf.Push(pts[i]);
                wf.Width = 360; wf.Height = 120;
                wf.Measure(new Size(360, 120));
                wf.Arrange(new Rect(0, 0, 360, 120));
                wf.UpdateLayout();
                bmp.Render(wf);
                waveOk = wf.CurrentCeil > 2000;
            }
            catch (Exception ex) { Diag.Fail("uitest.rev110.wave", ex); }
            Check(waveOk, "Rev110 Waveform 3.0：单调三次平滑贝塞尔与均值线渲染通过", null);

            // 3) MemRibbon 2.0 带 Labels 渲染通过
            bool ribOk = false;
            try
            {
                RenderTargetBitmap bmp = new RenderTargetBitmap(360, 24, 96, 96, PixelFormats.Pbgra32);
                MemRibbon rib = new MemRibbon();
                rib.Parts = new double[] { 12000, 6000, 200, 1800 };
                rib.Colors = new uint[] { T.Accent, T.SeriesA, T.SeriesB, T.LineMid };
                rib.Labels = new string[] { "已用", "待机", "已修改", "空闲" };
                rib.Width = 360; rib.Height = 24;
                rib.Measure(new Size(360, 24));
                rib.Arrange(new Rect(0, 0, 360, 24));
                rib.UpdateLayout();
                bmp.Render(rib);
                ribOk = true;
            }
            catch (Exception ex) { Diag.Fail("uitest.rev110.ribbon", ex); }
            Check(ribOk, "Rev110 MemRibbon 2.0：分段标签与高光顶棱渲染通过", null);

            // 4) 命令面板命中新增原生快捷命令（刷新 DNS / 清理待机列表）
            shell.TestPalette("刷新系统 DNS");
            Pump(40);
            bool pDns = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("DNS", StringComparison.OrdinalIgnoreCase) >= 0;
            shell.TestPalette("待机内存");
            Pump(40);
            bool pMem = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("待机", StringComparison.Ordinal) >= 0;
            shell.HidePalette();
            Pump(40);
            Check(pDns && pMem,
                  "Rev110 命令面板：新增原生刷新 DNS 与待机内存清理快捷命令命中",
                  "dns=" + pDns + " mem=" + pMem);
        }
    }
}
