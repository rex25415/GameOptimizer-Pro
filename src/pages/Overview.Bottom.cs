// ============================================================================
//  Overview.Bottom.cs — 01 态势总览：核心拓扑 + 快速调优（Overview 分片）
//  来源：Overview.cs（862 行单体）按职责拆分，partial 共享字段，行为零变化。
//  底部双栏：左边只读（拓扑），右边可写（四个开关 + 账本行 + 调优入口）。
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Overview : Page
    {
        private UIElement BuildTopology(Core c)
        {
            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel hl = new StackPanel();
            TextBlock t = Ui.Tx("核心拓扑", T.FsTitle, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            hl.Children.Add(t);
            TextBlock s = Ui.Label("CCD LOCALITY \u00B7 " + c.Topo.CcdLabel + " \u00B7 " + c.Topo.Logical + " THREADS", T.Mute);
            s.Margin = new Thickness(0, 3, 0, 0);
            hl.Children.Add(s);
            head.Children.Add(hl);
            TextBlock badge = Ui.Mono(c.CpuName, T.FsNano, T.AccentDim);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.TextTrimming = TextTrimming.CharacterEllipsis;
            badge.MaxWidth = 220;
            Grid.SetColumn(badge, 1);
            head.Children.Add(badge);
            Grid inner = new Grid();
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            inner.Children.Add(head);

            // 核心矩阵拿 * 行：它本来就能按实际高度铺开，以前钉死 96 的结果是
            // 面板剩下一半高度没东西放。现在矩阵自己吃掉这块空间，格子随之变高变清楚。
            _coreGrid = new CoreGrid();
            _coreGrid.MinHeight = 96;
            _coreGrid.VerticalAlignment = VerticalAlignment.Center;
            Topology topo = c.Topo;
            _coreGrid.Ccds = topo.Ccds;
            _coreGrid.CoresPerCcd = topo.CoresPerCcd;
            _coreGrid.ThreadsPerCore = topo.ThreadsPerCore;
            Grid.SetRow(_coreGrid, 2);
            inner.Children.Add(_coreGrid);

            Border rule = new Border();
            rule.Child = Ui.Rule(T.LineSoft, 0);
            Grid.SetRow(rule, 4);
            inner.Children.Add(rule);

            Grid legend = new Grid();
            legend.ColumnDefinitions.Add(new ColumnDefinition());
            legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock l1 = Ui.Mono(c.Topo.Cores + " 物理核心 / " + c.Topo.Logical + " 逻辑处理器", T.FsNano, T.Mute);
            legend.Children.Add(l1);
            TextBlock l2 = Ui.Mono("跨 CCD 延迟敏感", T.FsNano, T.AccentDim);
            l2.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(l2, 1);
            legend.Children.Add(l2);
            Grid.SetRow(legend, 6);
            inner.Children.Add(legend);

            return Ui.Panel(inner, 18);
        }

        private UIElement BuildQuick(Core c)
        {
            StackPanel body = new StackPanel();

            TextBlock t = Ui.Tx("快速调优", T.FsTitle, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            body.Children.Add(t);
            TextBlock s = Ui.Label("ONE-TAP TUNING", T.Mute);
            s.Margin = new Thickness(0, 3, 0, 0);
            body.Children.Add(s);
            Border g0 = new Border(); g0.Height = 8; body.Children.Add(g0);

            _swGm = new Switch(C.Shell != null && C.Shell.GameModeOn);
            Action<bool> gmCommit = delegate (bool on)
            {
                if (C.Shell == null) return;
                if (on != C.Shell.GameModeOn) C.Shell.SetGameMode(on);
                if (_swGm.On != C.Shell.GameModeOn) _swGm.On = C.Shell.GameModeOn;
                SyncGameModeUi();
            };
            _swGm.Changed += delegate { gmCommit(_swGm.On); };
            body.Children.Add(BuildGameModeRow(gmCommit));

            _swTimer = new Switch(false);
            Action<bool> timerCommit = delegate (bool on)
            {
                C.Timer.Set(TimerOwner.Overview, on, 0.5);
                if (on) C.Shell.Toast(C.Timer.Active ? ("定时器精度已提升至 " + C.Timer.GrantedMs.ToString("0.0") + " ms") : "定时器请求失败", C.Timer.Active);
                else C.Shell.Toast(C.Timer.Active ? "本页已放弃，其它模块仍持有精度请求" : "已恢复系统默认定时器精度", true);
            };
            _swTimer.Changed += delegate { timerCommit(_swTimer.On); };
            body.Children.Add(ToggleRow("计时器精度 0.5ms", "NtSetTimerResolution · 本进程", _swTimer, timerCommit));

            Btn purgeBtn = new Btn("立即清理", BtnKind.Ghost);
            purgeBtn.Height = T.BtnHSm;
            purgeBtn.Compact = true;
            purgeBtn.Click += delegate
            {
                double beforeAvail = (C != null && C.Perf != null && C.Perf.Current != null) ? C.Perf.Current.MemAvailMB : 0;
                bool ok = Mem.PurgeStandby();
                if (ok)
                {
                    C.Shell.Toast("待机缓存列表已释放（系统将按需重建热缓存）", true);
                    _standbySubTenth = int.MinValue;
                }
                else
                {
                    C.Shell.Toast("操作被拒绝，需要管理员权限", false);
                }
                C.Engine.Ledger.AppendAudit("MEM purge-standby -> " + ok + (beforeAvail > 0 ? (" availBefore=" + (int)beforeAvail + "MB") : ""));
            };
            body.Children.Add(ActionRowBtn("待机列表清理", "清空 Standby List · 需管理员", purgeBtn, out _standbySub));

            Btn dnsBtn = new Btn("刷新 DNS", BtnKind.Ghost);
            dnsBtn.Height = T.BtnHSm;
            dnsBtn.Compact = true;
            dnsBtn.Click += delegate
            {
                bool ok = WinNative.FlushDnsCache();
                C.Shell.Toast(ok ? "系统 DNS 解析缓存已即刻刷新" : "刷新 DNS 缓存失败", ok);
                C.Engine.Ledger.AppendAudit("NET flush-dns -> " + ok);
            };
            TextBlock dnsSub;
            body.Children.Add(ActionRowBtn("刷新 DNS 解析缓存", "原生 dnsapi.dll · 清除过期解析", dnsBtn, out dnsSub));

            Switch swNet = new Switch(false);
            ActionDef nagle = C.Engine.Find("nagle");
            if (nagle != null) swNet.On = C.Engine.ProbeForUi(nagle) == AState.Applied;
            Action<bool> netCommit = delegate (bool on)
            {
                if (nagle == null) return;
                if (!C.Engine.CanWrite(nagle))
                {
                    C.Shell.Toast("需要管理员权限：点左下角状态行提权重启", false);
                    swNet.On = C.Engine.ProbeForUi(nagle) == AState.Applied;
                    return;
                }
                ApplyReport rep = on ? C.Engine.Apply(nagle) : C.Engine.RevertAction(nagle.Id);
                C.State.Post(Dirty.Ledger);   // 写前账本变了 → 本页账本读数立即跟上
                string errMsg = !string.IsNullOrEmpty(rep.Reason)
                    ? rep.Reason
                    : (on ? "写入失败，需要管理员权限" : "该项并非由本工具修改（账本无记录），未改动系统设置");
                C.Shell.Toast(rep.Ok ? ("Nagle：" + (on ? "已关闭攒批" : "已恢复默认")) : errMsg, rep.Ok);
                swNet.On = C.Engine.ProbeForUi(nagle) == AState.Applied;
            };
            swNet.Changed += delegate { netCommit(swNet.On); };
            body.Children.Add(ToggleRow("关闭 Nagle 攒批", "TCPNoDelay=1 · 对已建立连接需重连", swNet, netCommit));

            // 动作行之后用 * 撑开，把「账本状态 + 打开调优矩阵」同行钉在面板底部。
            Grid foot = new Grid();
            foot.VerticalAlignment = VerticalAlignment.Bottom;
            foot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            foot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            foot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border fr = new Border();
            fr.Child = Ui.Rule(T.LineSoft, 0);
            Grid.SetRow(fr, 1);
            foot.Children.Add(fr);

            Grid footBar = new Grid();
            Grid.SetRow(footBar, 3);
            footBar.ColumnDefinitions.Add(new ColumnDefinition());
            footBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid ledger = new Grid();
            ledger.VerticalAlignment = VerticalAlignment.Center;
            ledger.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ledger.ColumnDefinitions.Add(new ColumnDefinition());
            ledger.Children.Add(Ui.Icon(Glyph.Shield, 12, T.Ok, 1.3));
            TextBlock lt = Ui.Mono("还原账本 · 0 项待还原", T.FsNano, T.Mute);
            _ledgerLine = lt;
            lt.Margin = new Thickness(8, 0, 0, 0);
            lt.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lt, 1);
            ledger.Children.Add(lt);
            footBar.Children.Add(ledger);

            Btn more = new Btn("打开调优矩阵 \u2192", BtnKind.Accent);
            more.Compact = true;
            more.Height = T.BtnHSm;
            more.HorizontalAlignment = HorizontalAlignment.Right;
            more.Click += delegate { C.Shell.Go("tuning"); };
            Grid.SetColumn(more, 1);
            footBar.Children.Add(more);
            foot.Children.Add(footBar);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(body);

            Grid.SetRow(foot, 1);
            root.Children.Add(foot);

            return Ui.Panel(root, 18);
        }
        // ---- 核心矩阵：只在负载真的变了的时候重画 --------------------------
        // CoreGrid.Load 每次都会 Array.Copy + InvalidateVisual，而采样器给的是
        // "同一个数组、内容每拍刷新"，所以按内容比较：32 个 double 的比较比
        // 一次全矩阵重绘便宜两个数量级。旧代码还在 Load 之后又补了一次
        // InvalidateVisual —— Load 内部已经叫过了。
        private int _coreN = -1;

        private void PushCores(double[] cores)
        {
            if (_coreGrid == null || cores == null || cores.Length == 0) return;
            double[] seen = _coreGrid.Load;             // 内部缓冲（长 128），只读比较
            int n = Math.Min(cores.Length, seen.Length);
            if (n == _coreN)
            {
                bool same = true;
                for (int i = 0; i < n; i++)
                {
                    if (cores[i] != seen[i]) { same = false; break; }
                }
                if (same) return;
            }
            _coreN = n;
            _coreGrid.Load = cores;
        }


        private UIElement ToggleRow(string title, string sub, Switch sw, Action<bool> commit)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Margin = new Thickness(-6, 2, -6, 0);
            g.Background = Brushes.Transparent;
            g.Cursor = Cursors.Hand;

            g.MouseEnter += delegate { g.Background = T.S(T.LineFaint); };
            g.MouseLeave += delegate { g.Background = Brushes.Transparent; };
            Pressable.On(g, delegate
            {
                bool want = !sw.On;
                sw.On = want;
                if (commit != null) commit(want);
            },
            delegate (bool down) { g.Background = T.S(down ? 0x10FFFFFF : (g.IsMouseOver ? T.LineFaint : 0x00000000)); });

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(6, 2, 8, 2);
            sp.IsHitTestVisible = false;
            TextBlock t = Ui.Tx(title, T.FsBody, T.Text, T.FCn);
            sp.Children.Add(t);
            TextBlock s = Ui.Mono(sub, T.FsNano, T.Mute);
            s.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(s);
            g.Children.Add(sp);

            sw.VerticalAlignment = VerticalAlignment.Center;
            sw.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);
            return g;
        }

        private UIElement ActionRowBtn(string title, string sub, Btn btn, out TextBlock subTb)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Margin = new Thickness(-6, 2, -6, 0);
            g.Background = Brushes.Transparent;
            g.MouseEnter += delegate { g.Background = T.S(T.LineFaint); };
            g.MouseLeave += delegate { g.Background = Brushes.Transparent; };

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(6, 2, 8, 2);
            sp.IsHitTestVisible = false;
            sp.Children.Add(Ui.Tx(title, T.FsBody, T.Text, T.FCn));
            subTb = Ui.Mono(sub, T.FsNano, T.Mute);
            subTb.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(subTb);
            g.Children.Add(sp);

            btn.VerticalAlignment = VerticalAlignment.Center;
            btn.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);
            return g;
        }

        private void RenderStandbySub(Snapshot s)
        {
            if (_standbySub == null || s == null) return;
            int tenth = (int)Math.Round(s.MemStandbyMB / 102.4);
            if (tenth == _standbySubTenth) return;
            _standbySubTenth = tenth;
            _standbySub.Text = tenth > 0
                ? ("Standby " + (tenth / 10.0).ToString("0.0") + " GB 可回收 · 需管理员")
                : "清空 Standby List · 需管理员";
        }

        private UIElement BuildGameModeRow(Action<bool> gmCommit)
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(T.RadiusMd + 2);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(11, 8, 11, 8);
            card.Margin = new Thickness(-2, 0, -2, 6);
            card.Cursor = Cursors.Hand;
            _gmCard = card;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid ico = Ui.IconTile(Glyph.Bolt, 30, T.Accent, T.AccentA(0x22), 8);
            ico.Margin = new Thickness(0, 0, 10, 0);
            ico.IsHitTestVisible = false;
            g.Children.Add(ico);

            StackPanel sp = new StackPanel();
            sp.IsHitTestVisible = false;
            Grid.SetColumn(sp, 1);

            StackPanel top = new StackPanel();
            top.Orientation = Orientation.Horizontal;
            TextBlock title = Ui.Tx("一键游戏模式", T.FsBody, T.Hi, T.FCn);
            title.FontWeight = FontWeights.SemiBold;
            top.Children.Add(title);

            Border badge = Chip.Make("待命", T.Mute, 18);
            badge.Margin = new Thickness(8, 0, 0, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            _gmBadge = badge;
            _gmBadgeText = badge.Child as TextBlock;
            top.Children.Add(badge);
            sp.Children.Add(top);

            TextBlock sub = Ui.Mono("游戏模式 + 关 DVR + 关空闲维护 + 0.5ms 定时器 · 可逆", T.FsNano, T.Dim);
            sub.Margin = new Thickness(0, 3, 8, 0);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(sub);
            g.Children.Add(sp);

            _swGm.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_swGm, 2);
            g.Children.Add(_swGm);

            card.Child = g;
            card.MouseEnter += delegate { PaintGameModeCard(true, false); };
            card.MouseLeave += delegate { if (!card.IsMouseCaptured) PaintGameModeCard(false, false); };
            Pressable.On(card, delegate
            {
                bool want = !_swGm.On;
                _swGm.On = want;
                if (gmCommit != null) gmCommit(want);
            },
            delegate (bool down) { PaintGameModeCard(card.IsMouseOver, down); });

            SyncGameModeUi();
            return card;
        }

        private void PaintGameModeCard(bool hover, bool down)
        {
            if (_gmCard == null) return;
            bool on = _swGm != null && _swGm.On;
            if (down)
                _gmCard.Background = T.S(0x22FFFFFF);
            else if (on)
                _gmCard.Background = T.AccentWashGrad();
            else
                _gmCard.Background = hover ? (Brush)T.S(T.Raised) : T.SubCardBg();
            uint bd = on ? T.AccentA((byte)(hover ? 0xAA : 0x76)) : (hover ? T.AccentA(0x52) : T.Line);
            _gmCard.BorderBrush = T.S(bd);
        }

        private void SyncGameModeUi()
        {
            bool on = C != null && C.Shell != null && C.Shell.GameModeOn;
            if (_gmBadge != null && _gmBadgeText != null)
            {
                string want = on ? "已启用" : "待命";
                if (_gmBadgeText.Text != want) _gmBadgeText.Text = want;
                Chip.Paint(_gmBadge, on ? T.Ok : T.Mute, on);
            }
            PaintGameModeCard(_gmCard != null && _gmCard.IsMouseOver, false);
        }

        private UIElement BuildForegroundPanel(Core c)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid ico = Ui.IconTile(Glyph.Target, 32, T.Accent, Ui.Wash(T.Accent, 0x16), 6);
            ico.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(ico);

            StackPanel mid = new StackPanel();
            mid.VerticalAlignment = VerticalAlignment.Center;

            StackPanel top = new StackPanel();
            top.Orientation = Orientation.Horizontal;
            _fgTitle = Ui.Tx("当前前台进程", T.FsBody, T.Hi, T.FCn);
            _fgTitle.FontWeight = FontWeights.SemiBold;
            _fgTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(_fgTitle);

            TextBlock en = Ui.Label("FOREGROUND", T.Mute);
            en.Margin = new Thickness(8, 0, 0, 0);
            top.Children.Add(en);

            _fgBadge = Chip.Make("待捕获", T.Mute, T.ChipH);
            _fgBadge.Margin = new Thickness(10, 0, 0, 0);
            _fgBadgeText = _fgBadge.Child as TextBlock;
            top.Children.Add(_fgBadge);
            mid.Children.Add(top);

            _fgMeta = Ui.Mono("暂无外部前台窗口记录 · 切到目标应用或游戏后自动捕获", T.FsNano, T.Dim);
            _fgMeta.Margin = new Thickness(0, 4, 12, 0);
            _fgMeta.TextTrimming = TextTrimming.CharacterEllipsis;
            mid.Children.Add(_fgMeta);

            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            StackPanel acts = new StackPanel();
            acts.Orientation = Orientation.Horizontal;
            acts.VerticalAlignment = VerticalAlignment.Center;

            _fgCreateBtn = new Btn("为前台建档", BtnKind.Accent);
            _fgCreateBtn.Height = T.BtnH;
            _fgCreateBtn.SetIcon(Glyph.Profiles, 12);
            _fgCreateBtn.Visibility = Visibility.Collapsed;
            _fgCreateBtn.Click += delegate { CreateForegroundProfile(c); };
            acts.Children.Add(_fgCreateBtn);

            Btn goProf = new Btn("档案库", BtnKind.Quiet);
            goProf.Height = T.BtnH;
            goProf.Margin = new Thickness(8, 0, 0, 0);
            goProf.Click += delegate { if (c.Shell != null) c.Shell.Go("profiles"); };
            acts.Children.Add(goProf);

            Grid.SetColumn(acts, 2);
            g.Children.Add(acts);
            return Ui.Panel(g, 16);
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint STILL_ACTIVE = 259;

        [StructLayout(LayoutKind.Sequential)]
        private struct FgFileTime { public uint Low; public uint High; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out FgFileTime create, out FgFileTime exit, out FgFileTime kernel, out FgFileTime user);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, StringBuilder buf, ref int size);

        private static void InspectForegroundPid(int pid, bool needPath,
            out bool alive, out bool denied, out long born, out uint prio, out string fullPath)
        {
            alive = false;
            denied = false;
            born = 0;
            prio = 0;
            fullPath = null;
            if (pid <= 4) return;

            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 5) denied = true;
                return;
            }
            try
            {
                uint exitCode;
                if (GetExitCodeProcess(h, out exitCode) && exitCode != STILL_ACTIVE)
                {
                    alive = false;
                    return;
                }
                alive = true;
                FgFileTime c, ex, k, u;
                if (GetProcessTimes(h, out c, out ex, out k, out u))
                    born = ((long)c.High << 32) | c.Low;
                prio = GetPriorityClass(h);
                if (needPath)
                {
                    StringBuilder sb = new StringBuilder(520);
                    int cap = sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref cap) && cap > 0)
                    {
                        fullPath = sb.ToString(0, cap);
                    }
                    else if (Marshal.GetLastWin32Error() == 122)
                    {
                        sb = new StringBuilder(4096);
                        cap = sb.Capacity;
                        if (QueryFullProcessImageNameW(h, 0, sb, ref cap) && cap > 0)
                            fullPath = sb.ToString(0, cap);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("overview.fg.inspect", ex);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private void RefreshProcsAsync(Core c)
        {
            if (c == null || c.Procs == null || _fgProcsRefreshing) return;
            _fgProcsRefreshing = true;
            int gen = _fgBgGen;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    c.Procs.Get(1500);
                }
                catch (Exception ex)
                {
                    Diag.Fail("overview.fg.procs", ex);
                }
                finally
                {
                    _fgProcsRefreshing = false;
                }
                try
                {
                    if (ui != null && !ui.HasShutdownStarted && !ui.HasShutdownFinished)
                    {
                        ui.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                        {
                            if (gen == _fgBgGen) SyncForeground(true);
                        }));
                    }
                }
                catch (Exception ex)
                {
                    Diag.Fail("overview.fg.marshal", ex);
                }
            });
        }

        private void SyncForeground(bool force)
        {
            if (_fgTitle == null || C == null) return;
            int pid = C.LastForeignPid;
            int profVer = C.Profiles == null ? 0 : C.Profiles.Version;

            if (pid <= 4 || pid == SelfPid)
            {
                if (!force && pid == _fgPidShown && profVer == _fgProfVer && _fgAliveShown == 0) return;
                _fgPidShown = pid;
                _fgProfVer = profVer;
                _fgAliveShown = 0;
                _fgPrioShown = uint.MaxValue;
                _fgTargetPid = 0;
                _fgTargetBorn = 0;
                _fgTargetExe = "";
                _fgTargetFullExe = "";
                _fgTargetName = "";
                _fgTitle.Text = "当前前台进程";
                _fgMeta.Text = "暂无外部前台窗口记录 · 切到目标游戏或应用窗口后自动捕获（支持一键建档）";
                if (_fgBadgeText != null) _fgBadgeText.Text = "待捕获";
                if (_fgBadge != null) Chip.Paint(_fgBadge, T.Mute, false);
                if (_fgCreateBtn != null) _fgCreateBtn.Visibility = Visibility.Collapsed;
                return;
            }

            bool needPath = force || pid != _fgPidShown || string.IsNullOrEmpty(_fgTargetFullExe);
            bool alive, denied;
            long born;
            uint prio;
            string fullPath;
            InspectForegroundPid(pid, needPath, out alive, out denied, out born, out prio, out fullPath);
            if (alive && !needPath && born != 0 && _fgTargetBorn != 0 && born != _fgTargetBorn)
                InspectForegroundPid(pid, true, out alive, out denied, out born, out prio, out fullPath);

            // 非阻塞查共享进程缓存：若缓存过旧 (>1500ms) 或未就绪，放线程池更新，UI 线程绝不持锁扫描。
            ProcSnapshot snap = (C.Procs != null && !_fgProcsRefreshing) ? C.Procs.Current : null;
            if (C.Procs != null && !_fgProcsRefreshing && (snap == null || !snap.Ok || snap.AgeMs < 0 || snap.AgeMs > 1500))
                RefreshProcsAsync(C);

            string img = null;
            string name = null;
            if (snap != null && snap.Ok)
            {
                for (int i = 0; i < snap.Count; i++)
                {
                    ProcEntry e = snap.Items[i];
                    if (e != null && e.Pid == pid)
                    {
                        img = e.Image;
                        name = e.Name;
                        if (denied)
                        {
                            alive = true;
                            if (born == 0) born = e.Born;
                            if (prio == 0 && e.PriorityClass != 0) prio = e.PriorityClass;
                        }
                        break;
                    }
                }
            }

            if (alive && string.IsNullOrEmpty(fullPath) && string.IsNullOrEmpty(img))
            {
                string pn = ProcName.Of(pid);
                if (!string.IsNullOrEmpty(pn))
                {
                    img = pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : (pn + ".exe");
                    name = pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn.Substring(0, pn.Length - 4) : pn;
                }
            }

            if (!alive || (string.IsNullOrEmpty(fullPath) && string.IsNullOrEmpty(img)))
            {
                if (!force && pid == _fgPidShown && profVer == _fgProfVer && _fgAliveShown == 0) return;
                _fgPidShown = pid;
                _fgProfVer = profVer;
                _fgAliveShown = 0;
                _fgPrioShown = uint.MaxValue;
                _fgTargetPid = 0;
                _fgTargetBorn = 0;
                _fgTargetExe = "";
                _fgTargetFullExe = "";
                _fgTargetName = "";
                _fgTitle.Text = "当前前台进程 · PID " + pid;
                _fgMeta.Text = "最近捕获的前台进程已退出或不可读取，切换窗口后会更新";
                if (_fgBadgeText != null) _fgBadgeText.Text = "已退出";
                if (_fgBadge != null) Chip.Paint(_fgBadge, T.Mute, false);
                if (_fgCreateBtn != null) _fgCreateBtn.Visibility = Visibility.Collapsed;
                return;
            }

            string cachedFull = (pid == _fgPidShown && (born == 0 || _fgTargetBorn == 0 || born == _fgTargetBorn)) ? _fgTargetFullExe : "";
            string resolvedFull = !string.IsNullOrEmpty(fullPath) ? fullPath : cachedFull;
            string exe = !string.IsNullOrEmpty(resolvedFull) ? resolvedFull : img;
            string shortImg = !string.IsNullOrEmpty(exe) ? Path.GetFileName(exe) : img;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(shortImg))
                name = shortImg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? shortImg.Substring(0, shortImg.Length - 4) : shortImg;

            if (!force && pid == _fgPidShown && profVer == _fgProfVer && _fgAliveShown == 1
                && prio == _fgPrioShown && born == _fgTargetBorn
                && string.Equals(exe, _fgTargetExe, StringComparison.OrdinalIgnoreCase))
                return;

            _fgPidShown = pid;
            _fgProfVer = profVer;
            _fgAliveShown = 1;
            _fgPrioShown = prio;
            _fgTargetBorn = born;
            _fgTargetPid = pid;
            _fgTargetExe = exe;
            _fgTargetFullExe = resolvedFull ?? "";
            _fgTargetName = name;

            string prioStr = prio != 0 ? (" · 优先级 " + ProcessPower.PriorityLabel(prio)) : "";
            GameProfile existing = C.Profiles == null ? null : C.Profiles.FindByExe(exe);

            _fgTitle.Text = "当前前台 · " + name + "  (" + shortImg + ")";
            Tip.Attach(_fgTitle, exe);
            _fgMeta.Text = "PID " + pid + prioStr + (existing != null ? (" · 已绑定档案「" + existing.Name + "」") : " · 尚未创建专属档案，点击右侧「为前台建档」加入自动守护");

            if (existing != null)
            {
                if (_fgBadgeText != null) _fgBadgeText.Text = "已建档";
                if (_fgBadge != null) Chip.Paint(_fgBadge, T.Ok, true);
                if (_fgCreateBtn != null) _fgCreateBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (_fgBadgeText != null) _fgBadgeText.Text = "未建档";
                if (_fgBadge != null) Chip.Paint(_fgBadge, T.Warn, false);
                if (_fgCreateBtn != null) _fgCreateBtn.Visibility = Visibility.Visible;
            }
        }

        private string ResolveFullExeForCreate(int pid, string fallback)
        {
            string full = ProcName.FullPathOf(pid);
            return !string.IsNullOrEmpty(full) ? full : fallback;
        }

        private void CreateForegroundProfile(Core c)
        {
            if (c == null || c.Profiles == null || _fgTargetPid <= 4 || _fgTargetPid == SelfPid || string.IsNullOrEmpty(_fgTargetExe))
            {
                if (c != null && c.Shell != null) c.Shell.Toast("暂无有效的外部前台进程可建档", false);
                return;
            }
            string exe = _fgTargetExe;
            if (exe.IndexOf('\\') < 0 && exe.IndexOf('/') < 0)
            {
                string full = ResolveFullExeForCreate(_fgTargetPid, exe);
                if (!string.IsNullOrEmpty(full)) exe = full;
            }
            GameProfile existing = c.Profiles.FindByExe(exe);
            if (existing != null)
            {
                if (c.Shell != null) c.Shell.Toast("该前台进程已建档：" + existing.Name, true);
                SyncForeground(true);
                return;
            }
            GameProfile p = new GameProfile();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name = string.IsNullOrEmpty(_fgTargetName) ? Path.GetFileNameWithoutExtension(exe) : _fgTargetName;
            p.Exe = exe;
            p.PinCcd0 = c.Topo != null && c.Topo.Ccds > 1;
            p.HighGpu = true;
            p.Timer = false;
            p.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            bool ok = c.Profiles.Add(p);
            if (c.Engine != null && c.Engine.Ledger != null)
                c.Engine.Ledger.AppendAudit("PROFILE fg-add " + p.Name + " (" + p.Exe + ") saved=" + ok);
            c.State.Post(Dirty.Daemon);
            if (c.Shell != null) c.Shell.Toast("已为前台建档：" + p.Name + " (" + Path.GetFileName(p.Exe) + ")", ok);
            SyncForeground(true);
        }
    }
}
