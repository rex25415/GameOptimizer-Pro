// ============================================================================
//  Overview.Power.cs — 态势总览 · 页头与电源计划（Overview 分片）
//  来源：Overview.cs（890 行单体）按职责拆分，partial 共享字段，行为零变化。
//  电源计划真实读取 powrprof、真实切换；页头含推荐方案入口。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Go.Daemon;

namespace Go.Pages
{
    public partial class Overview : Page
    {
        // ---- 页头 -----------------------------------------------------------
        private UIElement BuildHeader(Core c)
        {
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Bottom;

            RefreshFallbackPowerIfNeeded();
            right.Children.Add(BuildPowerBadge());

            // 电源计划选择器。原来这里是个纯装饰：文案写死"平衡 · BALANCED"、
            // 带一个下拉箭头却没有点击处理，用户以为有多个方案可选，永远只看到"平衡"。
            // 现在真的读系统、真的能切。
            Border prof = new Border();
            prof.Height = 30;
            prof.CornerRadius = new CornerRadius(T.RadiusSm);
            prof.Background = T.S(0xFF0F151D);
            prof.BorderBrush = T.S(T.LineMid);
            prof.BorderThickness = new Thickness(1);
            prof.Padding = new Thickness(11, 0, 9, 0);
            prof.Margin = new Thickness(0, 0, 8, 0);
            prof.Cursor = Cursors.Hand;
            _profBox = prof;

            StackPanel psp = new StackPanel();
            psp.Orientation = Orientation.Horizontal;
            psp.VerticalAlignment = VerticalAlignment.Center;
            psp.IsHitTestVisible = false;
            Border pdot = Ui.Dot(T.Accent, 6);
            pdot.Margin = new Thickness(0, 0, 8, 0);
            pdot.VerticalAlignment = VerticalAlignment.Center;
            psp.Children.Add(pdot);
            // OEM 机型的电源计划名会长成「8945HX电源计划」这种，直接拼上去会把
            // 箭头挤到文字上。定宽 + 省略号，标签永远收在自己的地盘里。
            _profValue = Ui.Tx(ProfileLabel(c), T.FsSmall, T.Hi, T.FCn);
            _profValue.MaxWidth = 170;
            _profValue.TextTrimming = TextTrimming.CharacterEllipsis;
            psp.Children.Add(_profValue);
            Path chev = Ui.Icon(Glyph.Chevron, 12, T.Dim, 1.5);
            chev.Margin = new Thickness(8, 0, 0, 0);
            chev.RenderTransformOrigin = new Point(0.5, 0.5);
            chev.RenderTransform = new RotateTransform(90);
            psp.Children.Add(chev);
            prof.Child = psp;

            Action profPaint = delegate
            {
                bool hot = prof.IsMouseOver || (_profPopup != null && _profPopup.IsOpen);
                prof.BorderBrush = T.S(hot ? T.AccentA(0x5A) : T.LineMid);
                prof.Background = T.S(hot ? 0xFF121A24 : 0xFF0F151D);
            };
            prof.MouseEnter += delegate { profPaint(); };
            prof.MouseLeave += delegate { if (!prof.IsMouseCaptured) profPaint(); };
            Pressable.On(prof, delegate { TogglePowerMenu(c); },
                delegate (bool down)
                {
                    if (down) { prof.BorderBrush = T.S(T.AccentRing); prof.Background = T.S(T.AccentA(0x1A)); }
                    else profPaint();
                });
            Tip.Attach(prof, "电源计划 · 点击切换（平衡 / 高性能 / 节能 / 卓越性能）");

            right.Children.Add(prof);

            Btn apply = new Btn("应用推荐方案", BtnKind.Primary);
            apply.Height = T.BtnHLg;
            apply.SetIcon(Glyph.Bolt, 13);
            apply.Click += delegate { ApplyRecommended(c); };
            right.Children.Add(apply);

            return Ui.PageHeader("SITUATION / 01", "态势总览", "系统遥测实时采样 · 1Hz 主循环 · 5Hz 抖动轨迹", right);
        }

        // ---- 电源计划选择器 -------------------------------------------------
        private void TogglePowerMenu(Core c)
        {
            if (_profPopup != null && _profPopup.IsOpen) { _profPopup.IsOpen = false; return; }

            Guid active = Power.Active();
            List<Guid> schemes = Power.Schemes();
            if (schemes.Count == 0)
            {
                c.Shell.Toast("读不到电源计划列表（powrprof 返回空）", false);
                return;
            }

            StackPanel rows = new StackPanel();

            Popup pop = new Popup();
            pop.PlacementTarget = _profBox;
            pop.Placement = PlacementMode.Custom;
            pop.StaysOpen = false;
            pop.AllowsTransparency = true;
            pop.PopupAnimation = PopupAnimation.None;
            pop.CustomPopupPlacementCallback = delegate (Size popupSize, Size targetSize, Point offset)
            {
                return new CustomPopupPlacement[]
                {
                    new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 6), PopupPrimaryAxis.Horizontal)
                };
            };

            for (int i = 0; i < schemes.Count; i++)
                rows.Children.Add(PowerRow(c, schemes[i], schemes[i] == active, pop));

            Border card = new Border();
            card.Width = 272;
            card.CornerRadius = new CornerRadius(T.RadiusLg);
            card.BorderThickness = new Thickness(1);
            card.BorderBrush = T.S(T.AccentA(0x4A));
            card.Background = T.Grad(0xFF101720, 0xFF080B10, 90);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(7);

            Grid head = new Grid();
            head.Margin = new Thickness(9, 5, 9, 6);
            TextBlock h1 = Ui.Label("电源计划", T.Mute);
            h1.FontSize = T.FsNano;
            head.Children.Add(h1);
            TextBlock h2 = Ui.Mono("POWER PLAN", T.FsNano, T.Mute);
            h2.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(h2);
            body.Children.Add(head);
            body.Children.Add(Ui.Rule(T.Line, 0));
            body.Children.Add(new Border { Height = 5 });
            body.Children.Add(rows);
            Border fr = Ui.Rule(T.LineSoft, 0);
            fr.Margin = new Thickness(6, 5, 6, 0);
            body.Children.Add(fr);
            // 用户主动通过总览下拉选择的电源计划应持久化保留；只有自动巡航/临时提档才会写恢复点。
            TextBlock foot = Ui.Mono("切换立即生效 · 用户手动选定方案退出后保留", T.FsNano, T.Mute);
            foot.Margin = new Thickness(10, 6, 9, 3);
            body.Children.Add(foot);

            card.Child = body;
            pop.Child = card;
            _profPopup = pop;
            pop.IsOpen = true;
        }

        private Border PowerRow(Core c, Guid g, bool on, Popup pop)
        {
            Border row = new Border();
            row.CornerRadius = new CornerRadius(T.RadiusMd);
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
            row.Margin = new Thickness(2, 1, 2, 1);

            Grid gr = new Grid();
            gr.Background = Brushes.Transparent;
            gr.ColumnDefinitions.Add(new ColumnDefinition());
            gr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border hi = new Border();
            hi.CornerRadius = new CornerRadius(T.RadiusMd);
            hi.Background = T.S(T.AccentWash);
            hi.Opacity = on ? 0.55 : 0;
            hi.IsHitTestVisible = false;
            gr.Children.Add(hi);

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(10, 6, 6, 6);
            sp.IsHitTestVisible = false;
            TextBlock name = Ui.Tx(Power.FriendlyName(g) ?? "自定义方案", T.FsBody, on ? T.Hi : T.Text, T.FCn);
            sp.Children.Add(name);
            string en = Power.KnownEn(g);
            TextBlock hint = Ui.Mono(en == null ? ShortGuid(g) : en, T.FsNano, on ? T.AccentDim : T.Mute);
            hint.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(hint);
            gr.Children.Add(sp);

            if (on)
            {
                Path tick = Ui.Icon(Glyph.Check, 13, T.Accent, 1.6);
                tick.VerticalAlignment = VerticalAlignment.Center;
                tick.Margin = new Thickness(0, 0, 10, 0);
                tick.IsHitTestVisible = false;
                Grid.SetColumn(tick, 1);
                gr.Children.Add(tick);
            }

            row.Child = gr;
            HoverFx hov = new HoverFx(hi, on ? 0.55 : 0, 0.85);
            row.MouseEnter += delegate { hov.Set(true); };
            row.MouseLeave += delegate { if (!row.IsMouseCaptured) hov.Set(false); };
            Pressable.On(row, delegate
            {
                pop.IsOpen = false;
                ApplyPower(c, g);
            },
            delegate (bool down) { if (down) hi.Opacity = 1; else hov.Set(row.IsMouseOver); });

            return row;
        }

        /// <summary>真正的切换动作。菜单行和自检走同一条路径，避免"测的不是用的"。</summary>
        private bool ApplyPower(Core c, Guid g)
        {
            bool ok = Power.SetActive(g);
            if (!ok)
            {
                if (c != null && c.Shell != null)
                    c.Shell.Toast("切换电源计划失败 · 通常需要管理员权限（点左下角状态行提权重启）", false);
                return false;
            }
            SyncProfile();
            if (c != null)
            {
                if (c.Shell != null) c.Shell.Toast("已切换电源计划 · " + Power.Describe(g), true);
                c.State.Raise(Dirty.Power);
            }
            return true;
        }

        // ---- 自检钩子（不参与正常交互）--------------------------------------
        public int PowerSchemeCount { get { return Power.Schemes().Count; } }
        public string PowerLabel { get { return _profValue == null ? "" : _profValue.Text; } }
        public Guid PowerSchemeAt(int i)
        {
            List<Guid> l = Power.Schemes();
            return (i >= 0 && i < l.Count) ? l[i] : Guid.Empty;
        }
        public bool TestApplyPowerAt(int i)
        {
            List<Guid> l = Power.Schemes();
            if (i < 0 || i >= l.Count) return false;
            return ApplyPower(C, l[i]);
        }

        private static string ShortGuid(Guid g)
        {
            string s = g.ToString("N");
            return s.Substring(0, 8).ToUpperInvariant();
        }

        /// <summary>
        /// 读系统电源计划 → 写唯一真源（Core.Profile / ProfileEn → Dirty.Profile）。
        /// 界面标签不在这里写：它只认总线，于是"系统里改了方案"和"本页点了切换"
        /// 走的是同一条刷新路径，不会出现两份文案逻辑。
        /// </summary>
        private void SyncProfile()
        {
            if (C == null) return;
            Power.SyncInto(C);
            ApplyProfileLabel();   // 构建 / 进入页面时立即补一帧，不等总线派发
        }

        /// <summary>页头那个 PROFILE 标签的唯一写入点（总线订阅与构建期共用）。</summary>
        private void ApplyProfileLabel()
        {
            if (_profValue == null || C == null) return;
            string want = ProfileLabel(C);
            if (_profValue.Text != want) _profValue.Text = want;
        }

        private static string ProfileLabel(Core c)
        {
            string name = c == null || string.IsNullOrEmpty(c.Profile) ? "—" : c.Profile.Trim();
            if (name.Length > 16) name = name.Substring(0, 15) + "\u2026";
            string en = c == null || string.IsNullOrEmpty(c.ProfileEn) ? "" : c.ProfileEn.Trim();
            if (en.Length > 12) en = en.Substring(0, 11) + "\u2026";
            return en.Length == 0 ? name : name + " · " + en;
        }

        private UIElement BuildPowerBadge()
        {
            StackPanel dl = Ui.DotLabel(T.Mute, "供电 —", T.Dim);
            _pwrBadgeDot = dl.Children.Count > 0 ? dl.Children[0] as Border : null;
            _pwrBadgeText = dl.Children.Count > 1 ? dl.Children[1] as TextBlock : null;

            Border badge = new Border();
            badge.Height = 30;
            badge.CornerRadius = new CornerRadius(T.RadiusSm);
            badge.Background = T.S(0xFF0F151D);
            badge.BorderBrush = T.S(T.LineMid);
            badge.BorderThickness = new Thickness(1);
            badge.Padding = new Thickness(10, 0, 10, 0);
            badge.Margin = new Thickness(0, 0, 8, 0);
            badge.Child = dl;
            _pwrBadge = badge;
            return badge;
        }

        private void RefreshFallbackPowerIfNeeded()
        {
            if (C != null && C.Daemon != null && C.Daemon.Power != null) return;
            using (PowerMonitor pm = new PowerMonitor())
            {
                _fbPowerOk = pm.PollOnce();
                if (_fbPowerOk)
                {
                    _fbOnAc = pm.OnAc;
                    _fbHasBat = pm.HasBattery;
                    _fbBatPct = pm.BatteryPercent;
                    _fbSaver = pm.PowerSaverActive;
                }
            }
        }

        private void ReadPowerFlags(Core c, out bool onAc, out bool hasBat, out int pct, out bool saver)
        {
            PowerMonitor pm = (c != null && c.Daemon != null) ? c.Daemon.Power : null;
            if (pm != null)
            {
                onAc = pm.OnAc;
                hasBat = pm.HasBattery;
                pct = pm.BatteryPercent;
                saver = pm.PowerSaverActive;
                return;
            }
            if (_fbPowerOk)
            {
                onAc = _fbOnAc;
                hasBat = _fbHasBat;
                pct = _fbBatPct;
                saver = _fbSaver;
                return;
            }
            onAc = true;
            hasBat = false;
            pct = -1;
            saver = false;
        }

        private void RenderPowerBadge()
        {
            if (_pwrBadge == null || _pwrBadgeText == null) return;
            bool onAc, hasBat, saver;
            int pct;
            ReadPowerFlags(C, out onAc, out hasBat, out pct, out saver);
            string key = (onAc ? "1" : "0") + "|" + (hasBat ? "1" : "0") + "|" + pct + "|" + (saver ? "1" : "0");
            if (key == _pwrBadgeKey) return;
            _pwrBadgeKey = key;

            string text;
            uint dotCol;
            uint textCol;
            uint borderCol;
            if (saver)
            {
                text = (hasBat && !onAc && pct >= 0) ? ("电池 " + pct + "% · 节能模式") : "节能模式";
                dotCol = T.Warn;
                textCol = T.Warn;
                borderCol = 0x44FFB13C;
            }
            else if (hasBat && !onAc)
            {
                text = pct >= 0 ? ("电池 " + pct + "%") : "电池";
                dotCol = T.Warn;
                textCol = T.Warn;
                borderCol = 0x44FFB13C;
            }
            else
            {
                text = (hasBat && pct >= 0 && pct < 100) ? ("AC " + pct + "%") : "AC 供电";
                dotCol = T.Ok;
                textCol = T.Hi;
                borderCol = T.LineMid;
            }
            if (_pwrBadgeDot != null) _pwrBadgeDot.Background = T.S(dotCol);
            _pwrBadgeText.Text = text;
            _pwrBadgeText.Foreground = T.S(textCol);
            _pwrBadge.BorderBrush = T.S(borderCol);

            string detail = saver
                ? "Windows 节能模式已激活（建议切换至 AC + 关闭节能以保障性能）"
                : (!onAc ? "当前处于电池供电，部分 CPU/GPU 睿频策略会收紧" : "当前处于交流供电（AC）");
            Tip.Attach(_pwrBadge, detail + (C != null && !string.IsNullOrEmpty(C.Profile) ? ("\r\n电源计划：" + C.Profile) : ""));
        }
    }
}