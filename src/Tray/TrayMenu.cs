// ============================================================================
//  TrayMenu.cs — 托盘菜单（自绘，但交互语义必须是 Windows 原生那套）
//
//  【Rev64 / Rev88 要点】
//  1. 触发时刻 = 左键抬起且仍在行内；拖出即取消（Pressable 内核）。
//  2. 右键 / 中键永不触发菜单项；落在菜单里只吞掉，不执行、不关闭。
//  3. 防误触：弹出后 130ms 内、以及"按下早于弹出时刻"的点击一律丢弃。
//  4. 键盘完整可用：↑↓ / Home / End / Enter / Esc，选中与悬停共用高亮。
//  5. 失焦即关（弹出 120ms 内的抖动不算）。6. 放不下时先收副标题、再收页脚。
//  7. 贴边翻转 + 多屏工作区 + 目标屏 DPI 拆至 TrayMenu.Layout.cs。
//  8. Rev88：顶部加入三态光点（Idle/Guard/Alert）与活动档案/定时器/电源态势摘要。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Go.Tray
{
    public sealed partial class TrayMenu : Window
    {
        // ---- 窗口常量 ------------------------------------------------------
        private const double WidthDip = 300;
        private const double Gutter = 8;          // 给投影留的边距
        private const double RowMinH = 30;
        private const double RowMinHTouch = 44;
        private const int AntiMisfireMs = 130;

        // ---- 行模型 --------------------------------------------------------
        private sealed class Row
        {
            public Border Box;
            public Border Hi;
            public Border Edge;
            public TextBlock Label;
            public TextBlock Hint;
            public Border Pill;
            public Ellipse Dot;
            public TranslateTransform DotShift;
            public Pressable Press;
            public HoverFx Hover;
            public Action Run;
            public Action<bool> Toggle;
            public bool IsCheck;
            public bool On;
            public bool Danger;
            public bool Enabled = true;
            // 缓存补间委托（首次使用时建）：避免每次悬浮/勾选都新建捕获 Row 的闭包。
            public Action<double> EdgeOp;
            public Action<double> DotX;
        }

        private readonly StackPanel _rows;
        private readonly TextBlock _status;
        private readonly Ellipse _statusDot;
        private readonly Ellipse _statusHalo;
        private readonly Border _postureStrip;
        private readonly TextBlock _postureProfile;
        private readonly TextBlock _postureTimer;
        private readonly TextBlock _posturePower;
        private readonly Border _frame;
        private readonly TextBlock _footer;
        private readonly Border _footerRule;
        private readonly List<Row> _items = new List<Row>();
        private readonly List<TextBlock> _hints = new List<TextBlock>();
        private readonly Action<double> _fadeApply;

        private double _scale = 1.0;
        private int _anchorX, _anchorY;
        private long _openedMs;
        private bool _closing;
        private int _sel = -1;
        private bool _compact;   // 高度不够时的紧凑模式

        public TrayMenu()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Focusable = true;
            ShowActivated = true;

            _fadeApply = delegate (double v) { _frame.Opacity = v; };

            bool lightTaskbar = IsLightTaskbar();

            _frame = new Border();
            _frame.Width = WidthDip;
            _frame.CornerRadius = new CornerRadius(T.RadiusLg);
            _frame.BorderThickness = new Thickness(1);
            _frame.BorderBrush = T.S(lightTaskbar ? 0x38000000 : T.AccentA(0x64));
            _frame.Background = T.Grad(0xFF162238, 0xFF090E1A, 90);
            _frame.Margin = new Thickness(Gutter);

            Grid shell = new Grid();

            Border bevel = new Border();
            bevel.Height = 1.5;
            bevel.VerticalAlignment = VerticalAlignment.Top;
            bevel.Margin = new Thickness(14, 0, 14, 0);
            bevel.Background = T.AccentBevel();
            bevel.IsHitTestVisible = false;
            shell.Children.Add(bevel);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(7);

            // ---- 头部 ----
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Margin = new Thickness(9, 6, 9, 7);

            StackPanel mark = new StackPanel();
            mark.Children.Add(Ui.Icon(Brand.MarkPath, 15, T.Accent, Brand.MarkStroke * 15.0 / 24.0));
            mark.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(mark);

            StackPanel words = new StackPanel();
            words.Margin = new Thickness(9, 0, 0, 0);
            words.VerticalAlignment = VerticalAlignment.Center;
            TextBlock t = Ui.Label(Brand.ProductName, T.Hi);
            t.FontSize = T.FsMicro;
            words.Children.Add(t);

            Grid st = new Grid();
            st.Margin = new Thickness(0, 3, 0, 0);
            st.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            st.ColumnDefinitions.Add(new ColumnDefinition());

            Grid beacon = new Grid();
            beacon.Width = 10;
            beacon.Height = 10;
            beacon.VerticalAlignment = VerticalAlignment.Center;
            beacon.Margin = new Thickness(0, 0, 5, 0);

            _statusHalo = new Ellipse();
            _statusHalo.Width = 10;
            _statusHalo.Height = 10;
            _statusHalo.Fill = T.S(Ui.Wash(TrayHost.ColorIdle, 0x36));
            beacon.Children.Add(_statusHalo);

            _statusDot = new Ellipse();
            _statusDot.Width = 5.5;
            _statusDot.Height = 5.5;
            _statusDot.Fill = T.S(TrayHost.ColorIdle);
            beacon.Children.Add(_statusDot);
            st.Children.Add(beacon);

            _status = Ui.Mono("守护待命", T.FsNano, T.Text);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_status, 1);
            st.Children.Add(_status);
            words.Children.Add(st);

            Grid.SetColumn(words, 1);
            head.Children.Add(words);
            body.Children.Add(head);

            // ---- 态势摘要条（活动档案 / 定时器 / 电源，按需展开）----
            _postureStrip = BuildPostureStrip(out _postureProfile, out _postureTimer, out _posturePower);
            _postureStrip.Visibility = Visibility.Collapsed;
            body.Children.Add(_postureStrip);

            body.Children.Add(Ui.Rule(T.Line, 0));
            body.Children.Add(new Border { Height = 5 });

            _rows = new StackPanel();
            body.Children.Add(_rows);

            // ---- 页脚（键盘提示）----
            _footerRule = Ui.Rule(T.LineSoft, 0);
            _footerRule.Margin = new Thickness(6, 5, 6, 0);
            _footerRule.Visibility = Visibility.Collapsed;
            body.Children.Add(_footerRule);

            _footer = Ui.Mono("↑↓ 选择   Enter 执行   Esc 关闭", T.FsNano, T.Mute);
            _footer.Margin = new Thickness(11, 6, 9, 2);
            _footer.Visibility = Visibility.Collapsed;
            body.Children.Add(_footer);

            shell.Children.Add(body);
            _frame.Child = shell;
            Content = _frame;

            Deactivated += delegate
            {
                // 刚弹出时的失焦抖动不算（外壳抢前台会抖一下）
                if (ClickClock.NowMs - _openedMs < 120) return;
                CloseMenu();
            };
            PreviewKeyDown += OnKey;
            MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            // 关闭时停呼吸动画：否则 RepeatBehavior.Forever 的动画时钟持有整棵已关菜单的视觉树。
            Closed += delegate { Motion.StopBreathe(_statusDot); };
        }

        private static Border BuildPostureStrip(out TextBlock prof, out TextBlock timer, out TextBlock power)
        {
            Border wrap = new Border();
            wrap.Margin = new Thickness(6, 0, 6, 7);
            wrap.Padding = new Thickness(8, 5, 8, 5);
            wrap.CornerRadius = new CornerRadius(T.RadiusMd);
            wrap.Background = T.S(T.Inset);
            wrap.BorderThickness = new Thickness(1);
            wrap.BorderBrush = T.S(T.LineSoft);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            prof = AddPostureCell(grid, 0, "档案", "—", T.Hi);
            AddPostureRule(grid, 1);
            timer = AddPostureCell(grid, 2, "定时器", "—", T.Accent);
            AddPostureRule(grid, 3);
            power = AddPostureCell(grid, 4, "电源", "—", T.Text);

            wrap.Child = grid;
            return wrap;
        }

        private static TextBlock AddPostureCell(Grid g, int col, string cap, string val, uint colArgb)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = col > 0 ? new Thickness(7, 0, 2, 0) : new Thickness(0, 0, 4, 0);
            TextBlock k = Ui.Tx(cap, 9.5, T.Mute, T.FCn);
            sp.Children.Add(k);
            TextBlock v = Ui.Mono(val, T.FsNano, colArgb);
            v.Margin = new Thickness(0, 1, 0, 0);
            v.TextTrimming = TextTrimming.CharacterEllipsis;
            v.TextWrapping = TextWrapping.NoWrap;
            sp.Children.Add(v);
            Grid.SetColumn(sp, col);
            g.Children.Add(sp);
            return v;
        }

        private static void AddPostureRule(Grid g, int col)
        {
            Border r = new Border();
            r.Width = 1;
            r.Margin = new Thickness(0, 2, 0, 2);
            r.Background = T.S(T.LineSoft);
            Grid.SetColumn(r, col);
            g.Children.Add(r);
        }

        // ====================================================================
        //  构建 API
        // ====================================================================
        public void SetStatus(string text, bool ok)
        {
            SetStatus(ok ? TrayVisualState.Guard : TrayVisualState.Idle, text);
        }

        /// <summary>按三态切换托盘菜单顶部信标光点与态别文案（Idle 冰蓝 / Guard 翠绿 / Alert 琥珀）。</summary>
        public void SetStatus(TrayVisualState state, string text)
        {
            uint col = TrayHost.ColorForState(state);
            _status.Text = text ?? "";
            _statusDot.Fill = T.S(col);
            _statusHalo.Fill = T.S(Ui.Wash(col, 0x38));
            _status.Foreground = T.S(state == TrayVisualState.Idle ? T.Text : col);

            if (state == TrayVisualState.Guard)
                Motion.Breathe(_statusDot, 0.48, 1.0, 2200);
            else if (state == TrayVisualState.Alert)
                Motion.Breathe(_statusDot, 0.35, 1.0, 1100);
            else
                Motion.StopBreathe(_statusDot);
        }

        /// <summary>更新菜单顶部活动档案 / 定时器 / 电源态势摘要条。</summary>
        public void SetPosture(string profile, string timerText, string powerText)
        {
            bool any = !string.IsNullOrEmpty(profile) || !string.IsNullOrEmpty(timerText) || !string.IsNullOrEmpty(powerText);
            _postureStrip.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _postureProfile.Text = string.IsNullOrEmpty(profile) ? "—" : profile;
            _postureTimer.Text = string.IsNullOrEmpty(timerText) ? "—" : timerText;
            _posturePower.Text = string.IsNullOrEmpty(powerText) ? "—" : powerText;
        }

        public void AddHeader(string text)
        {
            TextBlock h = Ui.Label(text, T.Mute);
            h.FontSize = T.FsNano;
            h.Margin = new Thickness(11, 8, 9, 4);
            _rows.Children.Add(h);
        }

        public void AddRule()
        {
            Border b = Ui.Rule(T.LineSoft, 0);
            b.Margin = new Thickness(6, 5, 6, 4);
            _rows.Children.Add(b);
        }

        public void AddItem(string label, string hint, Action run, bool danger)
        {
            BuildRow(label, hint, null, run, danger, false, false);
        }

        public void AddCheck(string label, bool on, Action<bool> toggle)
        {
            AddCheck(label, null, on, toggle);
        }

        public void AddCheck(string label, string hint, bool on, Action<bool> toggle)
        {
            BuildRow(label, hint, null, null, false, true, on);
            Row r = _items[_items.Count - 1];
            r.Toggle = toggle;
        }

        private Row BuildRow(string label, string hint, string shortcut, Action run, bool danger, bool check, bool on)
        {
            Row r = new Row();
            r.Run = run;
            r.IsCheck = check;
            r.On = on;
            r.Danger = danger;

            Border box = new Border();
            box.CornerRadius = new CornerRadius(T.RadiusMd);
            box.Background = Brushes.Transparent;
            box.Cursor = Cursors.Hand;
            box.Margin = new Thickness(2, 1, 2, 1);
            // 所有行同一个最小高度：带副标题的行和不带的行不能在视觉上参差
            box.MinHeight = Touch.IsTouchPrimary ? RowMinHTouch : RowMinH;
            r.Box = box;

            Grid g = new Grid();
            g.Background = Brushes.Transparent;

            // 高亮层（只动 Opacity，带细内描边提升精致反馈）
            r.Hi = new Border();
            r.Hi.CornerRadius = new CornerRadius(T.RadiusMd);
            r.Hi.Background = T.S(danger ? T.BadWash : T.AccentWash);
            r.Hi.BorderThickness = new Thickness(1);
            r.Hi.BorderBrush = T.S(danger ? Ui.Wash(T.Bad, 0x44) : T.AccentA(0x36));
            r.Hi.Opacity = 0;
            r.Hi.IsHitTestVisible = false;
            g.Children.Add(r.Hi);

            // 键盘选中时的左侧指示条
            r.Edge = new Border();
            r.Edge.Width = 2;
            r.Edge.CornerRadius = new CornerRadius(1);
            r.Edge.Background = T.S(danger ? T.Bad : T.Accent);
            r.Edge.HorizontalAlignment = HorizontalAlignment.Left;
            r.Edge.VerticalAlignment = VerticalAlignment.Stretch;
            r.Edge.Margin = new Thickness(-2, 5, 0, 5);
            r.Edge.Opacity = 0;
            r.Edge.IsHitTestVisible = false;
            g.Children.Add(r.Edge);

            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Margin = new Thickness(10, 5, 6, 5);

            TextBlock t = Ui.Tx(label, T.FsBody, danger ? T.Bad : T.Text, T.FCn);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            r.Label = t;

            if (!string.IsNullOrEmpty(hint))
            {
                TextBlock h = Ui.Mono(hint, T.FsNano, T.Dim);
                h.Margin = new Thickness(0, 2, 0, 0);
                h.TextTrimming = TextTrimming.CharacterEllipsis;
                sp.Children.Add(h);
                r.Hint = h;
                _hints.Add(h);
            }
            g.Children.Add(sp);

            if (check)
            {
                Grid pillHost = new Grid();
                pillHost.Width = 30;
                pillHost.VerticalAlignment = VerticalAlignment.Center;
                pillHost.Margin = new Thickness(0, 0, 9, 0);

                r.Pill = new Border();
                r.Pill.Width = 24; r.Pill.Height = 14;
                r.Pill.CornerRadius = new CornerRadius(7);
                r.Pill.BorderThickness = new Thickness(1);
                r.Pill.HorizontalAlignment = HorizontalAlignment.Center;
                r.Pill.Background = on ? (Brush)T.AccentGrad() : T.S(0xFF0A111F);
                r.Pill.BorderBrush = T.S(on ? T.AccentA(0xCC) : 0x3ECFE6FF);
                pillHost.Children.Add(r.Pill);

                r.Dot = new Ellipse();
                r.Dot.Width = 8; r.Dot.Height = 8;
                r.Dot.Fill = T.S(on ? 0xFFFFFFFFu : 0xFF98AEC8u);
                r.Dot.HorizontalAlignment = HorizontalAlignment.Left;
                r.Dot.VerticalAlignment = VerticalAlignment.Center;
                r.Dot.Margin = new Thickness(5.5, 0, 0, 0);
                r.DotShift = new TranslateTransform(on ? 11 : 0, 0);
                r.Dot.RenderTransform = r.DotShift;
                pillHost.Children.Add(r.Dot);

                Grid.SetColumn(pillHost, 1);
                g.Children.Add(pillHost);
            }
            else if (!string.IsNullOrEmpty(shortcut))
            {
                TextBlock k = Ui.Mono(shortcut, T.FsNano, T.Mute);
                k.VerticalAlignment = VerticalAlignment.Center;
                k.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(k, 1);
                g.Children.Add(k);
            }

            box.Child = g;
            _rows.Children.Add(box);
            _items.Add(r);

            r.Hover = new HoverFx(r.Hi, 0, danger ? 0.9 : 0.85);
            r.Press = Pressable.On(box,
                delegate { Activate(r); },
                delegate (bool down)
                {
                    if (down)
                    {
                        r.Hover.Snap(true);
                        r.Hi.Background = T.S(r.Danger ? Ui.Wash(T.Bad, 0x2C) : T.AccentGlow);
                        sp.Margin = new Thickness(11, 5.5, 5, 4.5);
                    }
                    else
                    {
                        r.Hi.Background = T.S(r.Danger ? T.BadWash : T.AccentWash);
                        sp.Margin = new Thickness(10, 5, 6, 5);
                        if (!r.Box.IsMouseOver) r.Hover.Set(false);
                    }
                },
                true);

            box.MouseEnter += delegate
            {
                if (!r.Enabled) return;
                r.Hover.Set(true);
                r.Label.Foreground = T.S(r.Danger ? T.Bad : T.Hi);
                Highlight(r);
            };
            box.MouseLeave += delegate
            {
                if (r.Press.Pressed) return;
                r.Hover.Set(false);
                r.Label.Foreground = T.S(r.Danger ? T.Bad : T.Text);
                if (_sel >= 0 && _sel < _items.Count && ReferenceEquals(_items[_sel], r)) ShowSel(r, false);
            };

            return r;
        }

        // ====================================================================
        //  触发
        // ====================================================================
        private void Activate(Row r)
        {
            if (r == null || !r.Enabled) return;

            // 防误触：菜单刚弹出 / 按下发生在弹出之前 → 这次点击作废
            if (ClickClock.NowMs - _openedMs < AntiMisfireMs) return;
            if (r.Press.LastPressMs < _openedMs) return;

            if (r.IsCheck)
            {
                r.On = !r.On;
                ApplyCheck(r, true);
                Action<bool> tol = r.Toggle;
                CloseMenu();
                if (tol != null) { try { tol(r.On); } catch (Exception ex) { TrayHost.LogTrayError(ex); } }
                return;
            }

            Action run = r.Run;
            CloseMenu();
            if (run != null) { try { run(); } catch (Exception ex) { TrayHost.LogTrayError(ex); } }
        }

        private void ApplyCheck(Row r, bool animate)
        {
            if (r == null || r.Dot == null) return;
            double to = r.On ? 11 : 0;
            double from = r.DotShift.X;
            if (animate)
            {
                r.DotShift.BeginAnimation(TranslateTransform.XProperty, null);
                r.DotShift.X = from;
                if (r.DotX == null) { Row rr = r; r.DotX = delegate (double v) { rr.DotShift.X = v; }; }
                Motion.Tween(from, to, 150, r.DotX);
            }
            else r.DotShift.X = to;
            r.Pill.Background = r.On ? (Brush)T.AccentGrad() : T.S(0xFF0A111F);
            r.Pill.BorderBrush = T.S(r.On ? T.AccentA(0xCC) : 0x3ECFE6FF);
            r.Dot.Fill = T.S(r.On ? 0xFFFFFFFFu : 0xFF98AEC8u);
        }

        // ====================================================================
        //  键盘
        // ====================================================================
        private void OnKey(object s, KeyEventArgs e)
        {
            if (HandleKey(e.Key)) e.Handled = true;
        }

        private bool HandleKey(Key key)
        {
            if (_items.Count == 0) return false;
            bool used = true;
            switch (key)
            {
                case Key.Escape: CloseMenu(); break;
                case Key.Up: MoveSel(-1); break;
                case Key.Down: MoveSel(1); break;
                case Key.Home: SetSel(FirstEnabled()); break;
                case Key.End: SetSel(LastEnabled()); break;
                case Key.PageUp: MoveSel(-5); break;
                case Key.PageDown: MoveSel(5); break;
                case Key.Enter:
                case Key.Space:
                    ActivateFromKeyboard();
                    break;
                default: used = false; break;
            }
            return used;
        }

        private int FirstEnabled()
        {
            for (int i = 0; i < _items.Count; i++) if (_items[i].Enabled) return i;
            return -1;
        }

        private int LastEnabled()
        {
            for (int i = _items.Count - 1; i >= 0; i--) if (_items[i].Enabled) return i;
            return -1;
        }

        private void MoveSel(int d)
        {
            if (_items.Count == 0) return;
            int i = _sel;
            for (int step = 0; step < _items.Count; step++)
            {
                i = i + d;
                if (i < 0) i = _items.Count - 1;
                if (i >= _items.Count) i = 0;
                if (_items[i].Enabled) { SetSel(i); return; }
            }
        }

        private void SetSel(int i)
        {
            if (_sel >= 0 && _sel < _items.Count) ShowSel(_items[_sel], false);
            _sel = i;
            if (i >= 0 && i < _items.Count) ShowSel(_items[i], true);
            if (_footer.Visibility != Visibility.Visible)
            {
                _footer.Visibility = Visibility.Visible;
                _footerRule.Visibility = Visibility.Visible;
            }
        }

        private void Highlight(Row r)
        {
            if (_sel >= 0 && _sel < _items.Count && ReferenceEquals(_items[_sel], r)) return;
            if (_sel >= 0 && _sel < _items.Count) ShowSel(_items[_sel], false);
            _sel = _items.IndexOf(r);
        }

        private void ShowSel(Row r, bool on)
        {
            if (r == null) return;
            if (r.EdgeOp == null) { Row rr = r; r.EdgeOp = delegate (double v) { rr.Edge.Opacity = v; }; }
            Motion.Tween(r.Edge.Opacity, on ? 0.9 : 0, 120, r.EdgeOp);
            r.Label.Foreground = T.S(r.Danger ? T.Bad : (on ? T.Hi : T.Text));
            if (on) r.Hover.Snap(true);
            else if (!r.Box.IsMouseOver) r.Hover.Set(false);
        }

        private void ActivateFromKeyboard()
        {
            if (_sel < 0) { SetSel(FirstEnabled()); return; }
            Row r = _items[_sel];
            if (!r.Enabled) return;
            if (r.IsCheck)
            {
                r.On = !r.On;
                ApplyCheck(r, true);
                Action<bool> tol = r.Toggle;
                CloseMenu();
                if (tol != null) { try { tol(r.On); } catch (Exception ex) { TrayHost.LogTrayError(ex); } }
                return;
            }
            Action run = r.Run;
            CloseMenu();
            if (run != null) { try { run(); } catch (Exception ex) { TrayHost.LogTrayError(ex); } }
        }

        public int RowCount { get { return _items.Count; } }
        public bool IsOpen { get { return !_closing && IsVisible; } }
        public int SelectedIndex { get { return _sel; } }

        /// <summary>自检钩子：等价于点中第 i 行（跳过防误触计时）。</summary>
        public bool TestActivate(int i)
        {
            if (i < 0 || i >= _items.Count) return false;
            Row r = _items[i];
            if (r.IsCheck)
            {
                r.On = !r.On;
                ApplyCheck(r, false);
                Action<bool> tol = r.Toggle;
                if (tol != null) tol(r.On);
                return true;
            }
            if (r.Run != null) r.Run();
            return true;
        }

        public void TestKey(Key k) { HandleKey(k); }

        /// <summary>自检钩子：拿到第 i 行的可命中元素（用来合成鼠标事件）。</summary>
        public UIElement TestRow(int i)
        {
            return (i >= 0 && i < _items.Count) ? (UIElement)_items[i].Box : null;
        }

        /// <summary>自检钩子：只看不做，返回第 i 行当前是否处于选中状态。</summary>
        public bool TestIsSelected(int i) { return _sel == i; }

        private void CloseMenu()
        {
            if (_closing) return;
            _closing = true;
            try { if (IsLoaded || IsVisible) Close(); } catch { /* 可忽略：已经关掉/还没显示时 Close 会抛；这里只是尽力关闭，没有后续依赖 */ }
        }
    }
}
