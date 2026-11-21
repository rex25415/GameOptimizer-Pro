// ============================================================================
//  SampleClock.cs — 采样节拍（一个实现，所有采样线程共用）
//
//  为什么不再用 Thread.Sleep：
//   旧时钟是 "Sleep(1s) → 起床采一拍"。Stop() 只是把标志置 false，最坏要等
//   一整个间隔（休眠态 3s）才退出；设置变化（Dormant / JitterHot）同样要等。
//   线程退出慢 = 关窗慢 = 进程被拖住，而它明明是后台线程。
//
//  现在的契约：
//   · 绝对时刻排程：_due 是"这一拍应该开始的时刻"，Done 推进它，采样耗时
//     不累积成漂移；落后太多则从当前时刻重新起算，绝不连发补拍。
//   · WaitAny 阻塞：停止和设置变化都能立刻打断等待，返回 false 表示该退了。
//   · 等待句柄与 WaitHandle[] 在构造期一次分配，节拍路径零分配。
//
//  谁在等：go.sampler（Perf）与 go.gpu（Gpu）各持一个实例，同一套排程语义。
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace Go
{
    internal sealed class SampleClock : IDisposable
    {
        // 比这更短的间隔一定是配置错误（0 / 负数）：夹到 16ms，别把线程变成忙等。
        private const long MinIntervalMs = 16;

        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly WaitHandle[] _waits = new WaitHandle[2];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _due;
        private bool _primed;

        public SampleClock()
        {
            _waits[0] = _stop;
            _waits[1] = _wake;
        }

        /// <summary>给共用这一份停止信号的线程（抖动探针）用的等待句柄。</summary>
        public WaitHandle StopHandle { get { return _stop; } }

        /// <summary>置停止位并叫醒等待者。幂等。</summary>
        public void Stop()
        {
            try { _stop.Set(); }
            catch (ObjectDisposedException) { /* 已释放：说明停机早已完成 */ }
            try { _wake.Set(); }
            catch (ObjectDisposedException) { /* 同上 */ }
        }

        public bool Stopped
        {
            get
            {
                try { return _stop.WaitOne(0); }
                catch (ObjectDisposedException) { return true; }
            }
        }

        /// <summary>设置变了：让等待中的线程立刻重算下一拍。</summary>
        public void Wake()
        {
            try { _wake.Set(); }
            catch (ObjectDisposedException) { /* 停机中：没有等待者需要叫醒 */ }
        }

        /// <summary>
        /// 等到下一拍该开始的时刻。true = 可以采样；false = 收到停止。
        /// 首次调用立即返回（启动即有第一帧读数，不用等一个间隔）。
        /// </summary>
        public bool WaitTurn(long intervalMs)
        {
            if (intervalMs < MinIntervalMs) intervalMs = MinIntervalMs;
            if (intervalMs > int.MaxValue) intervalMs = int.MaxValue;
            if (!_primed)
            {
                _primed = true;
                _due = _clock.ElapsedMilliseconds;
            }

            for (;;)
            {
                long now = _clock.ElapsedMilliseconds;
                if (now >= _due) return !Stopped;

                long rem = _due - now;
                int wait = rem >= int.MaxValue ? int.MaxValue : (int)rem;
                int idx;
                try { idx = WaitHandle.WaitAny(_waits, wait); }
                catch (ObjectDisposedException) { return false; }

                if (idx == 0) return false;
                if (idx == 1) _due = _clock.ElapsedMilliseconds;
            }
        }

        /// <summary>采样完成后推进到期时刻。intervalMs 用这一拍实际使用的值。</summary>
        public void Done(long intervalMs)
        {
            if (intervalMs < MinIntervalMs) intervalMs = MinIntervalMs;
            if (intervalMs > int.MaxValue) intervalMs = int.MaxValue;
            _due += intervalMs;
            long now = _clock.ElapsedMilliseconds;
            // 采样本身比间隔还慢：不追补（连发会把一次卡顿放大成一串采样），从现在起重算。
            if (_due <= now) _due = now + intervalMs;
        }

        public void Dispose()
        {
            _stop.Close();
            _wake.Close();
        }
    }
}
