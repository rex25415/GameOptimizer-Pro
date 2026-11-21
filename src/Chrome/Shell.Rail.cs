// ============================================================================
//  Shell.Rail.cs — 左导轨（Shell 分片）
//  品牌标记 / 分组导航 / 状态脚注
//  来源：Shell.cs（1324 行单体）按职责拆分，partial 共享字段，行为零变化。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Go
{
    public partial class Shell : Window
    {
        // ---- 系统偏离度（导轨中段那个原本空着的区域）------------------------
        //  产品对用户的承诺是"每一次改动都可撤销"，那么"此刻有多少项偏离出厂状态"
        //  就不该只藏在还原中心里——它必须在任何页面上都看得见。这也是导轨从
        //  导航末行到状态脚注之间那 400px 纯黑的用途：把空白换成读数。
        private StackPanel _driftBox, _driftFull;
        private Grid _driftTrack;
        private TextBlock _driftNum, _driftNote, _driftMini;
        private Border _driftFill;
        private int _driftShown = -1;
        /// <summary>0 = 不显示，1 = 一行，2 = 完整。由 AdjustRailFit 决定。</summary>
        private int _driftMode = 2;
        /// <summary>进度条的满量程。超过它的读数不再靠长度表达，改由颜色表达。</summary>
        private const int DriftCap = 24;

        // ---- 左导轨 ---------------------------------------------------------
        private Border BuildRail()
        {
            Grid rail = new Grid();
            rail.Background = T.RailBg();
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rail.RowDefinitions.Add(new RowDefinition());
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Rectangle edge = new Rectangle();
            edge.Width = 1;
            edge.HorizontalAlignment = HorizontalAlignment.Right;
            edge.Fill = T.Grad(T.AccentA(0x52), T.LineSoft, 90);
            Grid.SetRowSpan(edge, 3);
            rail.Children.Add(edge);

            // 品牌徽章（Rev120 HYPERION：36×36 浮雕双色光晕晶框）
            Grid brand = new Grid();
            brand.Margin = new Thickness(16, 15, 16, 12);
            brand.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brand.ColumnDefinitions.Add(new ColumnDefinition());

            Border markFrame = new Border();
            markFrame.Width = 36; markFrame.Height = 36;
            markFrame.CornerRadius = new CornerRadius(10);
            markFrame.Background = T.Grad(T.AccentA(0x2E), T.Accent2A(0x14), 135);
            markFrame.BorderBrush = T.S(T.AccentA(0x72));
            markFrame.BorderThickness = new Thickness(1);
            markFrame.VerticalAlignment = VerticalAlignment.Center;
            markFrame.SnapsToDevicePixels = true;

            Grid mark = new Grid();
            mark.Width = 22; mark.Height = 22;
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.Children.Add(Ui.Icon(Brand.MarkPath, 22, T.AccentA(0x46), Brand.MarkStroke * 22.0 / 24.0 * 2.4));
            mark.Children.Add(Ui.Icon(Brand.MarkPath, 22, T.Accent, Brand.MarkStroke * 22.0 / 24.0));
            mark.Children.Add(Ui.Icon(Brand.MarkInnerPath, 22, T.Hi, Brand.MarkInnerStroke * 22.0 / 24.0));
            markFrame.Child = mark;
            brand.Children.Add(markFrame);

            StackPanel wording = new StackPanel();
            wording.VerticalAlignment = VerticalAlignment.Center;
            wording.Margin = new Thickness(12, 0, 0, 0);
            TextBlock wm = Ui.Label(Brand.ProductName, T.Hi);
            wm.FontSize = T.FsBody;
            wm.FontWeight = FontWeights.SemiBold;
            wording.Children.Add(wm);
            TextBlock sub = Ui.Label(Brand.ProductSub, T.AccentDim);
            sub.FontSize = 9.5;
            sub.Margin = new Thickness(0, 3, 0, 0);
            wording.Children.Add(sub);
            Grid.SetColumn(wording, 1);
            brand.Children.Add(wording);
            _brandWording = wording;
            _brand = brand;

            rail.Children.Add(brand);
            Boot.M("rail_brand");


            _navList = new StackPanel();
            // Top 对齐是必须的：默认 Stretch 会让这个 StackPanel 撑满整行，
            // 于是 ActualHeight 等于"行高"而不是"导航内容高"，
            // 任何想算"还剩多少空间"的逻辑都会得到 0。
            _navList.VerticalAlignment = VerticalAlignment.Top;
            _navList.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetRow(_navList, 1);
            rail.Children.Add(_navList);

            rail.Children.Add(BuildDrift());
            Boot.M("rail_drift");

            bool group2 = false, group3 = false;
            foreach (Page p in _core.Pages)
            {
                if (!group2 && p.Key == "tuning") { _navList.Children.Add(GroupLabel("调优内核")); group2 = true; }
                if (!group3 && p.Key == "profiles") { _navList.Children.Add(GroupLabel("保障与档案")); group3 = true; }
                NavRow row = new NavRow(p, this);
                _navs[p.Key] = row;
                _navList.Children.Add(row);
            }
            Boot.M("rail_nav");

            StackPanel foot = new StackPanel();
            foot.Margin = new Thickness(14, 0, 14, 12);
            Grid.SetRow(foot, 2);
            _footPanel = foot;

            // SYSTEM BUS 玻璃读数微卡
            Border busCard = new Border();
            busCard.CornerRadius = new CornerRadius(T.RadiusMd + 2);
            busCard.Background = T.SubCardBg();
            busCard.BorderBrush = T.S(T.LineSoft);
            busCard.BorderThickness = new Thickness(1);
            busCard.Padding = new Thickness(11, 9, 11, 9);
            busCard.Margin = new Thickness(0, 0, 0, 9);
            StackPanel busInner = new StackPanel();

            _sysCpu = Ui.Label("System Bus", T.AccentDim);
            _sysCpu.FontSize = 9.5;
            busInner.Children.Add(_sysCpu);
            Border g2 = new Border(); g2.Height = 5; busInner.Children.Add(g2);

            Topology topo = _core.Topo;
            _sysGpu = Ui.Mono(_core.CpuName + " · " + topo.Cores + "C/" + topo.Logical + "T", T.FsNano, T.Text);
            _sysGpu.Margin = new Thickness(0, 0, 0, 2);
            busInner.Children.Add(_sysGpu);
            TextBlock gpu = Ui.Mono(_core.GpuName + " · " + topo.CcdLabel, T.FsNano, T.Dim);
            string dispSpec = MainDisplaySpec(_core);
            gpu.Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(dispSpec) ? 0 : 2);
            busInner.Children.Add(gpu);
            if (!string.IsNullOrEmpty(dispSpec))
            {
                TextBlock disp = Ui.Mono(dispSpec, T.FsNano, T.Mute);
                busInner.Children.Add(disp);
            }
            busCard.Child = busInner;
            foot.Children.Add(busCard);
            _footTexts.Add(busCard);

            Grid stat = new Grid();
            stat.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stat.ColumnDefinitions.Add(new ColumnDefinition());
            stat.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stat.Cursor = Cursors.Hand;
            stat.Background = Brushes.Transparent;

            _statDot = new Ellipse();
            _statDot.Width = 5; _statDot.Height = 5;
            _statDot.Fill = T.S(_core.Elevated ? T.Ok : T.Warn);
            _statDot.VerticalAlignment = VerticalAlignment.Center;
            stat.Children.Add(_statDot);

            _statLine = Ui.Mono(_core.Elevated ? "ADMIN" : "STANDARD", T.FsNano, T.Dim);
            _statLine.Margin = new Thickness(7, 0, 0, 0);
            Grid.SetColumn(_statLine, 1);
            stat.Children.Add(_statLine);

            DateTime bootNow = DateTime.Now;
            _clockMin = bootNow.Hour * 60 + bootNow.Minute;
            _clock = Ui.Mono(bootNow.ToString("HH:mm"), T.FsNano, T.Mute);
            Grid.SetColumn(_clock, 2);
            stat.Children.Add(_clock);
            foot.Children.Add(stat);

            Tip.Attach(stat, _core.Elevated
                ? "已具备管理员权限：全部注册表、电源子项、内核调度与待机清理能力均已解锁。"
                : "当前为只读模式：读取与诊断可用，写入系统需要管理员权限。点此以管理员身份重启。");
            Pressable.On(stat, delegate { RelaunchElevated(); });

            // Rev130：折叠窄导轨时提供独立 1 键主题循环圆钮（展开态隐藏，与完整主题栏互补）
            Grid compactOrb = new Grid();
            compactOrb.Width = 20;
            compactOrb.Height = 20;
            compactOrb.Margin = new Thickness(0, 8, 0, 0);
            compactOrb.HorizontalAlignment = HorizontalAlignment.Center;
            compactOrb.Background = Brushes.Transparent;
            compactOrb.Cursor = Cursors.Hand;
            compactOrb.SnapsToDevicePixels = false;
            compactOrb.Visibility = _compactRail ? Visibility.Visible : Visibility.Collapsed;
            Ellipse cRing = new Ellipse();
            cRing.Width = 18; cRing.Height = 18;
            cRing.Stroke = T.S(T.AccentA(0xB0));
            cRing.StrokeThickness = 1.4;
            cRing.Fill = T.S(T.AccentWash);
            cRing.HorizontalAlignment = HorizontalAlignment.Center;
            cRing.VerticalAlignment = VerticalAlignment.Center;
            cRing.IsHitTestVisible = false;
            compactOrb.Children.Add(cRing);
            Ellipse cDot = new Ellipse();
            cDot.Width = 9; cDot.Height = 9;
            cDot.Fill = T.S(T.Accent);
            cDot.HorizontalAlignment = HorizontalAlignment.Center;
            cDot.VerticalAlignment = VerticalAlignment.Center;
            cDot.IsHitTestVisible = false;
            compactOrb.Children.Add(cDot);
            Pressable.On(compactOrb, delegate
            {
                int nextIdx = 0;
                for (int ti = 0; ti < T.Themes.Length; ti++)
                    if (T.Themes[ti].Id == T.ThemeId) { nextIdx = (ti + 1) % T.Themes.Length; break; }
                ApplyThemeLive(T.Themes[nextIdx].Id);
            });
            Tip.Attach(compactOrb, "切换主题配色（当前：" + T.ThemeId + " · 点击循环切换）");
            _compactThemeOrb = compactOrb;
            foot.Children.Add(compactOrb);

            Border g3 = new Border(); g3.Height = 9; foot.Children.Add(g3);

            // 守护状态：与托盘图标互为镜像
            Grid guard = new Grid();
            guard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            guard.ColumnDefinitions.Add(new ColumnDefinition());
            guard.Margin = new Thickness(0, 0, 0, 7);
            guard.Background = Brushes.Transparent;
            _daemonDot = new Ellipse();
            _daemonDot.Width = 5; _daemonDot.Height = 5;
            _daemonDot.Fill = T.S(T.AccentDim);
            _daemonDot.VerticalAlignment = VerticalAlignment.Center;
            guard.Children.Add(_daemonDot);
            _daemonLine = Ui.Mono("GUARD · 待命", T.FsNano, T.Dim);
            _daemonLine.Margin = new Thickness(7, 0, 0, 0);
            Grid.SetColumn(_daemonLine, 1);
            guard.Children.Add(_daemonLine);
            Tip.Attach(guard, "游戏守护：检测到已建档的游戏启动时，自动挂载 CCD0 亲和 / 独显优先 / 0.5ms 精度，退出后按账本复原。");
            foot.Children.Add(guard);
            _footTexts.Add(guard);

            // Rev130 修复：用 Grid 同心叠放双矢量 Ellipse（彻底根治旧 Border+1.5 线宽在高 DPI 下的 3px 偏心与跳动）
            StackPanel themeBar = new StackPanel();
            themeBar.Orientation = Orientation.Horizontal;
            themeBar.VerticalAlignment = VerticalAlignment.Center;
            themeBar.Margin = new Thickness(0, 6, 0, 6);
            TextBlock themeLbl = Ui.Tx("主题", T.FsNano, T.Mute, T.FCn);
            themeLbl.VerticalAlignment = VerticalAlignment.Center;
            themeLbl.Margin = new Thickness(0, 0, 8, 0);
            themeBar.Children.Add(themeLbl);
            _themeRings = new FrameworkElement[T.Themes.Length];
            string activeThemeName = "";
            for (int ti = 0; ti < T.Themes.Length; ti++)
            {
                T.AccentTheme th = T.Themes[ti];
                string tid = th.Id;
                bool activeTh = tid == T.ThemeId;
                if (activeTh) activeThemeName = th.Name;

                Grid swatch = new Grid();
                swatch.Width = 20;
                swatch.Height = 20;
                swatch.Margin = new Thickness(0, 0, 4, 0);
                swatch.Background = Brushes.Transparent;
                swatch.Cursor = Cursors.Hand;
                swatch.SnapsToDevicePixels = false;
                swatch.UseLayoutRounding = false;

                Ellipse halo = new Ellipse();
                halo.Width = 20;
                halo.Height = 20;
                halo.Fill = activeTh ? T.S(th.Wash) : Brushes.Transparent;
                halo.HorizontalAlignment = HorizontalAlignment.Center;
                halo.VerticalAlignment = VerticalAlignment.Center;
                halo.IsHitTestVisible = false;
                swatch.Children.Add(halo);

                Ellipse outerRing = new Ellipse();
                outerRing.Width = 18;
                outerRing.Height = 18;
                outerRing.StrokeThickness = 1.5;
                outerRing.Stroke = activeTh ? T.S(T.Hi) : Brushes.Transparent;
                outerRing.HorizontalAlignment = HorizontalAlignment.Center;
                outerRing.VerticalAlignment = VerticalAlignment.Center;
                outerRing.IsHitTestVisible = false;
                swatch.Children.Add(outerRing);

                Ellipse dot = new Ellipse();
                dot.Width = 10;
                dot.Height = 10;
                dot.Fill = T.S(th.Accent);
                dot.HorizontalAlignment = HorizontalAlignment.Center;
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.IsHitTestVisible = false;
                swatch.Children.Add(dot);

                swatch.MouseEnter += delegate
                {
                    if (tid != T.ThemeId)
                    {
                        outerRing.Stroke = T.S(T.LineHi);
                        halo.Fill = T.S(T.LineSoft);
                    }
                };
                swatch.MouseLeave += delegate
                {
                    if (tid != T.ThemeId)
                    {
                        outerRing.Stroke = Brushes.Transparent;
                        halo.Fill = Brushes.Transparent;
                    }
                };
                Pressable.On(swatch, delegate { ApplyThemeLive(tid); });
                Tip.Attach(swatch, "主题：" + th.Name + " (" + tid + ")");
                _themeRings[ti] = swatch;
                themeBar.Children.Add(swatch);
            }
            if (activeThemeName.Length > 0)
            {
                TextBlock curTh = Ui.Tx(activeThemeName, T.FsNano, T.Accent, T.FCn);
                curTh.VerticalAlignment = VerticalAlignment.Center;
                curTh.Margin = new Thickness(4, 0, 0, 0);
                themeBar.Children.Add(curTh);
            }
            _themeBar = themeBar;
            foot.Children.Add(themeBar);
            _footTexts.Add(themeBar);

            Grid verRow = new Grid();
            verRow.ColumnDefinitions.Add(new ColumnDefinition());
            verRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel verLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Border verDot = Ui.Dot(T.Accent, 4);
            verDot.Margin = new Thickness(0, 0, 6, 0);
            verDot.VerticalAlignment = VerticalAlignment.Center;
            verLeft.Children.Add(verDot);
            _sysVer = Ui.Mono("REV " + Brand.Rev + " · " + Brand.RevStamp, T.FsNano, T.Mute);
            verLeft.Children.Add(_sysVer);
            verRow.Children.Add(verLeft);

            _installChip = Ui.Tx("", T.FsNano, T.AccentDim, T.FCn);
            _installChip.VerticalAlignment = VerticalAlignment.Center;
            _installChip.HorizontalAlignment = HorizontalAlignment.Right;
            _installChip.Cursor = Cursors.Hand;
            _installChip.MouseEnter += delegate { if (_installChip != null) _installChip.Foreground = T.S(T.Accent); };
            _installChip.MouseLeave += delegate { RefreshInstallChipColor(); };
            Pressable.On(_installChip, delegate { InstallToDesktopFromUi(); });
            Grid.SetColumn(_installChip, 1);
            verRow.Children.Add(_installChip);
            RefreshInstallUi();

            foot.Children.Add(verRow);
            _footTexts.Add(verRow);

            rail.Children.Add(foot);
            Boot.M("rail_foot");

            _railGrid = rail;
            // 导轨重建（换主题 / 折叠）后，新的状态点还没挂呼吸动画，而旧的已经进了垃圾堆：
            // 不复位 _guardPulsing 的话，守护命中的呼吸效果会永远不再出现。
            ResetGuardPulseForRebuild();
            RefreshDrift();
            // 行高变化（窗口缩放）与导航高度变化（折叠/展开）是唯一会改变"够不够"的两件事。
            rail.SizeChanged += delegate { AdjustRailFit(); };
            _navList.SizeChanged += delegate { AdjustRailFit(); };
            Border wrap = new Border();
            wrap.Child = rail;
            wrap.Width = _railW;
            return wrap;
        }

        /// <summary>
        /// 系统偏离度：账本里还有多少条"改了但没还原"的记录。
        /// 它替代的是导轨中段那一片纯黑——空白不是被填满了，而是被赋予了含义。
        /// </summary>
        private UIElement BuildDrift()
        {
            StackPanel box = new StackPanel();
            box.Margin = new Thickness(14, 0, 14, 10);
            box.VerticalAlignment = VerticalAlignment.Bottom;
            box.Background = Brushes.Transparent;
            box.Cursor = Cursors.Hand;
            Grid.SetRow(box, 1);

            Border card = new Border();
            card.CornerRadius = new CornerRadius(T.RadiusMd + 2);
            card.Background = T.SubCardBg();
            card.BorderBrush = T.S(T.LineSoft);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(11, 9, 11, 10);
            StackPanel cardInner = new StackPanel();

            // ---- 完整形态：标签 + 大数字 + 比例尺 ----
            StackPanel full = new StackPanel();
            TextBlock cap = Ui.Label("SYSTEM DRIFT", T.AccentDim);
            full.Children.Add(cap);

            Grid nrow = new Grid();
            nrow.Margin = new Thickness(0, 2, 0, 0);
            nrow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nrow.ColumnDefinitions.Add(new ColumnDefinition());

            _driftNum = Ui.Num("\u2014", 28, T.Hi);
            _driftNum.IsHitTestVisible = false;
            nrow.Children.Add(_driftNum);

            _driftNote = Ui.Mono("", T.FsNano, T.Mute);
            _driftNote.VerticalAlignment = VerticalAlignment.Bottom;
            _driftNote.Margin = new Thickness(10, 0, 0, 5);
            _driftNote.TextTrimming = TextTrimming.CharacterEllipsis;
            _driftNote.IsHitTestVisible = false;
            Grid.SetColumn(_driftNote, 1);
            nrow.Children.Add(_driftNote);
            full.Children.Add(nrow);

            _driftTrack = new Grid();
            _driftTrack.Height = 4;
            _driftTrack.Margin = new Thickness(0, 7, 0, 0);
            _driftTrack.IsHitTestVisible = false;

            Border track = new Border();
            track.Background = T.S(T.LineMid);
            track.CornerRadius = new CornerRadius(2);
            _driftTrack.Children.Add(track);

            _driftFill = new Border();
            _driftFill.Height = 4;
            _driftFill.Width = 0;
            _driftFill.HorizontalAlignment = HorizontalAlignment.Left;
            _driftFill.Background = T.S(T.Ok);
            _driftFill.CornerRadius = new CornerRadius(2);
            _driftTrack.Children.Add(_driftFill);
            full.Children.Add(_driftTrack);

            // ---- 紧凑形态：一行 ----
            _driftMini = Ui.Mono("", T.FsNano, T.Mute);
            _driftMini.TextTrimming = TextTrimming.CharacterEllipsis;
            _driftMini.IsHitTestVisible = false;
            _driftMini.Visibility = Visibility.Collapsed;

            cardInner.Children.Add(full);
            cardInner.Children.Add(_driftMini);
            card.Child = cardInner;
            box.Children.Add(card);

            box.MouseEnter += delegate { card.BorderBrush = T.S(T.AccentA(0x5A)); };
            box.MouseLeave += delegate { card.BorderBrush = T.S(T.LineSoft); };

            _driftFull = full;

            Tip.Attach(box, "账本里还有多少条「已修改、尚未还原」的系统设置。\n任何一条都可以在还原中心单独撤销。");
            Pressable.On(box, delegate { Go("restore"); });

            _driftBox = box;
            return box;
        }

        /// <summary>
        /// 按"这一行还剩多少高度"决定偏离度读显不显示。
        ///
        /// 这块是底对齐的，而导航列表是顶对齐的，两者活在同一行里：
        /// 行高不够时，底对齐的那块会向上溢出、压到导航的最后几行上——
        /// 在 1300×800 的窗口里，"SYSTEM DRIFT" 正好叠在"审计日志"那一行上。
        /// 读数可以少一块，但不能叠字。
        /// </summary>
        private void AdjustRailFit()
        {
            if (_railGrid == null || _driftBox == null || _navList == null) return;
            if (_railGrid.RowDefinitions.Count < 2) return;

            double rowH = _railGrid.RowDefinitions[1].ActualHeight;
            if (rowH <= 0) return;
            double navH = _navList.ActualHeight;
            if (navH <= 0) return;
            double free = rowH - navH;

            // 三档：完整（约 100px）/ 一行（约 30px）/ 不显示。
            // 阈值写成常量而不是"量出来的当前高度"：偏离度块自己不会影响 free
            // （导航顶对齐、它底对齐），所以固定的阈值不会来回抖。
            int mode = 0;                       // 0 隐藏 1 一行 2 完整
            if (!_compactRail)
                mode = free >= 100 ? 2 : (free >= 30 ? 1 : 0);

            Visibility boxVis = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_driftBox.Visibility != boxVis) _driftBox.Visibility = boxVis;
            if (_driftFull != null)
            {
                Visibility fullVis = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
                if (_driftFull.Visibility != fullVis) _driftFull.Visibility = fullVis;
            }
            if (_driftMini != null)
            {
                Visibility miniVis = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
                if (_driftMini.Visibility != miniVis) _driftMini.Visibility = miniVis;
            }
            _driftMode = mode;
        }

        /// <summary>每拍复核一次；没有变化就什么都不做（不重排、不分配）。</summary>
        private void RefreshDrift()
        {
            if (_driftNum == null) return;
            int n = _core.Engine.PendingCount;
            if (n == _driftShown) return;
            _driftShown = n;

            bool clean = n == 0;
            bool heavy = n >= 12;
            _driftNum.Text = n.ToString();
            _driftNum.Foreground = T.S(clean ? T.Ok : (heavy ? T.Warn : T.Hi));
            _driftNote.Text = clean ? "出厂状态" : "项待还原";
            // 颜色只在真的需要被注意时才变：0 = 绿，1-11 = 强调色，>=12 = 琥珀。
            _driftFill.Background = T.S(clean ? T.Ok : (heavy ? T.Warn : T.Accent));

            if (_driftMini != null)
            {
                _driftMini.Text = "DRIFT \u00B7 " + n + (clean ? " \u5DF2\u5F52\u96F6" : " \u9879\u5F85\u8FD8\u539F");
                _driftMini.Foreground = T.S(clean ? T.Ok : (heavy ? T.Warn : T.Accent));
            }

            double tw = _driftTrack == null ? 0 : _driftTrack.ActualWidth;
            if (tw <= 0) tw = 196;
            double frac = n / (double)DriftCap;
            if (frac > 1) frac = 1;
            if (frac < 0) frac = 0;
            _driftFill.Width = Math.Round(tw * frac);
        }

        private static string MainDisplaySpec(Core core)
        {
            if (core == null || core.Displays == null || core.Displays.Count == 0) return "";
            Hardware.DisplayInfo main = null;
            for (int i = 0; i < core.Displays.Count; i++)
            {
                if (core.Displays[i].Main) { main = core.Displays[i]; break; }
            }
            if (main == null) main = core.Displays[0];
            if (main.Width <= 0 || main.Height <= 0) return "";
            string s = main.Width + "\u00D7" + main.Height;
            if (main.RefreshHz > 0) s += " \u00B7 " + main.RefreshHz + "Hz";
            return s;
        }

        private static UIElement GroupLabel(string text)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(20, 12, 20, 6);
            sp.Tag = "group";
            sp.Children.Add(Ui.Rule(T.LineSoft, 0));
            Border gap = new Border(); gap.Height = 9; sp.Children.Add(gap);
            TextBlock t = Ui.Label(text, T.Dim);
            t.FontSize = T.FsNano;
            sp.Children.Add(t);
            return sp;
        }
    }
}