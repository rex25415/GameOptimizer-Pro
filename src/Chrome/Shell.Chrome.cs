// ============================================================================
//  Shell.Chrome.cs — 标题栏（Shell 分片）
//
//  【Rev64 交互重建】
//  旧实现用 MouseLeftButtonDown + DragMove() 自己模拟拖动。后果：
//    · 在标题栏空白处"点一下"也会进入拖动循环（点击被吞掉）；
//    · 双击最大化要靠自己数 ClickCount，和系统抢活；
//    · 没有 Aero Snap（拖到屏幕边缘不吸附）；
//    · 最大化状态下拖动行为不符合 Windows 习惯；
//    · 右键标题栏没有系统菜单。
//  现在把标题栏交给 WindowChrome 的 CaptionHeight：拖动、双击最大化/还原、
//  拖到边缘吸附、右键系统菜单、Alt+Space 全部由 Windows 按原厂语义处理。
//  我们只把真正可点的控件标记成 IsHitTestVisibleInChrome，其余交给系统。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Go
{
    public partial class Shell : Window
    {
        /// <summary>让控件从"标题栏拖动区"里挖出来，恢复成普通可点元素。</summary>
        private static void ChromeHit(UIElement el)
        {
            WindowChrome.SetIsHitTestVisibleInChrome(el, true);
        }

        // ---- 标题栏 ---------------------------------------------------------
        private UIElement BuildTitleBar()
        {
            Grid bar = new Grid();
            bar.Background = T.S(0xC809101E);

            Rectangle line = new Rectangle();
            line.Height = 1;
            line.VerticalAlignment = VerticalAlignment.Bottom;
            line.Fill = T.S(T.Line);
            line.IsHitTestVisible = false;
            Grid.SetColumnSpan(line, 2);
            bar.Children.Add(line);

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.Margin = new Thickness(T.PadX - 8, 0, 0, 0);
            left.VerticalAlignment = VerticalAlignment.Center;

            _histBack = HistBtn(Glyph.Chevron, true, delegate { Navigate(-1); });
            left.Children.Add(_histBack);
            _histFwd = HistBtn(Glyph.Chevron, false, delegate { Navigate(1); });
            left.Children.Add(_histFwd);

            Border spacer = new Border();
            spacer.Width = 10;
            spacer.IsHitTestVisible = false;
            left.Children.Add(spacer);

            TextBlock c1 = Ui.Label("Vanguard", T.Mute);
            c1.FontSize = T.FsNano;
            left.Children.Add(c1);
            TextBlock sep = Ui.Mono("/", T.FsNano, T.AccentA(0x90));
            sep.Margin = new Thickness(8, 0, 8, 0);
            left.Children.Add(sep);
            _crumb = Ui.Label("Overview", T.Text);
            _crumb.FontSize = T.FsNano;
            left.Children.Add(_crumb);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.Children.Add(left);
            if (_crumb != null) _crumb.TextTrimming = TextTrimming.CharacterEllipsis;

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(btns, 1);

            // Rev120 HYPERION：遥测灵动胶囊（将脉搏波形 + 实时指标统一收编于凹陷玻璃岛内）
            Border island = new Border();
            island.Height = 26;
            island.CornerRadius = new CornerRadius(13);
            island.Background = T.S(0xD8070D19);
            island.BorderBrush = T.S(T.Line);
            island.BorderThickness = new Thickness(1);
            island.Padding = new Thickness(10, 0, 12, 0);
            island.Margin = new Thickness(0, 0, 10, 0);
            island.VerticalAlignment = VerticalAlignment.Center;
            island.SnapsToDevicePixels = true;
            StackPanel islandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _pulse = new Sparkline(120);
            _pulse.Width = 112;
            _pulse.Height = 16;
            _pulse.VerticalAlignment = VerticalAlignment.Center;
            islandRow.Children.Add(_pulse);

            _telemetrySep = new Border();
            _telemetrySep.Width = 1;
            _telemetrySep.Height = 12;
            _telemetrySep.Background = T.S(T.LineMid);
            _telemetrySep.Margin = new Thickness(10, 0, 10, 0);
            _telemetrySep.VerticalAlignment = VerticalAlignment.Center;
            islandRow.Children.Add(_telemetrySep);

            _ticker = Ui.Mono("", T.FsNano, T.Dim);
            _ticker.VerticalAlignment = VerticalAlignment.Center;
            islandRow.Children.Add(_ticker);

            island.Child = islandRow;
            _telemetryIsland = island;
            btns.Children.Add(island);

            btns.Children.Add(BuildPowerChip());
            btns.Children.Add(BuildGameModeButton());

            Grid pal = BuildPaletteButton();
            btns.Children.Add(pal);

            btns.Children.Add(WinBtn("min"));
            btns.Children.Add(WinBtn("max"));
            btns.Children.Add(WinBtn("close"));
            bar.Children.Add(btns);

            return bar;
        }

        /// <summary>标题栏紧凑供电态势芯片（AC / 电池 / 节能）。</summary>
        private Border BuildPowerChip()
        {
            _pwrKnown = false;
            Border chip = new Border();
            chip.Height = 24;
            chip.CornerRadius = new CornerRadius(12);
            chip.BorderThickness = new Thickness(1);
            chip.Padding = new Thickness(8, 0, 9, 0);
            chip.Margin = new Thickness(0, 0, 8, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            chip.SnapsToDevicePixels = true;
            ChromeHit(chip);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            row.IsHitTestVisible = false;

            _pwrDot = Ui.Dot(T.Ok, 6);
            _pwrDot.Margin = new Thickness(0, 0, 5, 0);
            _pwrDot.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_pwrDot);

            _pwrText = Ui.Mono("AC", T.FsNano, T.Dim);
            _pwrText.FontWeight = FontWeights.SemiBold;
            _pwrText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_pwrText);

            chip.Child = row;
            _pwrChip = chip;
            RefreshPowerChip();
            return chip;
        }

        /// <summary>标题栏右侧一键游戏模式胶囊按钮。</summary>
        private Border BuildGameModeButton()
        {
            _gmHover = false;
            Border pill = new Border();
            pill.Height = 24;
            pill.CornerRadius = new CornerRadius(12);
            pill.BorderThickness = new Thickness(1);
            pill.Padding = new Thickness(9, 0, 10, 0);
            pill.Margin = new Thickness(0, 0, 8, 0);
            pill.VerticalAlignment = VerticalAlignment.Center;
            pill.Cursor = Cursors.Hand;
            pill.SnapsToDevicePixels = true;
            ChromeHit(pill);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            row.IsHitTestVisible = false;

            _gmIcon = Ui.Icon(Glyph.Bolt, 11, T.Dim, 1.8);
            _gmIcon.Margin = new Thickness(0, 0, 5, 0);
            _gmIcon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_gmIcon);

            _gmText = Ui.Tx("游戏模式", T.FsMicro, T.Dim, T.FCn);
            _gmText.FontWeight = FontWeights.SemiBold;
            _gmText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_gmText);

            _gmDot = Ui.Dot(T.Mute, 5);
            _gmDot.Margin = new Thickness(6, 0, 0, 0);
            _gmDot.VerticalAlignment = VerticalAlignment.Center;
            _gmDot.Visibility = Visibility.Collapsed;
            row.Children.Add(_gmDot);

            pill.Child = row;
            _gmPill = pill;

            pill.MouseEnter += delegate { _gmHover = true; RefreshChromeGameMode(); };
            pill.MouseLeave += delegate { _gmHover = false; RefreshChromeGameMode(); };
            Pressable.On(pill,
                delegate { ToggleGameMode(); },
                delegate(bool down)
                {
                    if (_gmPill != null) _gmPill.Opacity = down ? 0.78 : 1.0;
                });
            Tip.Attach(pill, "一键游戏模式 · 启用/退出游戏模式 + 关 DVR + 0.5ms 定时");

            RefreshChromeGameMode();
            return pill;
        }

        /// <summary>同步标题栏游戏模式胶囊的文案、光点与强调边。</summary>
        public void RefreshChromeGameMode()
        {
            if (_gmPill == null || _gmText == null || _gmDot == null || _gmIcon == null) return;
            if (_gameMode)
            {
                _gmPill.Background = T.S(_gmHover ? T.AccentA(0x36) : T.AccentA(0x22));
                _gmPill.BorderBrush = T.S(_gmHover ? T.Hi : T.Accent);
                _gmIcon.Stroke = T.S(T.Accent);
                _gmDot.Background = T.S(T.Ok);
                _gmDot.Visibility = Visibility.Visible;
                _gmText.Text = "游戏模式 ON";
                _gmText.Foreground = T.S(T.Hi);
            }
            else
            {
                _gmPill.Background = T.S(_gmHover ? 0x16FFFFFF : T.LineSoft);
                _gmPill.BorderBrush = T.S(_gmHover ? T.AccentA(0x5A) : T.LineMid);
                _gmIcon.Stroke = T.S(_gmHover ? T.Text : T.Dim);
                _gmDot.Background = T.S(T.Mute);
                _gmDot.Visibility = Visibility.Collapsed;
                _gmText.Text = "游戏模式";
                _gmText.Foreground = T.S(_gmHover ? T.Hi : T.Dim);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct CHROME_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out CHROME_POWER_STATUS st);

        /// <summary>刷新标题栏供电状态芯片（AC / 电池 / 节能）。</summary>
        public void RefreshPowerChip()
        {
            if (_pwrChip == null || _pwrText == null || _pwrDot == null) return;

            bool onAc = true;
            bool saver = false;
            bool batt = false;
            int pct = -1;

            Go.Daemon.DaemonHost d = Daemon;
            if (d != null && d.Power != null)
            {
                onAc = d.Power.OnAc;
                saver = d.Power.PowerSaverActive;
                batt = d.Power.HasBattery;
                pct = d.Power.BatteryPercent;
            }
            else
            {
                try
                {
                    CHROME_POWER_STATUS st;
                    if (GetSystemPowerStatus(out st))
                    {
                        batt = st.BatteryFlag != 128 && st.BatteryFlag != 255;
                        pct = st.BatteryLifePercent == 255 ? -1 : st.BatteryLifePercent;
                        onAc = st.ACLineStatus != 0;
                        saver = (st.SystemStatusFlag & 1) != 0;
                    }
                }
                catch (Exception ex) { Diag.Fail("chrome.power", ex); }
            }

            if (_pwrKnown && onAc == _pwrAc && saver == _pwrSaver && batt == _pwrBatt && pct == _pwrPct)
                return;

            _pwrKnown = true;
            _pwrAc = onAc;
            _pwrSaver = saver;
            _pwrBatt = batt;
            _pwrPct = pct;

            string label = saver ? "节能" : (onAc ? "AC" : "电池");
            bool warn = saver || !onAc;
            uint warnRim = ((uint)0x66 << 24) | (T.Warn & 0x00FFFFFFu);

            _pwrText.Text = label;
            _pwrText.Foreground = T.S(warn ? T.Warn : T.Dim);
            _pwrDot.Background = T.S(warn ? T.Warn : T.Ok);
            _pwrChip.Background = T.S(warn ? T.WarnWash : T.LineSoft);
            _pwrChip.BorderBrush = T.S(warn ? warnRim : T.LineMid);
            _pwrChip.ToolTip = saver
                ? "Windows 节能模式已开启 · 可能限制系统调度性能"
                : (!onAc
                    ? ("电池供电" + (batt && pct >= 0 ? (" · 电量 " + pct + "%") : ""))
                    : ("外接电源 AC" + (batt && pct >= 0 ? (" · 电量 " + pct + "%") : "")));
        }

        private Grid BuildPaletteButton()
        {
            Grid pal = new Grid();
            pal.Height = 24;
            pal.Margin = new Thickness(0, 0, 10, 0);
            pal.Cursor = Cursors.Hand;
            pal.Background = Brushes.Transparent;
            ChromeHit(pal);

            Border palBg = new Border();
            palBg.CornerRadius = new CornerRadius(T.RadiusSm);
            palBg.BorderThickness = new Thickness(1);
            palBg.BorderBrush = T.S(T.LineMid);
            palBg.Background = T.S(T.LineSoft);
            palBg.IsHitTestVisible = false;
            pal.Children.Add(palBg);

            StackPanel palC = new StackPanel();
            palC.Orientation = Orientation.Horizontal;
            palC.VerticalAlignment = VerticalAlignment.Center;
            palC.Margin = new Thickness(8, 0, 7, 0);
            palC.IsHitTestVisible = false;

            System.Windows.Shapes.Path mag = Ui.Icon(Glyph.Search, 11, T.Dim, 1.3);
            mag.IsHitTestVisible = false;
            palC.Children.Add(mag);
            TextBlock palT = Ui.Mono("Ctrl K", T.FsNano, T.Mute);
            palT.Margin = new Thickness(6, 0, 0, 0);
            palT.IsHitTestVisible = false;
            palC.Children.Add(palT);
            pal.Children.Add(palC);

            bool over = false;
            Action refresh = delegate
            {
                palBg.BorderBrush = T.S(over ? T.AccentA(0x5A) : T.LineMid);
                palBg.Background = T.S(over ? 0x16FFFFFF : T.LineSoft);
            };
            Pressable.On(pal, delegate { ShowPalette(); },
                delegate (bool down)
                {
                    if (down) { palBg.Background = T.S(T.AccentA(0x24)); palBg.BorderBrush = T.S(T.AccentRing); return; }
                    refresh();
                }, true);
            pal.MouseEnter += delegate { over = true; refresh(); };
            pal.MouseLeave += delegate { over = false; refresh(); };
            return pal;
        }

        private Grid HistBtn(string geo, bool mirrored, EventHandler onClick)
        {
            Grid g = new Grid();
            g.Width = 26; g.Height = 26;
            g.Margin = new Thickness(0, 0, 2, 0);
            g.Background = Brushes.Transparent;
            g.Cursor = Cursors.Hand;
            ChromeHit(g);

            System.Windows.Shapes.Path p = Ui.Icon(geo, 15, T.Dim, 1.4);
            p.RenderTransform = new RotateTransform(mirrored ? 180 : 0);
            p.RenderTransformOrigin = new Point(0.5, 0.5);
            p.IsHitTestVisible = false;
            g.Children.Add(p);

            Border face = new Border();
            face.CornerRadius = new CornerRadius(T.RadiusSm);
            face.Background = Brushes.Transparent;
            face.IsHitTestVisible = false;
            g.Children.Add(face);

            bool over = false;
            Pressable.On(g, delegate { onClick(g, EventArgs.Empty); },
                delegate (bool down)
                {
                    face.Background = T.S(down ? T.LineMid : (over ? 0x0FFFFFFFu : 0x00000000u));
                    p.Stroke = T.S(down ? T.Accent : (over ? T.Text : T.Dim));
                }, true);
            g.MouseEnter += delegate { over = true; face.Background = T.S(0x0FFFFFFF); p.Stroke = T.S(T.Text); };
            g.MouseLeave += delegate
            {
                over = false;
                if (g.IsMouseCaptured) return;
                face.Background = Brushes.Transparent;
                p.Stroke = T.S(T.Dim);
            };
            g.Opacity = 0.35;
            return g;
        }

        private UIElement WinBtn(string kind)
        {
            Grid g = new Grid();
            g.Width = 44; g.Height = T.TitleH;
            g.Background = Brushes.Transparent;
            ChromeHit(g);

            string geo = "M6 12 H14";
            if (kind == "max") geo = WindowState == WindowState.Maximized
                ? "M7 5 H14.5 V12.5 M5.5 7 H13 V14.5 H5.5 Z"
                : "M5.5 5.5 H14.5 V14.5 H5.5 Z";
            if (kind == "close") geo = "M5.5 5.5 L14.5 14.5 M14.5 5.5 L5.5 14.5";
            System.Windows.Shapes.Path p = Ui.Icon(geo, 20, 0x8AA6B4C4, 1.1);
            p.IsHitTestVisible = false;
            if (kind == "max") _maxBtnPath = p;
            g.Children.Add(p);

            bool over = false;
            Action<bool, bool> paint = delegate (bool down, bool hovered)
            {
                if (kind == "close")
                {
                    g.Background = T.S(down ? T.CloseDown : (hovered ? T.CloseHot : 0x00000000));
                    p.Stroke = T.S(hovered || down ? 0xFFFFFFFF : 0x8AA6B4C4);
                }
                else
                {
                    g.Background = T.S(down ? 0x2EFFFFFF : (hovered ? T.Line : 0x00000000));
                    p.Stroke = T.S(hovered || down ? 0xFFFFFFFF : 0x8AA6B4C4);
                }
            };

            Pressable.On(g, delegate
            {
                if (kind == "min") WindowState = WindowState.Minimized;
                else if (kind == "max") ToggleMax();
                else Close();
            },
            delegate (bool down) { paint(down, over); }, true);

            g.MouseEnter += delegate { over = true; paint(g.IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Pressed, true); };
            g.MouseLeave += delegate
            {
                over = false;
                if (g.IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Pressed) return;
                paint(false, false);
            };
            return g;
        }

        private void ToggleMax()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        /// <summary>Rev130 修复：无边框最大化时补偿系统 7px 溢出边框，并同步切换最大化/还原图标。</summary>
        private void SyncMaximizeChrome()
        {
            bool max = WindowState == WindowState.Maximized;
            if (_root != null)
            {
                Thickness want = max ? new Thickness(7) : new Thickness(0);
                if (_root.Margin != want) _root.Margin = want;
            }
            if (_maxBtnPath != null)
            {
                string geo = max
                    ? "M7 5 H14.5 V12.5 M5.5 7 H13 V14.5 H5.5 Z"
                    : "M5.5 5.5 H14.5 V14.5 H5.5 Z";
                _maxBtnPath.Data = Ui.IconGeometry(geo, 20);
            }
        }
    }
}
