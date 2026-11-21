// ============================================================================
//  Benchmark.Lifecycle.cs — 页面生命周期（Benchmark 分片）
//
//  离开页面时只暂停窗口，不销毁样本：回到页面接着算，已过的真实时间照常计入。
//  离屏期间采样器仍在产样本，但那批样本不属于"看得见的这个窗口"，
//  所以回来时把游标推到当前计数（与旧实现一致的口径）。
// ============================================================================
using System;
using System.Windows;

namespace Go.Pages
{
    public partial class Benchmark
    {
        public override void OnEnter()
        {
            if (C == null) return;
            // 采样窗口里必须让探针热跑，否则量到的"抖动"一半是探针自己在待机。
            if (C.Perf != null) C.Perf.JitterHot = true;

            if (_swTimer != null) _swTimer.On = C.Perf.RequestedTimerMs > 0;
            PaintTimerState(C.Perf.Current.TimerMs);

            // 首次进入自动跑一轮：基准页打开即产出真实基线，不必先点一次（仅唤醒超调模式）。
            if (!_autoStarted && _phase != Phase.Running && _mode == "wake")
            {
                _autoStarted = true;
                StartSample();
                return;
            }

            if (_phase == Phase.Running)
            {
                if (_mode == "wake") _cursor = C.Perf.JitterCount;
                _sw.Start();
                EnsurePoll();
                _poll.Start();
            }
        }

        public override void OnLeave()
        {
            if (C != null && C.Perf != null) C.Perf.JitterHot = false;
            if (_poll != null) _poll.Stop();
            if (_phase == Phase.Running) _sw.Stop();
        }

        public override void Tick(Snapshot s)
        {
            if (s == null || C == null) return;
            PaintTimerState(s.TimerMs);
            if (_swTimer != null)
            {
                bool on = C.Perf.RequestedTimerMs > 0;
                if (_swTimer.On != on) _swTimer.On = on;
            }
        }
    }
}
