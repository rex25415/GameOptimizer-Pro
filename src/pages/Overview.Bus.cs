// ============================================================================
//  Overview.Bus.cs — 01 态势总览：状态总线订阅与读数写入（Overview 分片）
//  来源：Overview.cs（862 行单体）按职责拆分，partial 共享字段，行为零变化。
//  六条域订阅（Profile/Power/Timer/Ledger/Daemon/Elevated）与它们的唯一写入点全在这一片。
// ============================================================================
using System;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Go.Pages
{
    public partial class Overview : Page
    {
        // ---- 总线订阅（SPINE）-----------------------------------------------
        /// <summary>
        /// 四条订阅覆盖本页所有"事件驱动"的读数：
        /// 档案名（Profile/Power）、定时器租约（Timer）、账本条目与守护（Ledger/Daemon）、
        /// 提权状态（Elevated）。CPU / 内存 / 抖动这些连续量仍然走 Tick。
        /// </summary>
        private void WireBus()
        {
            Watch(Dirty.Profile | Dirty.Power, OnProfileBus);
            Watch(Dirty.Timer, OnTimerBus);
            Watch(Dirty.Ledger | Dirty.Daemon, OnDaemonBus);
            Watch(Dirty.Elevated, OnElevatedBus);
        }

        /// <summary>构建完成后的第一帧：订阅项按当前真值先画一遍，不等总线派发。</summary>
        private void PrimeBus()
        {
            ApplyProfileLabel();
            RenderPowerBadge();
            RenderTuningCoverage();
            RenderReadinessPower();
            RenderReadinessGuard();
            double curMs = (C != null && C.Timer != null && C.Timer.Active && C.Timer.GrantedMs > 0)
                ? C.Timer.GrantedMs
                : ((C != null && C.Perf != null && C.Perf.Current != null && C.Perf.Current.TimerMs > 0)
                    ? C.Perf.Current.TimerMs
                    : -1);
            UpdateReadinessTimer(curMs);
            RenderTimerLease();
            RenderLedger();
            RenderGuard();
            RenderElevation();
            SyncGameModeUi();
            SyncForeground(true);
        }

        private void OnProfileBus(Dirty d)
        {
            ApplyProfileLabel();
            RenderPowerBadge();
            RenderReadinessPower();
            if ((d & Dirty.Power) != 0) RenderTuningCoverage();
        }
        private void OnTimerBus(Dirty d)
        {
            RenderTimerLease();
            if (C == null) return;
            if (C.Timer != null && C.Timer.Active && C.Timer.GrantedMs > 0)
            {
                UpdateReadinessTimer(C.Timer.GrantedMs);
                return;
            }
            double curMs = -1;
            uint mn, mx, cur;
            if (Native.NtQueryTimerResolution(out mn, out mx, out cur) == 0 && cur > 0)
                curMs = cur / 10000.0;
            else if (C.Perf != null && C.Perf.Current != null && C.Perf.Current.TimerMs > 0)
                curMs = C.Perf.Current.TimerMs;
            UpdateReadinessTimer(curMs);
        }
        private void OnElevatedBus(Dirty d) { RenderElevation(); }

        /// <summary>账本条目数与守护状态共用一条订阅：守护挂载/退出本身就会写账本。</summary>
        private void OnDaemonBus(Dirty d)
        {
            RenderLedger();
            RenderGuard();
            RenderReadinessGuard();
            RenderTuningCoverage();
            SyncForeground(true);
        }

        /// <summary>账本条目数：本页的写入动作与守护的挂载都会改变它。</summary>
        private void RenderLedger()
        {
            if (_ledgerLine == null || C == null) return;
            int pend = C.Engine.PendingCount;
            if (pend == _pendShown) return;
            _pendShown = pend;
            _ledgerLine.Text = "还原账本 · " + pend + " 项待还原";
            _ledgerLine.Foreground = T.S(pend > 0 ? T.Text : T.Mute);
        }

        /// <summary>守护引擎状态：运行中 / 待命 / 已关闭；离屏与自检下没有守护宿主，显示 —。</summary>
        private void RenderGuard()
        {
            if (_guardText == null) return;
            Go.Daemon.DaemonHost d = C == null ? null : C.Daemon;
            GameProfile a = d == null ? null : d.Watcher.Active;
            bool on = d != null && d.Watcher.Enabled;

            string text;
            uint col;
            if (d == null) { text = "\u2014"; col = T.Mute; }
            else if (a != null) { text = "运行中 " + Ui.Trunc(a.Name, 10, ""); col = T.Ok; }
            else if (on) { text = "待命"; col = T.AccentDim; }
            else { text = "已关闭"; col = T.Dim; }

            if (_guardText.Text == text) return;
            _guardText.Text = text;
            _guardText.Foreground = T.S(col);
            if (_guardDot != null) _guardDot.Fill = T.S(col);
        }

        /// <summary>提权状态：ADMIN 绿 / STANDARD 琥珀。用户在只读模式下必须一眼看见。</summary>
        private void RenderElevation()
        {
            if (_permText == null) return;
            bool el = C != null && C.Elevated;
            if (_elevShown == (el ? 1 : 0)) return;
            _elevShown = el ? 1 : 0;
            _permText.Text = el ? "ADMIN" : "STANDARD";
            _permText.Foreground = T.S(el ? T.Ok : T.Warn);
            if (_permDot != null) _permDot.Fill = T.S(el ? T.Ok : T.Warn);
        }

        /// <summary>
        /// 定时器租约：开关位置与注释只在这里写。
        /// 以前这段在 Tick 里每秒覆盖一次——不是因为状态会变，而是因为 Tick 是唯一的
        /// 刷新渠道；租约被守护引擎或电池切换改动时，界面最多滞后一秒。
        /// </summary>
        private void RenderTimerLease()
        {
            if (C == null) return;
            bool tuned = C.Timer != null && C.Timer.Active;
            if (_swTimer != null && _swTimer.On != tuned) _swTimer.On = tuned;
            if (_tmrNote == null) return;
            // 文案必须读系统真值：0.5 ms 是我们**请求**的，内核未必给。
            // 本机实测：请求 0.5 ms 时有时只落到 1.0 ms，忙时甚至原样退回 15.6 ms。
            // 契约是"TimerService.GrantedMs 就是系统真值"，所以这里只认它；
            // 写死一句"本进程 0.5ms"就是在替系统撒谎。
            double granted = C.Timer == null ? 0 : C.Timer.GrantedMs;
            bool off = tuned && granted > 0 && Math.Abs(granted - 0.5) > 0.05;
            string note = !tuned ? "系统默认"
                        : off ? ("本进程 · 请求 0.5 ms · 实测 " + granted.ToString("0.0") + " ms")
                        : ("本进程 " + granted.ToString("0.0") + " ms");
            uint col = !tuned ? T.Mute : (off ? T.Warn : T.Ok);
            if (_tmrNote.Text == note) return;      // 状态没变：连画刷都不碰
            _tmrNote.Text = note;
            _tmrNote.Foreground = T.S(col);
        }
    }
}
