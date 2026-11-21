// ============================================================================
//  UiTest.cs — 无头交互自检：命令面板 / 导航历史 / 自适应断点 / 行点击路径
//  --uitest <outFile>
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Rect = System.Windows.Rect;
using System.Windows.Threading;

namespace Go
{
    public static partial class UiTest
    {
        private static StringBuilder _log = new StringBuilder();
        private static int _pass, _fail;

        private static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++; else _fail++;
            _log.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail == null ? "" : "  ::  " + detail));
        }

        /// <summary>自检会写 ui.json（验证持久化要用真实路径），跑完必须原样还回去。</summary>
        private static string _statePath, _stateBackup;
        private static bool _stateHad;

        private static void BackupUserState()
        {
            try
            {
                _statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                          "GameOptimizer", "ui.json");
                _stateBackup = _statePath + ".uitest-backup";
                _stateHad = File.Exists(_statePath);
                if (_stateHad) File.Copy(_statePath, _stateBackup, true);
            }
            catch (Exception ex) { Diag.Fail("uitest.state.backup", ex); }
        }

        private static void RestoreUserState()
        {
            try
            {
                if (string.IsNullOrEmpty(_statePath)) return;
                if (_stateHad)
                {
                    if (File.Exists(_stateBackup)) File.Copy(_stateBackup, _statePath, true);
                }
                else if (File.Exists(_statePath)) File.Delete(_statePath);
                if (File.Exists(_stateBackup)) File.Delete(_stateBackup);
            }
            catch (Exception ex) { Diag.Fail("uitest.state.restore", ex); }
        }

        /// <summary>滑块在 ThinScroll 坐标系里的真实 Y（不是 Margin.Top：位置由布局决定）。</summary>
        private static double ThumbY(ThinScroll ts, Border thumb)
        {
            try { return thumb.TransformToAncestor(ts).Transform(new Point(0, 0)).Y; }
            catch (Exception ex) { Diag.Fail("uitest.thumby", ex); return -1; }
        }

        public static int Run(string outFile)
        {
            _pass = 0; _fail = 0;
            _log.Clear();
            _log.AppendLine("GameOptimizer Rev60 UI self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Application app = null;
            try
            {
                BackupUserState();
                Ui.Animate = false;

                Shell.SkipStateRestore = false; // 自检需要真实验证持久化
                app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                Core core = new Core();
                core.DetectHardware();
                Registry.Load(core);
                Shell shell = new Shell(core);

                double w = shell.Width, h = shell.Height;
                FrameworkElement root = shell.Content as FrameworkElement;
                root.Width = w; root.Height = h;
                Layout(root, w, h);

            // ---- 1. 导航 ----
            shell.Go("overview");
            Pump(120);
            Check(shell.CurrentKey == "overview", "导航：进入总览", shell.CurrentKey);

            shell.Go("tuning");
            Pump(120);
            Check(shell.CurrentKey == "tuning", "导航：切到调优矩阵", shell.CurrentKey);

            shell.Go("memory");
            Pump(120);
            int histBefore = shell.HistoryCount;
            Check(histBefore >= 3, "导航：历史栈累积", histBefore + " 条");

            // 回退
            shell.TestBack();
            Pump(120);
            Check(shell.CurrentKey == "tuning", "导航：Alt+← 回到上一页", shell.CurrentKey);
            shell.TestForward();
            Pump(120);
            Check(shell.CurrentKey == "memory", "导航：Alt+→ 前进回去", shell.CurrentKey);

            // ---- 1.5 只读横幅与提权状态一致 ----
            Check(shell.BannerVisible == !core.Elevated,
                  "只读横幅：可见性与提权状态一致",
                  "banner=" + shell.BannerVisible + " elevated=" + core.Elevated);

            // ---- 2. 命令面板 ----
            shell.TestPalette("");
            Pump(60);
            Check(shell.PaletteResultCount > 0, "命令面板：空查询有默认结果", shell.PaletteResultCount + " 条");

            shell.TestPalette("调度");
            Pump(60);
            string first = shell.PaletteFirstTitle;
            Check(shell.PaletteResultCount > 0 && first.IndexOf("调度", StringComparison.Ordinal) >= 0,
                  "命令面板：中文检索命中", first);

            shell.TestPalette("zzzznotexist");
            Pump(60);
            Check(shell.PaletteResultCount == 0, "命令面板：无结果时为空", null);

            shell.TestPalette("调矩");
            Pump(60);
            Check(shell.PaletteResultCount > 0, "命令面板：模糊子序列命中", shell.PaletteResultCount + " 条");

            shell.TestPalette("调优矩阵");
            Pump(60);
            string key = shell.CurrentKey;
            shell.TestPaletteActivate();
            Pump(120);
            Check(shell.CurrentKey == "tuning" && key != "tuning", "命令面板：回车执行跳转", shell.CurrentKey);

            // ---- 3. 自适应 ----
            shell.Width = 1600; shell.Height = 950;
            shell.Relayout();
            Pump(60);
            bool wideCompact = shell.RailCompact;
            shell.Width = 1000; shell.Height = 680;
            shell.Relayout();
            Pump(60);
            bool narrowCompact = shell.RailCompact;
            Check(!wideCompact, "自适应：1600 宽为完整侧栏", null);
            Check(narrowCompact, "自适应：1000 宽折叠为图标栏", null);

            // ---- 4. 流式重排 ----
            Reflow rf = new Reflow(10, 1, 2, 3, 4);
            for (int i = 0; i < 4; i++) rf.Add(new Border());
            rf.Width = 400; rf.Measure(new Size(400, 200)); rf.Arrange(new Rect(0, 0, 400, 200));
            rf.Apply();
            int narrowCols = rf.ColumnDefinitions.Count;
            rf.Width = 1400; rf.Measure(new Size(1400, 200)); rf.Arrange(new Rect(0, 0, 1400, 200));
            rf.Apply();
            int wideCols = rf.ColumnDefinitions.Count;
            Check(narrowCols == 1, "重排：400 宽为 1 列", narrowCols + " 列");
            Check(wideCols == 4, "重排：1400 宽为 4 列", wideCols + " 列");

            // ---- 4.5 交互语义：按钮的"触发时刻"与"哪个键"必须像原生应用 ----
            Go.Tray.TrayMenu menu = new Go.Tray.TrayMenu();
            int actA = 0, actB = 0, checks = 0;
            menu.AddItem("A", "第一个", delegate { actA++; }, false);
            menu.AddItem("B", null, delegate { actB++; }, false);
            menu.AddCheck("C", true, delegate (bool on) { checks++; });
            Check(menu.RowCount == 3, "交互：菜单行数正确", menu.RowCount + " 行");

            UIElement rowA = menu.TestRow(0);
            Check(rowA != null, "交互：可取到行元素", null);

            // 右键绝不触发动作
            MouseButtonEventArgs evRight = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right);
            evRight.RoutedEvent = UIElement.MouseRightButtonUpEvent;
            rowA.RaiseEvent(evRight);
            Check(actA == 0, "交互：右键点击不触发菜单项", "act=" + actA);

            // 只按下、没抬起 → 绝不触发（Windows 的标准是"抬起才算一次点击"）
            MouseButtonEventArgs evDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left);
            evDown.RoutedEvent = UIElement.MouseLeftButtonDownEvent;
            rowA.RaiseEvent(evDown);
            Check(actA == 0, "交互：仅按下不触发，抬起才触发", "act=" + actA);

            // 键盘：↓ ↓ Enter
            Check(menu.SelectedIndex == -1, "交互：弹出时无选中项", null);
            menu.TestKey(Key.Down);
            bool sel0 = menu.TestIsSelected(0);
            menu.TestKey(Key.Down);
            bool sel1 = menu.TestIsSelected(1);
            Check(sel0 && sel1, "交互：↓ 在菜单项之间移动选中", null);
            menu.TestKey(Key.Enter);
            Check(actB == 1 && actA == 0, "交互：Enter 执行选中项", "A=" + actA + " B=" + actB);

            Go.Tray.TrayMenu menu2 = new Go.Tray.TrayMenu();
            int toggled = 0; bool lastState = false;
            menu2.AddCheck("C", true, delegate (bool on) { toggled++; lastState = on; });
            menu2.TestActivate(0);
            Check(toggled == 1 && lastState == false, "交互：开关行取反并回调新状态", "on=" + lastState);

            // ---- 4.6 标题栏交给系统：CaptionHeight 必须是标题栏高度 ----
            WindowChrome chrome = WindowChrome.GetWindowChrome(shell);
            Check(chrome != null && Math.Abs(chrome.CaptionHeight - T.TitleH) < 0.01,
                  "窗口：标题栏使用系统 CaptionHeight（拖动/双击/吸附/系统菜单归 Windows）",
                  chrome == null ? "null" : chrome.CaptionHeight.ToString("0"));

            // 面板打开时必须关掉标题栏拖动区，否则点面板顶部会拖窗口
            shell.TestPalette("");
            Pump(60);
            WindowChrome chrome2 = WindowChrome.GetWindowChrome(shell);
            bool captionOff = chrome2 != null && chrome2.CaptionHeight < 0.01;
            shell.HidePalette();
            Pump(60);
            WindowChrome chrome3 = WindowChrome.GetWindowChrome(shell);
            bool captionOn = chrome3 != null && Math.Abs(chrome3.CaptionHeight - T.TitleH) < 0.01;
            Check(captionOff && captionOn, "窗口：命令面板开合时切换标题栏拖动区", null);

            // ---- 4.7 电源计划：真的读、真的切、切完能还原 ----
            shell.Go("overview");
            Pump(160);
            Go.Pages.Overview ov = core.Get("overview") as Go.Pages.Overview;
            Check(ov != null, "电源：总览页实例可取到", null);
            if (ov != null)
            {
                int n = ov.PowerSchemeCount;
                Check(n >= 1, "电源：下拉列表里至少有一个方案", n + " 个");

                Guid before = Power.Active();
                int pick = -1;
                for (int i = 0; i < n; i++)
                {
                    Guid g = ov.PowerSchemeAt(i);
                    if (g != Guid.Empty && g != before) { pick = i; break; }
                }
                if (pick >= 0)
                {
                    Guid target = ov.PowerSchemeAt(pick);
                    // 还原必须放在 finally：这里换的是**用户机器真实的电源方案**，
                    // 中途任何一条断言/调用抛异常，后面那行 Power.SetActive(before) 就再也走不到，
                    // 机器会一直停在测试方案上。
                    bool ok = false;
                    try
                    {
                        ok = ov.TestApplyPowerAt(pick);
                        Pump(80);
                        Check(ok && Power.Active() == target, "电源：切换后系统活动方案真的变了", Power.Describe(target));
                        // FriendlyName 为空时 IndexOf("") 恒为 0，这条断言会永远成立。
                        string friendly = Power.FriendlyName(target) ?? "";
                        Check(friendly.Length > 0 && ov.PowerLabel != null && ov.PowerLabel.IndexOf(friendly) >= 0,
                              "电源：右上角标签跟随变化", ov.PowerLabel + " ⊃ " + friendly);
                    }
                    finally
                    {
                        Power.SetActive(before);
                        Power.SyncInto(core);
                        Pump(60);
                    }
                    Check(Power.Active() == before, "电源：自检后已还原原方案", Power.Describe(before));
                }
                else
                {
                    Check(false, "电源：本机至少需要两个方案才能验证切换", n + " 个");
                }
            }

            // ---- 4.8 每页首次构建耗时：切页卡不卡，这里说了算 ----
            System.Text.StringBuilder timings = new System.Text.StringBuilder();
            double worstMs = 0;
            string worstKey = "";
            foreach (Page pg in core.Pages)
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                shell.Go(pg.Key);
                root.Width = 1300; root.Height = 800;
                root.Measure(new Size(1300, 800));
                root.Arrange(new Rect(0, 0, 1300, 800));
                root.UpdateLayout();
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > worstMs) { worstMs = ms; worstKey = pg.Key; }
                timings.Append(pg.Key).Append("=").Append(ms.ToString("0"))
                       .Append("(树").Append(shell.LastHostMs.ToString("0"))
                       .Append("+数").Append(shell.LastEnterMs.ToString("0")).Append(")  ");
            }
            _log.AppendLine("        页面首建：" + timings.ToString());
            Check(worstMs < 600, "性能：任一页面首次构建 < 600ms", worstKey + " " + worstMs.ToString("0") + "ms");

            // ---- 4.9 键盘滚动与悬浮提示缓存 ----
            Border tall = new Border();
            tall.Height = 2000;
            ThinScroll ts = new ThinScroll(tall);
            ts.Width = 400; ts.Height = 300;
            ts.Measure(new Size(400, 300));
            ts.Arrange(new Rect(0, 0, 400, 300));
            ts.UpdateLayout();
            Pump(60);
            double off0 = ts.View.VerticalOffset;
            bool consumedDown = ts.ScrollByKey(Key.Down);
            ts.UpdateLayout();
            Check(consumedDown && ts.View.VerticalOffset > off0, "滚动：键盘↓驱动 ThinScroll 滚动",
                  off0.ToString("0") + " -> " + ts.View.VerticalOffset.ToString("0"));
            ts.ScrollByKey(Key.End);
            ts.UpdateLayout();
            double maxOff = Math.Max(0, ts.View.ExtentHeight - ts.View.ViewportHeight);
            Check(Math.Abs(ts.View.VerticalOffset - maxOff) < 1.0, "滚动：End 键到底",
                  ts.View.VerticalOffset.ToString("0") + "/" + maxOff.ToString("0"));
            ts.ScrollByKey(Key.Home);
            ts.UpdateLayout();
            Check(ts.View.VerticalOffset < 1.0, "滚动：Home 键回顶", ts.View.VerticalOffset.ToString("0"));

            TextBlock tipHost = new TextBlock();
            tipHost.Text = "host";
            Tip.Attach(tipHost, "一段用于验证测量缓存的提示文字");
            MouseEventArgs me = new MouseEventArgs(Mouse.PrimaryDevice, 0);
            me.RoutedEvent = UIElement.MouseEnterEvent;
            tipHost.RaiseEvent(me);   // 首次悬浮触发惰性构造（ToolTip 视觉树不再在 Attach 时建）
            Check(tipHost.ToolTip != null, "提示：首次悬浮惰性构造生效", null);
            bool rehoverOk = true;
            string rehoverWhy = null;
            try { tipHost.RaiseEvent(me); }
            catch (Exception ex) { rehoverOk = false; rehoverWhy = ex.GetType().Name + ": " + ex.Message; }
            // 原来这里是无条件 Check(true)：第二次悬浮抛异常也照样 PASS。
            Check(rehoverOk, "提示：重复悬浮走测量缓存不崩溃", rehoverWhy);

            // ---- 4.9 细滚动条：滑块"真实位置"必须与内容偏移线性对应 ----
            // 这条断言是为一个已修复的真 bug 立的：滑块只设 Margin 而让它 Stretch 时，
            // WPF 把剩余空间当"居中"分配，于是顶部时滑块出现在轨道 1/3 处、往下滚又追不上内容，
            // 到底才刚好对齐。以前只断言过"滑块存在"，位置错了没人发现。
            {
                StackPanel tallCol = new StackPanel();
                for (int i = 0; i < 12; i++)
                {
                    Border bd = new Border();
                    bd.Height = 120;
                    tallCol.Children.Add(bd);
                }
                ThinScroll tsGeo = new ThinScroll(tallCol);
                Grid holderGeo = new Grid();
                holderGeo.Width = 900; holderGeo.Height = 600;
                holderGeo.Children.Add(tsGeo);
                Grid rpGeo = new Grid();
                rpGeo.Width = 900; rpGeo.Height = 600;
                rpGeo.Children.Add(holderGeo);
                rpGeo.Measure(new Size(900, 600));
                rpGeo.Arrange(new Rect(0, 0, 900, 600));
                rpGeo.UpdateLayout();

                System.Reflection.FieldInfo thumbFi = typeof(ThinScroll).GetField("_thumb",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Border thGeo = thumbFi == null ? null : thumbFi.GetValue(tsGeo) as Border;
                double maxGeo = tsGeo.View.ExtentHeight - tsGeo.View.ViewportHeight;
                double trackGeo = tsGeo.ActualHeight - 4;
                double thHGeo = thGeo == null ? 0 : thGeo.ActualHeight;
                bool geoOk = thGeo != null && maxGeo > 100 && trackGeo > 100 && thHGeo > 10;

                double yTop = -1, yHalf = -1, yBottom = -1;
                if (geoOk)
                {
                    tsGeo.View.ScrollToVerticalOffset(0); rpGeo.UpdateLayout(); yTop = ThumbY(tsGeo, thGeo);
                    tsGeo.View.ScrollToVerticalOffset(maxGeo * 0.5); rpGeo.UpdateLayout(); yHalf = ThumbY(tsGeo, thGeo);
                    tsGeo.View.ScrollToVerticalOffset(maxGeo); rpGeo.UpdateLayout(); yBottom = ThumbY(tsGeo, thGeo);
                }
                Check(geoOk && Math.Abs(yTop - 2) <= 1.2,
                      "滚动条：顶部时滑块贴顶（不是居中）", yTop.ToString("0.0"));
                Check(geoOk && Math.Abs(yHalf - (2 + (trackGeo - thHGeo) * 0.5)) <= 2.0,
                      "滚动条：中点位置与内容偏移成正比", yHalf.ToString("0.0") + " want " + (2 + (trackGeo - thHGeo) * 0.5).ToString("0.0"));
                Check(geoOk && Math.Abs(yBottom - (2 + trackGeo - thHGeo)) <= 1.5,
                      "滚动条：到底时滑块贴底", yBottom.ToString("0.0"));
            }

            // ---- 4.95 Rev86 读数语言：五个原子不许悄悄漂移 ----
            // 这一节验的不是像素，是"契约还在"：
            // 单位沉底、变化量能自我隐藏、行外壳让出色条位、两档徽标确实不同权重。
            // Ui.Instrument 是 11 个页面共用的读法；它一旦变形，所有页面会一起变形，
            // 而且没人会立刻发现——所以用断言把它钉住。
            TextBlock mval = Ui.Num("42", T.FsNumXL, T.Hi);
            TextBlock munit = Ui.Mono("ms", T.FsNano, T.Dim);
            Grid readout = Ui.Readout(mval, munit);
            // 必须按"内容尺寸"排：实际用法里它住在 StackPanel 中，高度由内容决定。
            // 上一版强行 Arrange 到 80px，Grid 被 VerticalAlignment=Stretch 拉满，
            // 数值居中、单位沉底——量出来差 11px，但那是测试布景错了，不是控件错了。
            readout.Measure(new Size(200, double.PositiveInfinity));
            readout.Arrange(new Rect(0, 0, 200, readout.DesiredSize.Height));
            readout.UpdateLayout();
            double vb = mval.TransformToAncestor(readout).Transform(new Point(0, mval.ActualHeight)).Y;
            double ub = munit.TransformToAncestor(readout).Transform(new Point(0, munit.ActualHeight)).Y;
            Check(Math.Abs(vb - ub) <= 8.0, "读数：单位沉在数值基线上", "值底=" + vb.ToString("0.0") + " 单位底=" + ub.ToString("0.0"));
            Check(munit.FontSize < mval.FontSize, "读数：单位退一档字号", munit.FontSize + " < " + mval.FontSize);

            DeltaChip chip = new DeltaChip();
            chip.Set(0, "", false);
            Check(chip.Visibility == Visibility.Hidden, "变化量：没有基准时不显示", chip.Visibility.ToString());
            chip.Set(1, "0.4", false);
            TextBlock chipText = chip.Child as TextBlock;
            SolidColorBrush chipBrush = chipText == null ? null : chipText.Foreground as SolidColorBrush;
            Check(chip.Visibility == Visibility.Visible && chipBrush != null && chipBrush.Color == T.C(T.Bad),
                  "变化量：方向和好坏分开判色（CPU 上升=坏）",
                  chipBrush == null ? "无前景" : chipBrush.Color.ToString());

            Border rowBody = new Border();
            rowBody.Width = 40; rowBody.Height = 20;
            Border shellRow = Ui.RowShell(rowBody, T.Warn);
            Grid shellGrid = shellRow.Child as Grid;
            System.Windows.Shapes.Rectangle riskBar = shellGrid == null ? null : shellGrid.Children[0] as System.Windows.Shapes.Rectangle;
            // 色条的宽度来自列定义（元素本身是 Stretch），所以量列而不是量 Width。
            double barCol = shellGrid == null || shellGrid.ColumnDefinitions.Count == 0
                          ? -1 : shellGrid.ColumnDefinitions[0].Width.Value;
            Check(riskBar != null && Math.Abs(barCol - 2.0) < 0.01,
                  "行外壳：左缘风险条 2px", "列宽=" + barCol.ToString("0.0"));
            Check(Math.Abs(rowBody.Margin.Left - 14.0) < 0.01,
                  "行外壳：内容让出色条位", rowBody.Margin.Left.ToString("0.0"));

            Border ghostTag = Ui.Tag("A", T.Dim);
            Border solidTag = Ui.TagSolid("需管理员", T.Warn);
            Check(ghostTag.Background == null && ghostTag.BorderThickness.Left >= 1.0,
                  "徽标：描边档没有实底", ghostTag.BorderThickness.Left.ToString("0.0"));
            Check(solidTag.Background != null && solidTag.BorderThickness.Left == 0.0,
                  "徽标：实底档没有描边", solidTag.BorderThickness.Left.ToString("0.0"));

            // 注意：一个 TextBlock 只能有一个父元素。上一版这里复用了 mval，
            // 于是 Ui.Stat 把它从 Readout 里抢走 → InvalidOperationException → 自检进程挂住不退。
            TextBlock statVal = Ui.Num("7", T.FsNumXL, T.Hi);
            TextBlock statNote = Ui.Mono("注脚", T.FsNano, T.Mute);
            StackPanel stat = Ui.Stat("CPU LOAD", statVal, "%", chip, Ui.Note(statNote));
            Check(stat.Children.Count == 5, "统计格：标签/刻度/读数/中段/注脚五层", stat.Children.Count + " 层");
            Check(statNote.TextTrimming == TextTrimming.CharacterEllipsis,
                  "统计格：注脚单行截断", statNote.TextTrimming.ToString());

            // ---- 4.10 审计日志：不把空态当数据行；写入真实记录后进页非空、含正文、二次进入不丢 ----
            shell.Go("overview");
            Pump(80);
            shell.Go("log");
            Pump(150);
            Go.Pages.LogPage lp = core.Get("log") as Go.Pages.LogPage;
            int emptyDataRows = CountLogDataRows(lp);
            int emptyRawLines = lp == null ? -1 : lp.LineCount;
            Check(emptyDataRows == 0 && LogLastCount(lp) == 0,
                  "日志：空态面板不误算为真实日志记录",
                  "raw=" + emptyRawLines + " dataRows=" + emptyDataRows);

            string auditMarker = "UITEST_LOG_" + Environment.TickCount;
            core.Engine.Ledger.AppendAudit("OK " + auditMarker + " :: Rev89 审计正文验证");
            shell.Go("overview");
            Pump(80);
            shell.Go("log");
            Pump(200);
            int logRows1 = CountLogDataRows(lp);
            bool hasBody1 = LogContainsBody(lp, auditMarker);
            shell.Go("overview");
            Pump(120);
            shell.Go("log");
            Pump(300);
            int logRows2 = CountLogDataRows(lp);
            bool hasBody2 = LogContainsBody(lp, auditMarker);
            Check(logRows1 > 0 && logRows2 == logRows1 && hasBody1 && hasBody2 && LogLastCount(lp) == logRows2,
                  "日志：写入真实记录后进页非空、含目标正文且二次进入不丢内容",
                  logRows1 + " -> " + logRows2 + " body=" + hasBody1 + "/" + hasBody2);

            // ---- 5. 窗口状态记忆 ----
            shell.Width = 1300; shell.Height = 800;
            shell.Go("restore");
            shell.SaveState();
            string statePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOptimizer", "ui.json");
            bool saved = File.Exists(statePath) && File.ReadAllText(statePath).Contains("restore");
            Check(saved, "持久化：窗口状态与当前页写入 ui.json", statePath);

            // ---- 5.1 主题系统：切换强调色联动派生色，运行时重建不崩溃 ----
            uint iceAccent = T.Accent;
            Check(T.ApplyTheme("ember") && T.Accent != iceAccent, "主题：ApplyTheme 改主色", T.Accent.ToString("X"));
            shell.ApplyThemeLive("violet");
            Pump(200);
            Check(T.ThemeId == "violet" && shell.CurrentKey.Length > 0, "主题：运行时重建外壳保持当前页", shell.CurrentKey);
            shell.ApplyThemeLive("ice");
            Pump(120);
            Check(T.Accent == iceAccent && T.ThemeId == "ice", "主题：切回冰蓝恢复原色", T.Accent.ToString("X"));

            // ---- 5.2 新功能：GPU 快照默认不可用（不伪造）、基准历史 CSV 格式 ----
            Check(!new GpuSampler().Current.Available, "GPU：未启动采样时快照不可用（显示 —）", null);
            List<BenchRecord> hr = new List<BenchRecord>();
            BenchRecord br = new BenchRecord();
            br.Time = "2026-11-11 12:00:00"; br.P50 = 100; br.P99 = 900; br.Max = 2000; br.Count = 40; br.TimerMs = 0.5;
            hr.Add(br);
            string csv = BenchHistory.ToCsv(hr);
            Check(csv.StartsWith("time,p50_us") && csv.Contains("900"), "基准：历史 CSV 表头与数据行", null);

            // ---- 5.3 Rev 88 Chrome / 托盘三态 / 供电芯片 / 主题色点 / 命令面板 ----
            string spaced = Ui.Spaced("SYSTEM DRIFT");
            Check(!string.IsNullOrEmpty(spaced) && spaced.Contains("\u2002\u2009") && spaced.StartsWith("S") && spaced.EndsWith("T"),
                  "排版：Ui.Spaced 英文单词间距保留分隔符不粘连", spaced);

            // 自适应断点与标题栏零重叠验证（1600 / 1360 / 1000）
            shell.Width = 1600; shell.Height = 950;
            shell.Relayout();
            Pump(80);
            Check(!shell.RailCompact && shell.ThemeBarVisible && shell.TickerVisible && shell.PulseVisible && !shell.HasTitleBarOverlap(),
                  "布局：1600 宽标题栏无重叠且主题栏/播报完整显示", null);

            shell.Width = 1360; shell.Height = 860;
            shell.Relayout();
            Pump(80);
            Check(!shell.RailCompact && shell.ThemeBarVisible && !shell.TickerVisible && shell.PulseVisible && !shell.HasTitleBarOverlap(),
                  "布局：1360 中宽隐藏播报保标题栏无重叠", null);

            shell.Width = 1000; shell.Height = 680;
            shell.Relayout();
            Pump(80);
            Check(shell.RailCompact && !shell.ThemeBarVisible && !shell.TickerVisible && !shell.PulseVisible && !shell.HasTitleBarOverlap(),
                  "布局：1000 窄窗折叠主题栏与脉搏且标题栏无重叠", null);

            shell.Width = 1600; shell.Height = 950;
            shell.Relayout();
            Pump(80);

            // 侧栏 4 主题快速切换色点
            Check(shell.ThemeSwatchCount == 4, "主题：侧栏脚注渲染 4 枚主题色点", shell.ThemeSwatchCount + " 枚");
            string[] swatchIds = new string[] { "ember", "violet", "mint", "ice" };
            bool swatchOk = true;
            for (int si = 0; si < swatchIds.Length; si++)
            {
                shell.TestClickThemeSwatch(swatchIds[si]);
                Pump(100);
                if (T.ThemeId != swatchIds[si]) { swatchOk = false; break; }
            }
            Check(swatchOk && T.ThemeId == "ice" && T.Accent == iceAccent,
                  "主题：4 枚色点依次切换并安全恢复冰蓝", T.ThemeId);

            // 顶部一键游戏模式按钮、供电状态芯片与动态三态托盘引擎联动
            Check(shell.GameModeButtonHitReady, "Chrome：顶部游戏模式胶囊标记可点击命中", shell.GameModeButtonText);
            string basePwr = shell.PowerChipText;
            Check(basePwr == "AC" || basePwr == "电池" || basePwr == "节能",
                  "Chrome：供电状态芯片初始显示有效态势", basePwr);

            bool wasGm = shell.GameModeOn;
            Go.Daemon.DaemonHost prevDaemon = core.Daemon;
            Go.Daemon.DaemonHost testDaemon = new Go.Daemon.DaemonHost(core);
            testDaemon.TrayEnabled = false;
            try
            {
                core.Daemon = testDaemon;
                shell.RefreshDaemonUi();
                Pump(60);

                testDaemon.Power.HandleStatus(false, false);
                Pump(80);
                Check(shell.PowerChipText == "电池", "供电：切至电池供电芯片实时显示「电池」", shell.PowerChipText);

                testDaemon.Power.HandleStatus(false, true);
                Pump(80);
                Check(shell.PowerChipText == "节能"
                      && shell.LastTrayState == Go.Tray.TrayVisualState.Alert
                      && testDaemon.Tray.VisualState == Go.Tray.TrayVisualState.Alert,
                      "托盘：节能模式限制性能触发 Alert 琥珀态", testDaemon.Tray.VisualState.ToString());

                testDaemon.Power.HandleStatus(true, false);
                Pump(80);
                Check(shell.PowerChipText == "AC"
                      && shell.LastTrayState == Go.Tray.TrayVisualState.Idle
                      && testDaemon.Tray.VisualState == Go.Tray.TrayVisualState.Idle,
                      "托盘：恢复 AC 电源切回 Idle 冰蓝态", testDaemon.Tray.VisualState.ToString());

                if (core.Elevated)
                {
                    shell.TestClickGameMode();
                    Pump(80);
                    bool gmOnOk = shell.GameModeOn
                                  && shell.GameModeButtonText == "游戏模式 ON"
                                  && shell.LastTrayState == Go.Tray.TrayVisualState.Guard
                                  && testDaemon.Tray.VisualState == Go.Tray.TrayVisualState.Guard;
                    shell.TestClickGameMode();
                    Pump(80);
                    bool gmOffOk = !shell.GameModeOn && shell.GameModeButtonText == "游戏模式";
                    Check(gmOnOk && gmOffOk, "Chrome：一键游戏模式点击切态并联动 Guard 托盘态", shell.GameModeButtonText);
                }
                else
                {
                    Check(shell.GameModeButtonHitReady && shell.GameModeButtonText == "游戏模式",
                          "Chrome：标准权限下保持待机态且不可误触发", shell.GameModeButtonText);
                }
            }
            finally
            {
                if (shell.GameModeOn != wasGm) shell.SetGameMode(wasGm);
                core.Daemon = prevDaemon;
                shell.RefreshDaemonUi();
                Pump(60);
            }

            // 命令面板 Rev88 高价值快捷命令检索命中
            shell.TestPalette("图形与多显");
            Pump(40);
            bool pGpu = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("图形", StringComparison.Ordinal) >= 0;
            shell.TestPalette("三模定时");
            Pump(40);
            bool pBench = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("三模", StringComparison.Ordinal) >= 0;
            shell.TestPalette("账本导出");
            Pump(40);
            bool pExp = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("账本导出", StringComparison.Ordinal) >= 0;
            shell.HidePalette();
            Pump(40);
            Check(pGpu && pBench && pExp, "命令面板：Rev88 快捷命令检索直达", "gpu=" + pGpu + " bench=" + pBench + " export=" + pExp);

            // ---- 5.4 Rev 89 控件语义与自检深化 ----
            // 1) DeltaChip 切换 higherIsBetter (false -> true) 同方向同文案下红绿极性翻转
            DeltaChip polChip = new DeltaChip();
            polChip.Set(1, "12.5%", false);
            TextBlock polText = polChip.Child as TextBlock;
            SolidColorBrush polFgLow = polText == null ? null : polText.Foreground as SolidColorBrush;
            SolidColorBrush polBgLow = polChip.Background as SolidColorBrush;
            polChip.Set(1, "12.5%", true);
            SolidColorBrush polFgHigh = polText == null ? null : polText.Foreground as SolidColorBrush;
            SolidColorBrush polBgHigh = polChip.Background as SolidColorBrush;
            polChip.Set(-1, "12.5%", true);
            SolidColorBrush polFgNeg = polText == null ? null : polText.Foreground as SolidColorBrush;
            Check(polFgLow != null && polFgHigh != null && polFgNeg != null
                  && polFgLow.Color == T.C(T.Bad) && polFgHigh.Color == T.C(T.Ok) && polFgNeg.Color == T.C(T.Bad)
                  && polBgLow != null && polBgHigh != null
                  && polBgLow.Color == T.C(Ui.Wash(T.Bad, 0x14)) && polBgHigh.Color == T.C(Ui.Wash(T.Ok, 0x14)),
                  "Rev89：DeltaChip 同方向同文案切换 higherIsBetter 正确翻转 Bad/Ok 极性",
                  (polFgLow == null ? "null" : polFgLow.Color.ToString()) + " -> " + (polFgHigh == null ? "null" : polFgHigh.Color.ToString()));

            // 2) 未入账 ActionRow 关闭时绝不假绿报「已还原」，给出账本无记录提醒并回弹开关
            ActionDef extAct = new ActionDef();
            extAct.Id = "__uitest_unledgered";
            extAct.Group = "自检";
            extAct.Name = "外部开启测试项";
            extAct.Ops = new Op[] { new Op { Kind = "Reg", Hive = "HKCU", Path = @"Software\GameOptimizer\UiTestMissing", Name = "AbsentFlag", Delete = true } };
            Ui.ActionRowSpec rowSpec = new Ui.ActionRowSpec();
            rowSpec.Core = core;
            rowSpec.Def = extAct;
            rowSpec.Kind = Ui.ActionRowKind.Columns;
            Ui.ActionRow extRow = Ui.Row(rowSpec);
            extRow.Refresh();
            bool beforeOn = extRow.State == AState.Applied && extRow.SwitchOn;
            extRow.TestUserToggle(false);
            string fb = extRow.LastFeedback;
            Check(beforeOn
                  && !extRow.LastFeedbackOk
                  && fb == "该项并非通过本工具修改（账本无记录），未改动系统设置"
                  && fb.IndexOf("已还原", StringComparison.Ordinal) < 0
                  && shell.LastToastText == fb && !shell.LastToastOk
                  && extRow.SwitchOn,
                  "Rev89：关闭未入账 ActionRow 不误报已还原并正确回弹开关",
                  "ok=" + extRow.LastFeedbackOk + " sw=" + extRow.SwitchOn + " msg=" + fb);

            // 3) Waveform 关闭阈值线时不再被 200 硬下限压扁小量程走势
            RenderTargetBitmap wfBmp = new RenderTargetBitmap(300, 120, 96, 96, PixelFormats.Pbgra32);
            Waveform wfSmall = new Waveform(16);
            wfSmall.ShowThreshold = false;
            wfSmall.AutoScale = true;
            wfSmall.Push(10); wfSmall.Push(22); wfSmall.Push(34); wfSmall.Push(40);
            wfSmall.Width = 300; wfSmall.Height = 120;
            wfSmall.Measure(new Size(300, 120));
            wfSmall.Arrange(new Rect(0, 0, 300, 120));
            wfSmall.UpdateLayout();
            wfBmp.Render(wfSmall);

            Waveform wfThresh = new Waveform(16);
            wfThresh.ShowThreshold = true;
            wfThresh.AutoScale = true;
            wfThresh.Threshold = 16.7;
            wfThresh.Push(10); wfThresh.Push(22); wfThresh.Push(34); wfThresh.Push(40);
            wfThresh.Width = 300; wfThresh.Height = 120;
            wfThresh.Measure(new Size(300, 120));
            wfThresh.Arrange(new Rect(0, 0, 300, 120));
            wfThresh.UpdateLayout();
            wfBmp.Render(wfThresh);
            Check(wfSmall.CurrentCeil > 40.0 && wfSmall.CurrentCeil < 60.0 && wfThresh.CurrentCeil >= 200.0,
                  "Rev89：Waveform ShowThreshold=false 取消 200 下限保底，ShowThreshold=true 保留",
                  "noThresh=" + wfSmall.CurrentCeil.ToString("0.0") + " withThresh=" + wfThresh.CurrentCeil.ToString("0.0"));

            // 4) Ctrl+K 面板 Rev 89 副标题更新命中
            shell.TestPalette("WeGame");
            Pump(40);
            bool pScan = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("多源扫描", StringComparison.Ordinal) >= 0;
            shell.TestPalette("线程调度乒乓");
            Pump(40);
            bool pPing = shell.PaletteResultCount > 0 && shell.PaletteFirstTitle.IndexOf("三模定时", StringComparison.Ordinal) >= 0;
            shell.HidePalette();
            Pump(40);
            Check(pScan && pPing, "Rev89：命令面板快捷命令副标题涵盖 WeGame 与线程调度乒乓", "scan=" + pScan + " ping=" + pPing);

            // ---- 5.5 Rev 110 AETHER & Rev 130 VANGUARD 新增前端交互自检 ----
            RunRev110UiChecks(core, shell);
            RunRev130UiChecks(core, shell);
            }
            catch (Exception ex)
            {
                _fail++;
                _log.AppendLine("[FAIL] 未捕获异常: " + ex);
            }
            finally
            {
                RestoreUserState();
                if (app != null)
                {
                    try { app.Shutdown(); }
                    catch (Exception ex) { Diag.Fail("uitest.app.shutdown", ex); }
                }
            }

            _log.AppendLine("");
            _log.AppendLine("RESULT  pass=" + _pass + "  fail=" + _fail);
            string text = _log.ToString();
            try { File.WriteAllText(outFile, text, new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                // 与 SelfTest 同口径：写不出去就不能报成功，否则调用方读到的是上一轮的报告。
                Diag.Fail("uitest.output", ex);
                _fail++;
                Console.WriteLine("[FAIL] 报告无法写入 " + outFile + "：" + ex.Message);
            }
            Console.WriteLine(text);
            return _fail == 0 ? 0 : 1;
        }

        private static int CountLogDataRows(Go.Pages.LogPage lp)
        {
            if (lp == null) return -1;
            FieldInfo fi = typeof(Go.Pages.LogPage).GetField("_lines", BindingFlags.NonPublic | BindingFlags.Instance);
            StackPanel linesPanel = fi == null ? null : fi.GetValue(lp) as StackPanel;
            if (linesPanel == null) return -1;
            int count = 0;
            for (int i = 0; i < linesPanel.Children.Count; i++)
            {
                Grid g = linesPanel.Children[i] as Grid;
                if (g != null && g.ColumnDefinitions.Count == 3) count++;
            }
            return count;
        }

        private static bool LogContainsBody(Go.Pages.LogPage lp, string needle)
        {
            if (lp == null || string.IsNullOrEmpty(needle)) return false;
            FieldInfo fi = typeof(Go.Pages.LogPage).GetField("_lines", BindingFlags.NonPublic | BindingFlags.Instance);
            StackPanel linesPanel = fi == null ? null : fi.GetValue(lp) as StackPanel;
            if (linesPanel == null) return false;
            for (int i = 0; i < linesPanel.Children.Count; i++)
            {
                Grid g = linesPanel.Children[i] as Grid;
                if (g == null || g.ColumnDefinitions.Count != 3) continue;
                for (int k = 0; k < g.Children.Count; k++)
                {
                    TextBlock tb = g.Children[k] as TextBlock;
                    if (tb != null && Grid.GetColumn(tb) == 2 && tb.Text != null
                        && tb.Text.IndexOf(needle, StringComparison.Ordinal) >= 0)
                        return true;
                }
            }
            return false;
        }

        private static int LogLastCount(Go.Pages.LogPage lp)
        {
            if (lp == null) return -1;
            FieldInfo fi = typeof(Go.Pages.LogPage).GetField("_lastCount", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) return -1;
            object v = fi.GetValue(lp);
            return v is int ? (int)v : -1;
        }

        private static void Layout(FrameworkElement root, double w, double h)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        }

        private static void Pump(int ms)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer t = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
                delegate { frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            t.Start();
            Dispatcher.PushFrame(frame);
            t.Stop();
        }
    }
}