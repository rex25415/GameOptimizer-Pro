// ============================================================================
//  Shell.Daemon.cs — 外壳与守护引擎的接缝（Shell 分片）
//  托盘驻留 / 最小化到托盘 / 守护状态回显。页面不直接碰 DaemonHost。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Go
{
    public partial class Shell : Window
    {
        private Go.Daemon.DaemonHost Daemon { get { return _core == null ? null : _core.Daemon; } }

        // 暂停 / 恢复：最小化与收进托盘都要停采样，但两件事对外表现必须不同。
        private bool _suspended;

        /// <summary>停掉 1Hz 节拍、前台监听与抖动探针（对外表现不变）。</summary>
        public void Suspend()
        {
            if (_suspended) return;
            _suspended = true;
            _beat.Stop();
            _fgWatch.Stop();
            if (_cur != null) { try { _cur.OnLeave(); } catch { /* 释放或收尾失败不改变本次结果 */ } }
            _core.Perf.Dormant = true;
            _core.Gpu.Paused = true;
            if (_core.Daemon != null) _core.Daemon.Watcher.Dormant = true;
            // 呼吸灯是独立 WPF 动画，不走 Motion 池的自停机制；收起时必须手动停。
            Motion.StopBreathe(_daemonDot);
            _guardPulsing = false;
        }

        public void Resume()
        {
            if (!_suspended) return;
            _suspended = false;
            _beat.Start();
            _fgWatch.Start();
            _core.Perf.Dormant = false;
            _core.Gpu.Paused = false;
            if (_core.Daemon != null) _core.Daemon.Watcher.Dormant = false;
            if (_cur != null) { try { _cur.OnEnter(); } catch (Exception ex) { Diag.Fail("page.onenter", ex); } }
            UpdateGuardPulse(_guardActive);
        }

        /// <summary>
        /// 收进托盘（**只有关窗和托盘菜单会走这里**）。
        ///
        /// 最小化不再调用它：正常应用点任务栏按钮只是最小化，图标要继续留在任务栏上；
        /// 而 Hide() 会让任务栏按钮消失，看起来就像"退出了"。这条分界线以前被抹掉了。
        /// </summary>
        public void Park()
        {
            if (_parked) return;
            Go.Daemon.DaemonHost d = Daemon;
            bool canTray = d != null && d.Tray.Installed;

            _parked = true;
            SaveState();
            Suspend();

            if (!canTray)
            {
                // 没有托盘可收：只能最小化，图标留在任务栏
                _parked = false;
                WindowState = WindowState.Minimized;
                return;
            }
            Hide();
            // 交还工作集：常驻守护不该占着 11 个页面的视觉树内存
            Native.TrimWorkingSet();
            if (d != null) d.Tray.UpdateTip("GameOptimizer · 守护中");
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);


        /// <summary>把窗口提到真正的前台。Activate() 会被前台锁挡住，需要显式 SetForegroundWindow。</summary>
        private void ForceForeground()
        {
            try
            {
                IntPtr h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero) SetForegroundWindow(h);
            }
            catch { /* 前台化被系统拒绝是常态（前台锁），不影响后续逻辑 */ }
        }

        public void Unpark()
        {
            _parked = false;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Resume();
            ForceForeground();
            RefreshDaemonUi();
        }

        public bool Parked { get { return _parked; } }

        /// <summary>守护状态变化后回显（导航栏脚注 + 页面按需刷新）。</summary>
        public void RefreshDaemonUi()
        {
            EnsureChromeHooks();
            RefreshPowerChip();
            RefreshChromeGameMode();
            SyncTrayState();

            if (_daemonLine == null) return;
            Go.Daemon.DaemonHost d = Daemon;
            bool on = d != null && d.Watcher.Enabled;
            Go.GameProfile g = d == null ? null : d.Watcher.Active;

            UpdateGuardPulse(g != null);
            _daemonLine.Text = g != null ? ("GUARD · " + Shorten(g.Name, 14)) : (on ? "GUARD · 待命" : "GUARD · 关闭");
            _daemonLine.Foreground = T.S(g != null ? T.Ok : (on ? T.Dim : T.Mute));
            if (_daemonDot != null) _daemonDot.Fill = T.S(g != null ? T.Ok : (on ? T.AccentDim : T.Mute));

            Page cur = _cur;
            if (cur is Pages.Profiles && cur != null) { try { ((Pages.Profiles)cur).RefreshGuard(); } catch (Exception ex) { Diag.Fail("profiles.refreshguard", ex); } }
        }

        /// <summary>幂等绑定状态总线与 PowerMonitor 边沿事件（跨主题重建不重复订阅）。</summary>
        private void EnsureChromeHooks()
        {
            if (_busToken == 0 && _core != null && _core.State != null)
            {
                _busToken = _core.State.OnToken(
                    Dirty.Power | Dirty.Daemon | Dirty.Elevated | Dirty.Timer | Dirty.Ledger,
                    delegate(Dirty mask)
                    {
                        if (_closing) return;
                        try
                        {
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                if (_closing) return;
                                if ((mask & Dirty.Power) != 0) RefreshPowerChip();
                                if ((mask & (Dirty.Daemon | Dirty.Elevated)) != 0) RefreshChromeGameMode();
                                SyncTrayState();
                            });
                        }
                        catch (Exception ex) { Diag.Fail("chrome.bus", ex); }
                    });
            }

            Go.Daemon.DaemonHost d = Daemon;
            if (d != null && d != _hookedDaemon)
            {
                UnhookDaemonPower();
                _hookedDaemon = d;
                if (d.Power != null)
                {
                    _powerChangedCb = delegate { DispatchPowerRefresh(); };
                    _powerSaverCb = delegate { DispatchPowerRefresh(); };
                    d.Power.Changed += _powerChangedCb;
                    d.Power.PowerSaverChanged += _powerSaverCb;
                }
            }
            else if (d == null && _hookedDaemon != null)
            {
                UnhookDaemonPower();
            }
        }

        private void DispatchPowerRefresh()
        {
            if (_closing) return;
            try
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (_closing) return;
                    RefreshPowerChip();
                    SyncTrayState();
                });
            }
            catch (Exception ex) { Diag.Fail("power.dispatch", ex); }
        }

        private void UnhookDaemonPower()
        {
            if (_hookedDaemon != null && _hookedDaemon.Power != null)
            {
                if (_powerChangedCb != null) _hookedDaemon.Power.Changed -= _powerChangedCb;
                if (_powerSaverCb != null) _hookedDaemon.Power.PowerSaverChanged -= _powerSaverCb;
            }
            _powerChangedCb = null;
            _powerSaverCb = null;
            _hookedDaemon = null;
        }

        private void ReleaseChromeHooks()
        {
            if (_busToken != 0 && _core != null && _core.State != null)
            {
                _core.State.OffToken(_busToken);
                _busToken = 0;
            }
            UnhookDaemonPower();
        }

        /// <summary>计算三态托盘视觉状态（Guard / Alert / Idle）与悬浮菜单态势字段。</summary>
        private void ComputeTrayState(
            out global::Go.Tray.TrayVisualState state,
            out string tipSuffix,
            out string menuStatus,
            out string profileText,
            out string timerText,
            out string powerText)
        {
            global::Go.Daemon.DaemonHost d = Daemon;
            global::Go.GameProfile g = d == null ? null : d.Watcher.Active;
            bool saver = (d != null && d.Power != null) ? d.Power.PowerSaverActive : _pwrSaver;
            bool onAc = (d != null && d.Power != null) ? d.Power.OnAc : _pwrAc;
            bool batt = (d != null && d.Power != null) ? d.Power.HasBattery : _pwrBatt;
            int pct = (d != null && d.Power != null) ? d.Power.BatteryPercent : _pwrPct;

            bool denied = d != null && d.Pilot != null && d.Pilot.LastHadDenied;
            Ledger ledger = (_core != null && _core.Engine != null) ? _core.Engine.Ledger : null;
            bool ledgerBad = (d != null && d.RecoveredEntries < 0) || (ledger != null && (ledger.Unreadable || ledger.Poisoned));
            bool sampleBad = false;
            if (_core != null && _core.Perf != null && _core.Perf.Health != null)
            {
                SampleHealthBoard hb = _core.Perf.Health;
                if (_core.Elevated)
                    sampleBad = hb.DownCount > 0;
                else
                    // LayerMemList(2) 在标准用户权限下按设计被系统拒绝，仅检查核心可见层以免误报警。
                    sampleBad = hb.At(0).Down || hb.At(1).Down || hb.At(3).Down || hb.At(4).Down || hb.At(7).Down;
            }

            if (denied || ledgerBad || sampleBad || saver)
            {
                state = global::Go.Tray.TrayVisualState.Alert;
                string reason = saver ? "节电模式限制性能"
                    : (denied ? "守护权限受阻 · 需管理员权限"
                    : (ledgerBad ? "状态账本异常 · 请检查恢复区"
                    : "采样链路降级 · 请查诊断"));
                tipSuffix = "需关注 · " + reason;
                menuStatus = tipSuffix;
            }
            else if (_gameMode || g != null)
            {
                state = global::Go.Tray.TrayVisualState.Guard;
                string target = g != null ? Shorten(g.Name, 18) : "游戏模式 ON";
                tipSuffix = "守护中 · " + target;
                menuStatus = "运行中 · " + target;
            }
            else
            {
                state = global::Go.Tray.TrayVisualState.Idle;
                bool watchOn = d != null && d.Watcher.Enabled;
                tipSuffix = watchOn ? "守护待命 · 未检测到目标游戏" : "待机 · 自动识别未开启";
                menuStatus = tipSuffix;
            }

            profileText = g != null ? Shorten(g.Name, 18) : (_gameMode ? "游戏模式 ON" : "常规待机");
            timerText = (_core != null && _core.Timer != null && _core.Timer.Active)
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} ms", _core.Timer.GrantedMs)
                : "默认";
            if (saver)
                powerText = "节能模式";
            else if (!onAc)
                powerText = (batt && pct >= 0) ? ("电池 " + pct + "%") : "电池供电";
            else
                powerText = (_core != null && !string.IsNullOrEmpty(_core.Profile)) ? _core.Profile : "AC 电源";
        }

        /// <summary>同步托盘图标三态（Idle/Guard/Alert）与已打开的托盘菜单；无托盘环境零报错降级。</summary>
        public void SyncTrayState()
        {
            global::Go.Tray.TrayVisualState state;
            string tipSuffix, menuStatus, profileText, timerText, powerText;
            ComputeTrayState(out state, out tipSuffix, out menuStatus, out profileText, out timerText, out powerText);

            global::Go.Daemon.DaemonHost d = Daemon;
            bool trayInstalled = d != null && d.Tray != null && d.Tray.Installed;
            bool changed = (_lastTrayState != (int)state) || (_lastTrayTip != tipSuffix) || (trayInstalled && !_lastTrayInstalled);
            _lastTrayState = (int)state;
            _lastTrayTip = tipSuffix;
            _lastTrayInstalled = trayInstalled;

            if (d == null) return;
            if (changed && d.Tray != null)
            {
                try { d.Tray.SetVisualState(state, tipSuffix); }
                catch (Exception ex) { Diag.Fail("tray.state", ex); }
            }
            if (d.MenuOpen && d.Menu != null)
            {
                try
                {
                    d.Menu.SetStatus(state, menuStatus);
                    d.Menu.SetPosture(profileText, timerText, powerText);
                }
                catch (Exception ex) { Diag.Fail("tray.menu", ex); }
            }
        }

        private bool _guardPulsing;
        private bool _guardActive;

        /// <summary>守护命中时，状态点缓慢明灭——一眼就能看出"正在生效"而不是"待命"。</summary>
        private void UpdateGuardPulse(bool active)
        {
            _guardActive = active;
            if (active == _guardPulsing) return;
            _guardPulsing = active;
            if (active) Motion.Breathe(_daemonDot, 0.45, 1.0, 1800);
            else Motion.StopBreathe(_daemonDot);
        }

        /// <summary>导轨重建后调用：新状态点还没挂呼吸动画，旧点上的 Forever 动画也要停掉。</summary>
        private void ResetGuardPulseForRebuild()
        {
            _guardPulsing = false;
            UpdateGuardPulse(_guardActive);
        }

        private static string Shorten(string s, int n)
        {
            return Ui.Trunc(s, n, "");
        }

    }
}
