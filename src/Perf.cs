// ============================================================================
//  Perf.cs — 采样内核：CPU 拓扑 / 内存分级 / 唤醒抖动 / 定时器精度
//
//  结构（Rev86 重写）：
//   · 一次 tick = 一帧。ReadCpu → ReadMemory → ReadKernelPerf → ReadMemoryList
//     → ReadTimer → ReadJitter 全部写进**只属于采样线程**的写缓冲，最后一帧
//     发布（换引用）。消费者只读已发布的那一份：不需要锁，也永远看不到写了一半的帧。
//   · 唯一的并发闸门是 _gate。它只保护"抖动环 + 游标"这一份跨线程可变状态；
//     快照走"发布即替换"，不参与加锁。旧版本里 CPU/内存/抖动各自一条路径、
//     各自一份旧值，读者能同时读到三个不同年代的数。
//   · 节拍由 SampleClock 统一排程（绝对时刻 + WaitAny）。Stop()/Dormant 变化
//     能立刻打断等待——线程退出是毫秒级，不再"最坏等一个间隔"。
//   · 任何一层失败都进 SampleHealth：哪层、什么码、哪一步、什么时候再试。
//     界面读 Health，不再只有一条没人看得懂的 Diag 计数。
//
//  零分配约定（Rev61 保留，Rev86 加固）：
//   · 快照三缓冲轮转，Current 直接返回已发布对象，不再 MemberwiseClone + 数组克隆；
//   · 所有非托管缓冲在采样线程启动时一次性分配，循环体内无 AllocHGlobal/FreeHGlobal；
//   · 抖动统计走预分配 scratch + 原地 quickselect，不排序、不装箱；
//   · 进程数直接取 PERFORMANCE_INFORMATION.ProcessCount，不调 Process.GetProcesses()；
//   · 降级账簿只写 int，格式化只在诊断读取时发生；
//   · Marshal.SizeOf / 结构体长度全部预计算，不再每拍反射。
//
//  分片：本文件 = 生命周期 / 公开读数 / 抖动探针；每层怎么读在 Kernel/Perf.Frame.cs；
//  帧的数据形状与 P/Invoke 在 Kernel/SampleFrame.cs（单文件 < 800 行的约束）。
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace Go
{
    public partial class PerfSampler
    {
        // ---- 线程与统一时钟 --------------------------------------------------
        private Thread _thread;
        private Thread _jit;
        private SampleClock _clock;
        private AutoResetEvent _jitWake;

        // ---- 快照三缓冲 ------------------------------------------------------
        private volatile Snapshot _pub = new Snapshot();
        private Snapshot _write = new Snapshot();
        private Snapshot _spare = new Snapshot();
        private int _tickCount;
        private int _coreCount;

        // ---- 抖动环：全采样器唯一的跨线程可变状态（见文件头）------------------
        private const int JitterRing = 1200;
        private const int JitterMinSamples = 8;
        private readonly object _gate = new object();
        private readonly double[] _jring = new double[JitterRing];
        private readonly double[] _jscratch = new double[JitterRing];
        private int _jhead, _jcount;
        private long _jsamples;

        // ---- 设置（UI / 守护线程写，采样线程读）------------------------------
        private volatile bool _dormant;
        private volatile bool _jhot;
        private volatile bool _bootDone = true;
        private volatile int _reqTimerMsX10000;

        public int IntervalMs = 1000;

        // ---- CPU 差分状态（只有采样线程碰，无需锁）----------------------------
        private IntPtr _cpuBuf;
        private int _cpuCap;
        private long[] _prevIdle;
        private long[] _prevTotal;
        private double[] _cores;
        private int _lastCpuCount;
        private int _gotCores;
        private long _sumPrevIdle, _sumPrevTotal, _sumPrevDpc;
        private long _frameDt, _frameDi, _frameDpc;
        private bool _primed;
        private bool _haveDelta;

        // ---- 内核缓冲（复用；线程启动分配、退出释放）--------------------------
        private IntPtr _kPerf;
        private IntPtr _memList;
        private const int KPerfSize = 160;
        private const int KPerfCb = 104;
        private const int MemListSize = 256;
        private const int KernAllocRetryMs = 30000;
        private int _kernNextTry;
        private long _pageSize = 4096;

        // ---- 健康账簿（诊断面板读它）-----------------------------------------
        private const int LayerCpu = 0;
        private const int LayerMem = 1;
        private const int LayerMemList = 2;
        private const int LayerKern = 3;
        private const int LayerTimer = 4;
        private const int LayerLease = 5;
        private const int LayerJitter = 6;
        private const int LayerSample = 7;
        private readonly SampleHealthBoard _health = new SampleHealthBoard(new string[]
        {
            "perf.cpu.query", "perf.mem", "perf.memlist", "perf.memmgr",
            "perf.timer", "perf.timer.lease", "perf.jitter", "perf.sample"
        });

        /// <summary>各层采样通路的健康度：哪层降级、什么码、下次重试时间。</summary>
        public SampleHealthBoard Health { get { return _health; } }

        /// <summary>
        /// 静默模式：窗口收到托盘后进入。抖动探针本是 1ms 一次的高频唤醒，
        /// 常驻后台时停掉它，采样间隔拉到 3s —— 这是"托盘常驻 CPU 0.0%"的关键。
        /// </summary>
        public bool Dormant
        {
            get { return _dormant; }
            set
            {
                if (_dormant == value) return;
                _dormant = value;
                WakeSample();      // 立刻按新间隔重排，不等旧的等待到期
                WakeJitter();      // 探针的冷/热判定也依赖它：不叫醒就会白等一个阻塞
            }
        }

        /// <summary>有页面正在显示抖动读数时置真。见 _jhot 的注释。</summary>
        public bool JitterHot
        {
            get { return _jhot; }
            set
            {
                if (_jhot == value) return;
                _jhot = value;
                WakeJitter();
            }
        }

        /// <summary>首帧是否已上屏。常规启动由 App 在 ContentRendered 置真，之前探针不热跑。</summary>
        public bool BootDone
        {
            get { return _bootDone; }
            set
            {
                if (_bootDone == value) return;
                _bootDone = value;
                // 不叫醒的代价：探针在冷态里阻塞等待，首帧已经上屏却还要等下一次超时
                // 才会转热。旧实现的 50ms 轮询正是为了掩盖这一点——代价是常驻 20Hz 唤醒。
                WakeJitter();
            }
        }

        /// <summary>把探针从阻塞等待里叫醒。对象已释放时静默（停机路径）。</summary>
        private void WakeJitter()
        {
            AutoResetEvent w = _jitWake;
            if (w == null) return;
            try { w.Set(); }
            catch (ObjectDisposedException) { /* 停机中：探针已经不需要叫醒 */ }
        }

        /// <summary>最近一次发布的快照。只读，不要修改其字段。</summary>
        public Snapshot Current { get { return _pub; } }

        // ====================================================================
        //  生命周期
        // ====================================================================
        public void Start()
        {
            if (_thread != null && _thread.IsAlive) return;   // 幂等
            // 上一轮如果还没退干净，绝不能开第二轮：两条线程会共用同一组非托管缓冲，
            // 先退出的那条在 finally 里 Free，后一条就踩在已释放的内存上。
            JoinQuiet(_thread);
            JoinQuiet(_jit);
            if (_thread != null && _thread.IsAlive) { Diag.Hit("perf.start.busy"); return; }
            if (_jit != null && _jit.IsAlive) { Diag.Hit("perf.start.busy"); return; }
            _thread = null;
            _jit = null;
            _coreCount = Environment.ProcessorCount;
            int n = _coreCount;
            _pub.Cores = new double[n];
            _write.Cores = new double[n];
            _spare.Cores = new double[n];
            _cores = new double[n];
            _prevIdle = new long[n];
            _prevTotal = new long[n];
            _primed = false;
            _haveDelta = false;
            _lastCpuCount = 0;
            _tickCount = 0;

            SampleClock oldClock = _clock;
            AutoResetEvent oldWake = _jitWake;
            _clock = new SampleClock();
            _jitWake = new AutoResetEvent(false);
            if (oldClock != null) oldClock.Dispose();   // 两条线程都已确认退出，句柄可以安全释放
            if (oldWake != null) oldWake.Close();

            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "go.sampler";
            _thread = t;
            t.Start();

            Thread j = new Thread(JitterLoop);
            j.IsBackground = true;
            // 起手必须是冷的：JitterLoop 的 hot 初值是 false，而切优先级的代码只在
            // want != hot 时执行。若这里就设成 Highest，冷启动永远走不到"降下来"那一步，
            // 一个 20Hz 唤醒的 High 线程会一直挂到进程结束（收进托盘也在耗电）。
            j.Priority = ThreadPriority.BelowNormal;
            j.Name = "go.jitter";
            _jit = j;
            j.Start();
        }

        public void Stop()
        {
            SampleClock c = _clock;
            if (c != null)
            {
                try { c.Stop(); }
                catch (ObjectDisposedException) { /* 已停过：没有等待者 */ }
            }
            AutoResetEvent w = _jitWake;
            if (w != null)
            {
                try { w.Set(); }
                catch (ObjectDisposedException) { /* 探针线程已经退出 */ }
            }
            bool sampleDead = JoinQuiet(_thread);
            bool jitterDead = JoinQuiet(_jit);
            if (!sampleDead || !jitterDead) Diag.Hit("perf.stop.timeout");
            if (sampleDead) _thread = null;
            if (jitterDead) _jit = null;
            DisposeClock();
        }

        /// <summary>有界等待线程退出。true = 线程已经不在了（或从未启动）。</summary>
        private static bool JoinQuiet(Thread t)
        {
            if (t == null || !t.IsAlive) return true;
            try { return t.Join(500); }
            catch (ThreadStateException) { return true; }   // 从未启动：没有可等的线程
            catch (Exception ex) { Diag.Fail("perf.stop.join", ex); return false; }
        }

        private void DisposeClock()
        {
            // 只有两条线程都确认死了才释放句柄：还在 WaitAny 里等着的线程拿到已释放的
            // 句柄会抛 ObjectDisposedException，那会比泄漏两个句柄糟糕得多。
            bool dead = (_thread == null || !_thread.IsAlive) && (_jit == null || !_jit.IsAlive);
            if (!dead) return;
            SampleClock c = _clock;
            _clock = null;
            if (c != null) c.Dispose();
            AutoResetEvent w = _jitWake;
            _jitWake = null;
            if (w != null) w.Close();
        }

        private void WakeSample()
        {
            SampleClock c = _clock;
            if (c == null) return;
            c.Wake();
        }

        // ---- 定时器精度 ---------------------------------------------------
        /// <summary>请求 100ns 单位的定时器精度；返回实际生效的毫秒数。</summary>
        public double RequestTimerResolution(double ms)
        {
            uint want = (uint)(ms * 10000.0);
            uint actual = 0;

            // 机器的状态不由我们决定：系统可能给出比请求更粗的值
            // （本机实测：请求 0.5ms 时常常只落到 1.0ms；忙的时候甚至原样退回 15.6ms）。
            // 所以这里**只做一件事：如实返回系统给的数**，绝不把"我要了"当成"我得到了"。
            // 请求值与实际值的差异由界面负责呈现（调度页写"请求 0.5 / 实测 1.0"）。
            int st = Native.NtSetTimerResolution(want, true, out actual);
            if (st != 0 || actual == 0)
            {
                // 旧实现在这里 Thread.Sleep(2) 后重试。这个调用发生在 UI 线程（开关点击）
                // 且被 TimerService 的租约锁包着：为了让一次几乎不会成功的重试，
                // 把 2ms 的睡眠加到点击响应上，不划算。立即重试一次即可。
                st = Native.NtSetTimerResolution(want, true, out actual);
            }

            if (st != 0 || actual == 0)
            {
                // 系统明确拒绝：界面显示"未持有"，健康账簿记下状态码与"可立即重试"。
                Diag.Hit("perf.timer.lease");
                _health.At(LayerLease).Fail(st, SampleHealth.StageQuery, 0);
                _reqTimerMsX10000 = 0;
                return 0;
            }

            _health.At(LayerLease).Ok();
            _reqTimerMsX10000 = (int)actual;
            return actual / 10000.0;
        }

        public void ReleaseTimerResolution()
        {
            int v = _reqTimerMsX10000;
            if (v <= 0) return;
            uint a;
            Native.NtSetTimerResolution((uint)v, false, out a);
            _reqTimerMsX10000 = 0;
        }

        public double RequestedTimerMs { get { return _reqTimerMsX10000 / 10000.0; } }

        // ---- 抖动样本（页面波形用）------------------------------------------
        public long JitterCount { get { lock (_gate) { return _jsamples; } } }

        /// <summary>取出自上次调用以来新增的抖动样本（零分配版本，写入调用方缓冲）。</summary>
        public int DrainInto(double[] dest, ref long cursor)
        {
            if (dest == null || dest.Length == 0) return 0;
            lock (_gate)
            {
                long oldest = _jsamples - _jcount;
                if (cursor < oldest) cursor = oldest;
                long avail = _jsamples - cursor;
                if (avail <= 0) return 0;
                long take = avail > dest.Length ? dest.Length : avail;
                int start = (int)((_jhead - avail + _jring.Length) % _jring.Length);
                for (int i = 0; i < (int)take; i++) dest[i] = _jring[(start + i) % _jring.Length];
                // 只前进实际交付的条数：UI 一卡顿，积压会超过 dest 长度，
                // 旧实现写 cursor = _jsamples 等于把没交出去的样本直接丢掉（波形出现断档）。
                cursor += take;
                return (int)take;
            }
        }

        /// <summary>兼容旧调用：返回新样本数组（仅页面进入时使用，不在热路径上）。</summary>
        public double[] Drain(ref long cursor)
        {
            double[] tmp = new double[256];
            int n = DrainInto(tmp, ref cursor);
            if (n <= 0) return null;
            if (n == tmp.Length) return tmp;
            double[] r = new double[n];
            Array.Copy(tmp, r, n);
            return r;
        }

        /// <summary>全量抖动样本（页面进入时一次性取用）。</summary>
        public double[] JitterSnapshot()
        {
            lock (_gate)
            {
                double[] r = new double[_jcount];
                int start = (_jhead - _jcount + _jring.Length) % _jring.Length;
                for (int i = 0; i < _jcount; i++) r[i] = _jring[(start + i) % _jring.Length];
                return r;
            }
        }

        // ====================================================================
        //  抖动探针：Sleep(1) 的唤醒超调 = 调度与定时器颗粒度实况
        // ====================================================================
        private void JitterLoop()
        {
            SampleClock clock = _clock;
            AutoResetEvent wake = _jitWake;
            if (clock == null || wake == null) return;

            WaitHandle[] waits = new WaitHandle[2];
            waits[0] = clock.StopHandle;
            waits[1] = wake;

            Stopwatch sw = Stopwatch.StartNew();
            bool hot = false;
            while (true)
            {
                try
                {
                    while (JitterStep(ref hot, waits, sw, clock)) { }
                    return;     // JitterStep 返回 false = 收到 Stop
                }
                catch (Exception ex)
                {
                    // 探针线程带异常退出会留下一个永远为 - 的抖动读数：记账并自愈。
                    // 旧实现一次异常就永久停摆，健康账簿却写着"可立即重试"。
                    Diag.Fail("perf.jitter", ex);
                    _health.At(LayerJitter).Fail(0, SampleHealth.StageException, 250);
                    try
                    {
                        if (WaitHandle.WaitAny(waits, 250) == 0) return;
                    }
                    catch (ObjectDisposedException) { return; }
                }
            }
        }

        /// <summary>一步：热跑时采一个样本，冷态时等 50ms。false = 该退出了。</summary>
        private bool JitterStep(ref bool hot, WaitHandle[] waits, Stopwatch sw, SampleClock clock)
        {
            bool want = _jhot && !_dormant && _bootDone;
            if (want != hot)
            {
                hot = want;
                SetProbePriority(hot);
                // 冷→热清环：上一会话冻住的样本不能混进新读数冒充实时。
                // 转冷时环不清（ReadJitter 会跳过），转热时从零开始采。
                if (hot) ResetJitterRing();
            }

            // 冷态：无限期等一个事件（BootDone / JitterHot / Dormant / Stop 都会 Set）。
            // 旧实现是 WaitAny(waits, 50)：只要没人看抖动，探针就永远 20Hz 空转，
            // 与"托盘常驻近零开销"的目标正好相反；而它轮询要等的三件事本都有 setter 可以叫醒。
            if (!hot) return WaitHandle.WaitAny(waits) != 0;

            // 热跑：这一下 1ms 等待**就是被测对象**，不能换成别的等待原语——
            // Sleep 与 WaitForSingleObject 的唤醒路径不同，换了量到的就不是同一件事。
            long t0 = sw.ElapsedTicks;
            Thread.Sleep(1);
            long t1 = sw.ElapsedTicks;

            double us = (t1 - t0) * 1000000.0 / Stopwatch.Frequency - 1000.0;
            if (us < 0) us = 0;
            if (us > 65000) us = 65000;
            RecordJitter(us);

            // 第二个 1ms 是采样间隔（原来是第二次 Sleep(1)）：这里可被打断，
            // 所以 Stop / JitterHot 变化不必等到下一次唤醒。
            if (clock.Stopped) return false;
            return WaitHandle.WaitAny(waits, 1) != 0;
        }

        private void SetProbePriority(bool hot)
        {
            try
            {
                Thread.CurrentThread.Priority = hot ? ThreadPriority.Highest : ThreadPriority.BelowNormal;
            }
            catch (Exception ex)
            {
                // 无权改优先级：探针照跑，只是读数偏大。记账，别装作设置成功。
                Diag.Fail("perf.jitter.pri", ex);
                _health.At(LayerJitter).Fail(0, SampleHealth.StageException, 0);
            }
        }

        internal void ResetJitterRing()
        {
            lock (_gate)
            {
                _jhead = 0;
                _jcount = 0;
            }
        }

        internal void RecordJitter(double us)
        {
            lock (_gate)
            {
                _jring[_jhead] = us;
                _jhead = (_jhead + 1) % _jring.Length;
                if (_jcount < _jring.Length) _jcount++;
                _jsamples++;
            }
        }
    }
}
