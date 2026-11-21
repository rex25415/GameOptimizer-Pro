// ============================================================================
//  Perf.Frame.cs — 采样帧的读取顺序与各路数据源（PerfSampler 的 partial 分片）
//
//  一次 tick = 一帧：ReadCpu → ReadMemory → ReadKernelPerf → ReadMemoryList
//  → ReadTimer → ReadJitter，全部写进只属于采样线程的写缓冲，最后一帧发布。
//  拆到这个文件的是"每层怎么读"；Perf.cs 是"什么时候读、怎么停、谁在等"。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Go
{
    public partial class PerfSampler
    {
        // ====================================================================
        //  采样帧
        // ====================================================================
        private void Loop()
        {
            SampleClock clock = _clock;
            try
            {
                // 缓冲分配失败不退出线程：健康账簿承诺"30 秒后重试"，就必须真的重试。
                // 注意这里用 StopHandle.WaitOne 而不是 SampleClock.WaitTurn：
                // 后者在首次调用时立即返回（启动即有第一帧），失败重试会变成忙等。
                while (!AllocSampleBuffers())
                {
                    if (clock.StopHandle.WaitOne(KernAllocRetryMs)) return;
                }

                while (true)
                {
                    try
                    {
                        while (true)
                        {
                            long beat = BeatMs(_dormant);
                            if (!clock.WaitTurn(beat)) return;
                            SampleOnce();
                            clock.Done(BeatMs(_dormant));
                        }
                    }
                    catch (Exception ex)
                    {
                        // 采样线程是后台服务：带异常退出会**杀掉整个进程**（.NET 2.0 起的默认行为），
                        // 而 AppDomain.UnhandledException 只能记录、不能阻止终止。
                        // 一拍失败只降级这一拍；等 500ms 后继续（Stop 可立刻打断）。
                        Diag.Fail("perf.loop", ex);
                        _health.At(LayerSample).Fail(0, SampleHealth.StageException, 500);
                        if (clock.StopHandle.WaitOne(500)) return;
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("perf.loop.fatal", ex);
                _health.At(LayerSample).Fail(0, SampleHealth.StageException, 0);
            }
            finally
            {
                FreeSampleBuffers();
            }
        }

        /// <summary>休眠态把间隔拉到 3 倍；夹紧到 [25ms, 1h]，坏配置不许把线程变成忙等。</summary>
        private long BeatMs(bool dormant)
        {
            long ms = IntervalMs;
            if (dormant) ms *= 3;
            if (ms < 25) ms = 25;
            if (ms > 3600000) ms = 3600000;
            return ms;
        }

        /// <summary>一帧：全部子系统写同一份写缓冲，最后一次性发布。</summary>
        private void SampleOnce()
        {
            Snapshot w = _write;
            try
            {
                ReadCpu(w);
                ReadMemory(w);
                ReadKernelPerf(w);      // 先读页大小：下面的 memlist 用它换算（旧实现写死 4096）
                ReadMemoryList(w);
                ReadTimer(w);

                _tickCount++;
                w.Tick = _tickCount;
                w.JitterLive = _jhot && !_dormant && _bootDone;
                ReadJitter(w);

                Publish();
            }
            catch (Exception ex)
            {
                // 一帧里的托管异常只降级这一帧：写缓冲不发布，读者继续看上一帧的完整数据。
                Diag.Fail("perf.sample", ex);
                _health.At(LayerSample).Fail(0, SampleHealth.StageException, (int)BeatMs(_dormant));
            }
        }

        /// <summary>把写缓冲发布给读者，并接手上一个读缓冲继续写。读者永远看不到正在被写的那一份。</summary>
        private void Publish()
        {
            Snapshot old = _pub;
            _pub = _write;
            _write = _spare;
            _spare = old;
        }

        // ---- CPU -----------------------------------------------------------
        private void ReadCpu(Snapshot w)
        {
            if (!QueryCpu())
            {
                // 失败帧必须把每一核也清零：_write 是两拍前发布过的缓冲，
                // 只清总量会让界面出现"总量 0% + 每核仍是旧负载"的自相矛盾读数。
                w.CpuTotal = 0;
                w.DpcPercent = 0;
                for (int i = 0; i < w.Cores.Length; i++) w.Cores[i] = 0;
                return;
            }
            ApplyCpu(w);
        }

        /// <summary>读一次 class 8 并推进差分基线。false = 这一拍没有可信读数。</summary>
        private bool QueryCpu()
        {
            const int stride = 48;   // sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION) on x64
            int n = _coreCount;
            int want = stride * n;
            int ret;

            // 容量钳制：want 来自 ProcessorCount，缓冲来自 AllocSampleBuffers。
            // 两者必须在这里对齐——把"外部输入 × 硬编码容量"直接交给内核写，
            // 一旦逻辑核数超过缓冲容量就是一次堆越界（实测内核按声明长度写）。
            if (want > _cpuCap) want = _cpuCap;
            if (want < stride) want = stride;
            int st = Native.NtQuerySystemInformation(8, _cpuBuf, want, out ret);
            if (st != 0 && ret > want && ret <= _cpuCap)
            {
                want = ret;
                st = Native.NtQuerySystemInformation(8, _cpuBuf, want, out ret);
            }
            if (st != 0)
            {
                Diag.Hit("perf.cpu.query");
                _health.At(LayerCpu).Fail(st, SampleHealth.StageQuery, (int)BeatMs(_dormant));
                return false;
            }

            // 以内核回写的实际长度为准：ret/stride 才是这次真正拿到的核数。
            int got = ret >= stride ? Math.Min(n, ret / stride) : 0;
            if (got <= 0)
            {
                _health.At(LayerCpu).Fail(0, SampleHealth.StageParse, (int)BeatMs(_dormant));
                return false;
            }
            if (got != _lastCpuCount)
            {
                // 热插 / 处理器组切换：上一拍的基线不能拿来和这一拍做差，否则会算出假占用。
                _primed = false;
                _lastCpuCount = got;
            }

            long sumIdle = 0, sumTotal = 0, sumDpc = 0;
            for (int i = 0; i < got; i++) ReadCpuCore(i, stride, ref sumIdle, ref sumTotal, ref sumDpc);

            _haveDelta = _primed;
            if (_haveDelta)
            {
                _frameDt = sumTotal - _sumPrevTotal;
                _frameDi = sumIdle - _sumPrevIdle;
                _frameDpc = sumDpc - _sumPrevDpc;
            }
            _sumPrevIdle = sumIdle;
            _sumPrevTotal = sumTotal;
            _sumPrevDpc = sumDpc;
            _primed = true;
            _gotCores = got;
            _health.At(LayerCpu).Ok();
            return true;
        }

        private void ReadCpuCore(int i, int stride, ref long sumIdle, ref long sumTotal, ref long sumDpc)
        {
            IntPtr p = new IntPtr(_cpuBuf.ToInt64() + i * stride);
            long idle = Marshal.ReadInt64(p, 0);
            long kernel = Marshal.ReadInt64(p, 8);
            long user = Marshal.ReadInt64(p, 16);
            long dpc = Marshal.ReadInt64(p, 24);
            long total = kernel + user;

            sumIdle += idle;
            sumTotal += total;
            sumDpc += dpc;

            if (_primed && i < _prevTotal.Length)
            {
                long dt = total - _prevTotal[i];
                long di = idle - _prevIdle[i];
                _cores[i] = dt > 0 ? Clamp01(1.0 - (double)di / dt) : 0;
            }
            if (i < _prevTotal.Length)
            {
                _prevIdle[i] = idle;
                _prevTotal[i] = total;
            }
        }

        private void ApplyCpu(Snapshot w)
        {
            if (!_haveDelta || _frameDt <= 0)
            {
                // 第一拍只建立基线：这不是"CPU 0%"，是"还没测"。界面按 Tick 判断。
                w.CpuTotal = 0;
                w.DpcPercent = 0;
                for (int i = 0; i < w.Cores.Length; i++) w.Cores[i] = 0;
                return;
            }
            w.CpuTotal = Clamp01(1.0 - (double)_frameDi / _frameDt);
            w.DpcPercent = Math.Max(0, (double)_frameDpc / _frameDt * 100.0);

            int got = _gotCores;
            if (got > w.Cores.Length) got = w.Cores.Length;
            Array.Copy(_cores, w.Cores, got);
            // 拿到的核数少于快照长度时，尾巴必须是 0，不能是三拍前那份缓冲的旧负载。
            for (int i = got; i < w.Cores.Length; i++) w.Cores[i] = 0;
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        // ---- 内存 ----------------------------------------------------------
        private void ReadMemory(Snapshot w)
        {
            Native.MEMORYSTATUSEX ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)Native.MemStatusSize;
            if (!Native.GlobalMemoryStatusEx(ref ms))
            {
                int err = Marshal.GetLastWin32Error();
                Diag.Hit("perf.mem");
                _health.At(LayerMem).Fail(err, SampleHealth.StageQuery, (int)BeatMs(_dormant));
                w.MemTotalMB = 0;
                w.MemAvailMB = 0;
                w.CommitTotalMB = 0;
                w.CommitLimitMB = 0;
                return;
            }
            w.MemTotalMB = ms.ullTotalPhys / 1048576.0;
            w.MemAvailMB = ms.ullAvailPhys / 1048576.0;
            w.CommitTotalMB = (ms.ullTotalPageFile - ms.ullAvailPageFile) / 1048576.0;
            w.CommitLimitMB = ms.ullTotalPageFile / 1048576.0;
            _health.At(LayerMem).Ok();
        }

        // ---- 内核性能计数（复用同一块非托管缓冲）-----------------------------
        private bool EnsureKernelBuffers()
        {
            if (_kPerf != IntPtr.Zero) return true;
            if (!RetryDue(_kernNextTry)) return false;

            IntPtr a = IntPtr.Zero;
            IntPtr b = IntPtr.Zero;
            try
            {
                a = Marshal.AllocHGlobal(KPerfSize);
                b = Marshal.AllocHGlobal(MemListSize);
                for (int i = 0; i < KPerfSize; i += 4) Marshal.WriteInt32(a, i, 0);
                for (int i = 0; i < MemListSize; i += 4) Marshal.WriteInt32(b, i, 0);
                _kPerf = a;
                _memList = b;
                return true;
            }
            catch (Exception ex)
            {
                // 半成功：两块都要还。旧实现只 Free 了 a，b 是局部变量、_memList 未赋值，
                // 每次失败泄漏 256 字节；30 秒重试一次，一整天的空闲会话能积几十 KB。
                if (a != IntPtr.Zero) { try { Marshal.FreeHGlobal(a); } catch (Exception fx) { Diag.Fail("perf.alloc.free", fx); } }
                if (b != IntPtr.Zero) { try { Marshal.FreeHGlobal(b); } catch (Exception fx) { Diag.Fail("perf.alloc.free", fx); } }
                Diag.Fail("perf.alloc", ex);
                _health.At(LayerKern).Fail(0, SampleHealth.StageAlloc, KernAllocRetryMs);
                _kernNextTry = unchecked(Environment.TickCount + KernAllocRetryMs);
                return false;
            }
        }

        private void ReadKernelPerf(Snapshot w)
        {
            if (!EnsureKernelBuffers())
            {
                w.KernelPagedMB = 0;
                w.KernelNonpagedMB = 0;
                w.ProcessCount = 0;
                return;
            }

            // 实测 sizeof(PERFORMANCE_INFORMATION) = 104；cb < 104 会被拒绝（ERROR_BAD_LENGTH）
            Marshal.WriteInt32(_kPerf, 0, KPerfCb);
            if (!Native.GetPerformanceInfo(_kPerf, KPerfCb))
            {
                int err = Marshal.GetLastWin32Error();
                Diag.Hit("perf.memmgr");
                _health.At(LayerKern).Fail(err, SampleHealth.StageQuery, (int)BeatMs(_dormant));
                w.KernelPagedMB = 0;
                w.KernelNonpagedMB = 0;
                w.ProcessCount = 0;
                return;
            }

            long pageSize = Marshal.ReadIntPtr(_kPerf, 80).ToInt64();
            long kPaged = Marshal.ReadIntPtr(_kPerf, 64).ToInt64();
            long kNonPaged = Marshal.ReadIntPtr(_kPerf, 72).ToInt64();
            int procs = Marshal.ReadInt32(_kPerf, 92);

            // ---- 偏移断言（HANDOFF §2.6）------------------------------------
            // 旧代码用「if (pageSize <= 0) pageSize = 4096」兜底。那不是防御，是掩饰：
            // 一旦结构体口径变了导致整体错一位，读出来照样是正数，只是「页大小」变成了
            // 别的字段，于是页面显示一个看起来完全合理的假数字，而自检全绿。
            // 这里改成：字段必须同时满足各自的语义约束，否则这一拍干脆不给读数。
            bool pageOk = pageSize >= 512 && pageSize <= 65536 && (pageSize & (pageSize - 1)) == 0;
            bool pagedOk = kPaged >= 0 && kPaged < 4294967296L;         // 页数 < 2^32（16TB@4K）
            bool nonPagedOk = kNonPaged >= 0 && kNonPaged < 268435456L; // < 2^28（1TB@4K）
            bool procsOk = procs > 0 && procs < 1000000;
            if (!(pageOk && pagedOk && nonPagedOk && procsOk))
            {
                Diag.Hit("perf.memmgr");
                _health.At(LayerKern).Fail(0, SampleHealth.StageParse, (int)BeatMs(_dormant));
                w.KernelPagedMB = 0;
                w.KernelNonpagedMB = 0;
                w.ProcessCount = 0;
                return;
            }

            _pageSize = pageSize;
            w.KernelPagedMB = kPaged * pageSize / 1048576.0;
            w.KernelNonpagedMB = kNonPaged * pageSize / 1048576.0;
            w.ProcessCount = procs;
            _health.At(LayerKern).Ok();
        }

        // ---- 待机 / 已修改列表 -----------------------------------------------
        private void ReadMemoryList(Snapshot w)
        {
            if (!EnsureKernelBuffers())
            {
                w.MemModifiedMB = 0;
                w.MemStandbyMB = 0;
                return;
            }

            int ret;
            int st = Native.NtQuerySystemInformation(80, _memList, MemListSize, out ret);
            if (st != 0)
            {
                Diag.Hit("perf.memlist");
                _health.At(LayerMemList).Fail(st, SampleHealth.StageQuery, (int)BeatMs(_dormant));
                w.MemModifiedMB = 0;
                w.MemStandbyMB = 0;
                return;
            }

            long modified = Marshal.ReadInt64(_memList, 16);
            long standby = 0;
            for (int i = 0; i < 8; i++) standby += Marshal.ReadInt64(_memList, 40 + i * 8);

            // 页数是 SIZE_T：错位读会得到负值或天文数字。两种都不许换算成 MB 展示。
            if (modified < 0 || standby < 0 || modified > 1099511627776L || standby > 1099511627776L)
            {
                Diag.Hit("perf.memlist");
                _health.At(LayerMemList).Fail(0, SampleHealth.StageParse, (int)BeatMs(_dormant));
                w.MemModifiedMB = 0;
                w.MemStandbyMB = 0;
                return;
            }

            w.MemModifiedMB = modified * _pageSize / 1048576.0;
            w.MemStandbyMB = standby * _pageSize / 1048576.0;
            _health.At(LayerMemList).Ok();
        }

        // ---- 定时器精度 ------------------------------------------------------
        private void ReadTimer(Snapshot w)
        {
            uint mn, mx, cur;
            if (Native.NtQueryTimerResolution(out mn, out mx, out cur) == 0)
            {
                w.TimerMs = cur / 10000.0;
                w.TimerMinMs = mn / 10000.0;
                w.TimerMaxMs = mx / 10000.0;
                _health.At(LayerTimer).Ok();
                return;
            }
            Diag.Hit("perf.timer");
            _health.At(LayerTimer).Fail(0, SampleHealth.StageQuery, (int)BeatMs(_dormant));
            w.TimerMs = 0;          // 界面把 0 显示成 —，绝不保留三拍前的旧读数
            w.TimerMinMs = 0;
            w.TimerMaxMs = 0;
        }

        // ---- 抖动统计：精确分位、零分配 --------------------------------------
        private void ReadJitter(Snapshot w)
        {
            if (!w.JitterLive)
            {
                ZeroJitter(w);
                return;
            }

            int count;
            double sum, max;
            if (!CopyJitter(out count, out sum, out max))
            {
                // 样本不足（刚转热）：归零而不是沿用上一份缓冲里的旧值。
                ZeroJitter(w);
                return;
            }

            // 第 99 百分位（1-based 的 ceil(0.99·n) 个样本，换成 0-based 下标）
            int k = (int)Math.Ceiling(count * 0.99) - 1;
            if (k < 0) k = 0;
            if (k >= count) k = count - 1;

            w.JitterAvgUs = sum / count;
            w.JitterP99Us = SelectKth(_jscratch, count, k);
            w.JitterMaxUs = max;
        }

        private static void ZeroJitter(Snapshot w)
        {
            w.JitterAvgUs = 0;
            w.JitterP99Us = 0;
            w.JitterMaxUs = 0;
        }

        /// <summary>把最近样本拷进 scratch 并算出 sum/max。false = 样本还不够，别出读数。</summary>
        private bool CopyJitter(out int count, out double sum, out double max)
        {
            count = 0;
            sum = 0;
            max = 0;
            lock (_gate)
            {
                count = _jcount;
                if (count < JitterMinSamples) return false;

                int start = (_jhead - _jcount + _jring.Length) % _jring.Length;
                for (int i = 0; i < count; i++)
                {
                    double v = _jring[(start + i) % _jring.Length];
                    if (double.IsNaN(v) || v < 0) v = 0;    // 异常值不能污染排序
                    _jscratch[i] = v;
                    sum += v;
                    if (v > max) max = v;
                }
                return true;
            }
        }

        /// <summary>原地 quickselect：返回 a[0..n) 中第 k 小的值（0-based）。O(n)，零分配。</summary>
        private static double SelectKth(double[] a, int n, int k)
        {
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                double pivot = a[lo + ((hi - lo) >> 1)];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j)
                    {
                        double t = a[i]; a[i] = a[j]; a[j] = t;
                        i++; j--;
                    }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else break;
            }
            return a[k];
        }

        // ---- 非托管缓冲：所有权在线程 ----------------------------------------
        private bool AllocSampleBuffers()
        {
            const int stride = 48;
            // 容量按真实核数分配：旧实现写死 64 核 + 4096 余量。
            // 目的不是支持 256 核，而是让"声明长度"永远落在"分配容量"之内。
            int cores = _coreCount < 1 ? 1 : _coreCount;
            _cpuCap = stride * cores + 4096;   // 处理器组最多 64 条 + 头部余量
            try
            {
                _cpuBuf = Marshal.AllocHGlobal(_cpuCap);
                for (int i = 0; i < _cpuCap; i += 4) Marshal.WriteInt32(_cpuBuf, i, 0);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Fail("perf.alloc", ex);
                _health.At(LayerCpu).Fail(0, SampleHealth.StageAlloc, KernAllocRetryMs);
                return false;
            }
        }

        private void FreeSampleBuffers()
        {
            if (_cpuBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_cpuBuf); _cpuBuf = IntPtr.Zero; }
            if (_kPerf != IntPtr.Zero) { Marshal.FreeHGlobal(_kPerf); _kPerf = IntPtr.Zero; }
            if (_memList != IntPtr.Zero) { Marshal.FreeHGlobal(_memList); _memList = IntPtr.Zero; }
        }

        /// <summary>TickCount 回绕安全的"到点了吗"。</summary>
        private static bool RetryDue(int at)
        {
            return at == 0 || unchecked(Environment.TickCount - at) >= 0;
        }
    }
}
