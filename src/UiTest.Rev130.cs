// ============================================================================
//  UiTest.Rev130.cs — Rev 130 VANGUARD CONSOLE 前端架构大修与缺陷修复回归自检
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Go
{
    public static partial class UiTest
    {
        private static void RunRev130UiChecks(Core core, Shell shell)
        {
            // 1) 左下角主题色球彻底修复：4 枚全部为 20x20 Grid + 3 枚居中同心矢量 Ellipse（零 Border 偏心）
            FieldInfo fiRings = typeof(Shell).GetField("_themeRings", BindingFlags.NonPublic | BindingFlags.Instance);
            FrameworkElement[] rings = fiRings == null ? null : fiRings.GetValue(shell) as FrameworkElement[];
            bool ringsOk = rings != null && rings.Length == T.Themes.Length;
            if (ringsOk)
            {
                for (int i = 0; i < rings.Length; i++)
                {
                    Grid g = rings[i] as Grid;
                    if (g == null || Math.Abs(g.Width - 20) > 0.1 || Math.Abs(g.Height - 20) > 0.1
                        || g.SnapsToDevicePixels || g.UseLayoutRounding || g.Children.Count != 3)
                    {
                        ringsOk = false;
                        break;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        Ellipse el = g.Children[k] as Ellipse;
                        if (el == null || el.HorizontalAlignment != HorizontalAlignment.Center
                            || el.VerticalAlignment != VerticalAlignment.Center)
                        {
                            ringsOk = false;
                            break;
                        }
                    }
                    if (!ringsOk) break;
                }
            }
            Check(ringsOk,
                  "Rev130 主题色球：4 枚均采用 20x20 同心三层矢量圆环，杜绝高 DPI 偏心错位",
                  "count=" + (rings == null ? -1 : rings.Length));

            // 2) 实时切换主题：不向导航历史追加重复记录、时钟与漂移读数立即可见不卡死 "--:--"
            FieldInfo fiHist = typeof(Shell).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fiClock = typeof(Shell).GetField("_clock", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fiDriftNum = typeof(Shell).GetField("_driftNum", BindingFlags.NonPublic | BindingFlags.Instance);

            string origTheme = T.ThemeId;
            shell.Go("overview");
            Pump(80);
            List<string> histBefore = fiHist == null ? null : fiHist.GetValue(shell) as List<string>;
            int hCountBefore = histBefore == null ? -1 : histBefore.Count;
            bool themeSwitchOk = false;
            string clockAfter = "", driftAfter = "";
            int hCountAfter = -1;
            try
            {
                string alt = origTheme == "amber" ? "cyan" : "amber";
                shell.ApplyThemeLive(alt);
                Pump(100);
                List<string> histAfter = fiHist == null ? null : fiHist.GetValue(shell) as List<string>;
                hCountAfter = histAfter == null ? -1 : histAfter.Count;
                TextBlock clk = fiClock == null ? null : fiClock.GetValue(shell) as TextBlock;
                TextBlock drf = fiDriftNum == null ? null : fiDriftNum.GetValue(shell) as TextBlock;
                clockAfter = clk == null ? "" : (clk.Text ?? "");
                driftAfter = drf == null ? "" : (drf.Text ?? "");
                themeSwitchOk = hCountAfter == hCountBefore
                    && !string.IsNullOrEmpty(clockAfter) && clockAfter != "--:--"
                    && !string.IsNullOrEmpty(driftAfter) && driftAfter != "\u2014";
            }
            finally
            {
                if (T.ThemeId != origTheme)
                {
                    shell.ApplyThemeLive(origTheme);
                    Pump(80);
                }
            }
            Check(themeSwitchOk,
                  "Rev130 实时换肤：不产生重复历史记录，重建后时钟与漂移计立即就绪",
                  "hist=" + hCountBefore + "->" + hCountAfter + " clk=" + clockAfter + " drift=" + driftAfter);

            // 3) 01 总览四维健康徽标与瓶颈诊断换行 + 02 调优右侧战术检视台联动
            shell.Go("overview");
            Pump(120);
            Go.Pages.Overview ov = core.Get("overview") as Go.Pages.Overview;
            FieldInfo fiGrade = typeof(Go.Pages.Overview).GetField("_gradeText", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fiBottle = typeof(Go.Pages.Overview).GetField("_bottleneckText", BindingFlags.NonPublic | BindingFlags.Instance);
            TextBlock ovGrade = (ov != null && fiGrade != null) ? fiGrade.GetValue(ov) as TextBlock : null;
            TextBlock ovBottle = (ov != null && fiBottle != null) ? fiBottle.GetValue(ov) as TextBlock : null;
            bool ovOk = ovGrade != null && !string.IsNullOrEmpty(ovGrade.Text)
                && ovBottle != null && ovBottle.TextWrapping == TextWrapping.Wrap;
            Check(ovOk,
                  "Rev130 总览驾驶舱：四维综合评级徽标与自动换行瓶颈诊断装配正常",
                  "grade=" + (ovGrade == null ? "null" : ovGrade.Text));

            shell.Go("tuning");
            Pump(100);
            Go.Pages.Tuning tp = core.Get("tuning") as Go.Pages.Tuning;
            FieldInfo fiInspName = typeof(Go.Pages.Tuning).GetField("_inspName", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo miInspect = typeof(Go.Pages.Tuning).GetMethod("InspectAction", BindingFlags.NonPublic | BindingFlags.Instance);
            bool inspOk = false;
            string inspShown = "";
            if (tp != null && fiInspName != null && miInspect != null && core.Engine.Actions.Count > 1)
            {
                ActionDef target = core.Engine.Actions[1];
                miInspect.Invoke(tp, new object[] { target });
                TextBlock tn = fiInspName.GetValue(tp) as TextBlock;
                inspShown = tn == null ? "" : (tn.Text ?? "");
                inspOk = inspShown == target.Name;
            }
            Check(inspOk,
                  "Rev130 调优工作台：右侧战术检视台支持按选中动作实时刷新 Diff 与机理",
                  "inspected=" + inspShown);

            // 3.5) 02 调优右侧战术检视台随滚吸顶（滑到中下部时自动跟随视口，回顶归零）
            bool stickyOk = false;
            double stickyAtTop = -1, stickyAtScroll = -1;
            if (tp != null)
            {
                FrameworkElement rootEl = shell.Content as FrameworkElement;
                FieldInfo fiTx = typeof(Go.Pages.Tuning).GetField("_inspStickyTx", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fiWraps = typeof(Shell).GetField("_wraps", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo miSticky = typeof(Go.Pages.Tuning).GetMethod("UpdateInspectorSticky", BindingFlags.NonPublic | BindingFlags.Instance);
                Dictionary<string, ThinScroll> wraps = fiWraps == null ? null : fiWraps.GetValue(shell) as Dictionary<string, ThinScroll>;
                ThinScroll tw = (wraps != null && wraps.ContainsKey("tuning")) ? wraps["tuning"] : null;
                TranslateTransform tx = fiTx == null ? null : fiTx.GetValue(tp) as TranslateTransform;
                if (rootEl != null && tw != null && tx != null && miSticky != null)
                {
                    tw.View.ScrollToVerticalOffset(0);
                    Layout(rootEl, 1440, 900);
                    miSticky.Invoke(tp, null);
                    stickyAtTop = tx.Y;

                    tw.View.ScrollToVerticalOffset(760);
                    Layout(rootEl, 1440, 900);
                    miSticky.Invoke(tp, null);
                    stickyAtScroll = tx.Y;

                    tw.View.ScrollToVerticalOffset(0);
                    Layout(rootEl, 1440, 900);
                    miSticky.Invoke(tp, null);
                    stickyOk = Math.Abs(stickyAtTop) < 1.0 && stickyAtScroll > 350.0 && Math.Abs(tx.Y) < 1.0;
                }
            }
            Check(stickyOk,
                  "Rev130 调优工作台：右侧战术检视台随外层滚动吸顶跟随并在回顶后归零",
                  "top=" + stickyAtTop.ToString("0.0") + " scrolled=" + stickyAtScroll.ToString("0.0"));

            // 4) 05 图形栈虚拟多屏画布渲染 + 06 网络四节点拓扑 + 08 档案 2 列卡牌墙 + 09 基准评级徽章
            shell.Go("graphics");
            Pump(120);
            Go.Pages.Graphics gp = core.Get("graphics") as Go.Pages.Graphics;
            FieldInfo fiCanvas = typeof(Go.Pages.Graphics).GetField("_displayCanvas", BindingFlags.NonPublic | BindingFlags.Instance);
            Go.Pages.DisplayStageCanvas canvas = (gp != null && fiCanvas != null) ? fiCanvas.GetValue(gp) as Go.Pages.DisplayStageCanvas : null;
            bool canvasOk = false;
            if (canvas != null)
            {
                RenderTargetBitmap rtb = new RenderTargetBitmap(420, 160, 96, 96, PixelFormats.Pbgra32);
                canvas.Width = 420; canvas.Height = 160;
                canvas.Measure(new Size(420, 160));
                canvas.Arrange(new Rect(0, 0, 420, 160));
                canvas.UpdateLayout();
                rtb.Render(canvas);
                canvasOk = true;
            }

            shell.Go("network");
            Pump(100);
            Go.Pages.Network np = core.Get("network") as Go.Pages.Network;
            FieldInfo fiRtGrade = typeof(Go.Pages.Network).GetField("_rtGradeText", BindingFlags.NonPublic | BindingFlags.Instance);
            bool netTopoOk = np != null && fiRtGrade != null && (fiRtGrade.GetValue(np) as TextBlock) != null;

            shell.Go("profiles");
            Pump(100);
            Go.Pages.Profiles pp = core.Get("profiles") as Go.Pages.Profiles;
            if (pp != null) pp.OnEnter();
            FieldInfo fiProfList = typeof(Go.Pages.Profiles).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance);
            Reflow profReflow = (pp != null && fiProfList != null) ? fiProfList.GetValue(pp) as Reflow : null;
            bool profWallOk = profReflow != null && profReflow.Count == core.Profiles.Count;

            shell.Go("bench");
            Pump(80);
            Go.Pages.Benchmark bp = core.Get("bench") as Go.Pages.Benchmark;
            FieldInfo fiHeroGrade = typeof(Go.Pages.Benchmark).GetField("_heroGradeLetter", BindingFlags.NonPublic | BindingFlags.Instance);
            bool benchOk = bp != null && fiHeroGrade != null && (fiHeroGrade.GetValue(bp) as TextBlock) != null;

            Check(canvasOk && netTopoOk && profWallOk && benchOk,
                  "Rev130 页面大修：图形虚拟多屏画布 / 网络四节点拓扑 / 档案卡牌墙 / 基准评级徽章全线就绪",
                  "canvas=" + canvasOk + " net=" + netTopoOk + " prof=" + profWallOk + " bench=" + benchOk);

            // 5) Rev141 桌面安装引导带与命令面板入口（纯状态注入，绝不触碰真实桌面/注册表）
            AppInstall.InstallStatus fakePortable = new AppInstall.InstallStatus();
            fakePortable.CurrentExe = @"C:\Downloads\GameOptimizer.exe";
            fakePortable.InstallDir = @"C:\Users\Test\AppData\Local\Programs\GameOptimizer";
            fakePortable.InstalledExe = @"C:\Users\Test\AppData\Local\Programs\GameOptimizer\GameOptimizer.exe";
            fakePortable.RunningFromInstallDir = false;
            fakePortable.RunningFromSourceTree = false;
            fakePortable.InstalledBinaryExists = false;
            fakePortable.HasDesktopShortcut = false;

            shell.TestSetInstallStatus(fakePortable, true);
            Pump(40);
            bool showOnPortable = shell.InstallBannerVisible;

            AppInstall.InstallStatus fakeSource = new AppInstall.InstallStatus();
            fakeSource.RunningFromSourceTree = true;
            shell.TestSetInstallStatus(fakeSource, true);
            Pump(40);
            bool hideOnSource = !shell.InstallBannerVisible;

            shell.TestSetInstallStatus(fakePortable, true);
            shell.DismissInstallPrompt(true);
            Pump(40);
            bool hideOnDismiss = !shell.InstallBannerVisible;

            shell.TestPalette("桌面快捷方式");
            Pump(60);
            bool palHit = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("快捷方式", StringComparison.Ordinal) >= 0;
            shell.HidePalette();

            shell.TestSetInstallStatus(AppInstall.Probe(), true);
            Check(showOnPortable && hideOnSource && hideOnDismiss && palHit,
                  "Rev141 桌面软件引导：下载便携态显示、源码仓静默、保持便携收敛、Ctrl+K 命令命中",
                  "portable=" + showOnPortable + " srcQuiet=" + hideOnSource + " dismiss=" + hideOnDismiss + " pal=" + palHit);
        }
    }
}
