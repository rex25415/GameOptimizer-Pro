// ============================================================================
//  BenchSuite.cs — 非虚构微基准测试套件（纯 /unsafe- 托管安全实现）
//
//  包含两项真实验证：
//   1) RunMemoryBench: 跨大数组按缓存行随机置换指针追逐延迟 (ns) +
//      顺序内存块复制带宽 (MB/s)。通过防死码消除汇防止 JIT 裁掉测试循环。
//   2) RunThreadPingPong: 跨线程 AutoResetEvent 往返调度唤醒延迟 (P50 / P99 / Max µs)。
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace Go
{
    public sealed class MemoryBenchResult
    {
        public double SeqCopyMBs;
        public double MemLatNs;
        public int SeqBufferMB;
        public int ChaseBufferMB;
        public int ChaseSteps;
        public long ElapsedMs;
        public int Checksum;
        public string Note = "";
    }

    public sealed class PingPongResult
    {
        public double P50Us;
        public double P99Us;
        public double MaxUs;
        public double MinUs;
        public double AvgUs;
        public int Count;
        public long ElapsedMs;
        public double OneWayP50Us { get { return P50Us * 0.5; } }
        public string Note = "";
    }

    public static class BenchSuite
    {
        private static volatile int _sink;

        public static MemoryBenchResult RunMemoryBench()
        {
            return RunMemoryBench(48 * 1024 * 1024, 64 * 1024 * 1024, 600000);
        }

        public static MemoryBenchResult RunMemoryBench(int seqBytes, int chaseBytes, int chaseSteps)
        {
            if (seqBytes < 1024 * 1024) seqBytes = 1024 * 1024;
            if (chaseBytes < 1024 * 1024) chaseBytes = 1024 * 1024;
            if (chaseSteps < 10000) chaseSteps = 10000;

            Stopwatch totalSw = Stopwatch.StartNew();
            MemoryBenchResult r = new MemoryBenchResult();
            r.SeqBufferMB = seqBytes / (1024 * 1024);
            r.ChaseBufferMB = chaseBytes / (1024 * 1024);
            r.ChaseSteps = chaseSteps;

            // 1. 顺序内存复制吞吐（大块绕开单核 L1/L2，覆盖预热后连续拷贝多轮）
            byte[] src = new byte[seqBytes];
            byte[] dst = new byte[seqBytes];
            for (int i = 0; i < src.Length; i += 4096) src[i] = (byte)(i * 31 + 7);
            src[src.Length - 1] = 0x5A;

            Buffer.BlockCopy(src, 0, dst, 0, seqBytes);
            int seqRounds = seqBytes >= 32 * 1024 * 1024 ? 6 : 12;
            int xorAcc = dst[0] ^ dst[seqBytes - 1];

            Stopwatch swCopy = Stopwatch.StartNew();
            for (int k = 0; k < seqRounds; k++)
            {
                Buffer.BlockCopy(src, 0, dst, 0, seqBytes);
                src[0] ^= 1;
                xorAcc ^= dst[(k * 4099) % seqBytes];
            }
            swCopy.Stop();
            double copySec = Math.Max(1e-6, swCopy.Elapsed.TotalSeconds);
            r.SeqCopyMBs = ((double)seqBytes * seqRounds) / (1024.0 * 1024.0) / copySec;

            // 释放大复制缓冲后再分配指针追逐数组，降低峰值内存足迹
            src = null;
            dst = null;

            // 2. 缓存行步长（64B = 16 个 int）随机置换哈密顿单环指针追逐
            int ints = chaseBytes / 4;
            const int intsPerLine = 16;
            int slots = ints / intsPerLine;
            int[] chain = new int[slots * intsPerLine];
            int[] order = new int[slots];
            for (int i = 0; i < slots; i++) order[i] = i;

            uint rng = 0x9E3779B9u;
            for (int i = slots - 1; i > 0; i--)
            {
                rng ^= rng << 13;
                rng ^= rng >> 17;
                rng ^= rng << 5;
                int j = (int)(rng % (uint)(i + 1));
                int tmp = order[i];
                order[i] = order[j];
                order[j] = tmp;
            }
            for (int i = 0; i < slots; i++)
            {
                int fromSlot = order[i];
                int toSlot = order[i + 1 < slots ? i + 1 : 0];
                chain[fromSlot * intsPerLine] = toSlot * intsPerLine;
            }
            order = null;

            int cur = 0;
            int warmup = Math.Min(50000, chaseSteps / 4);
            for (int i = 0; i < warmup; i++) cur = chain[cur];

            Stopwatch swChase = Stopwatch.StartNew();
            for (int i = 0; i < chaseSteps; i++)
            {
                cur = chain[cur];
            }
            swChase.Stop();

            double chaseNs = swChase.Elapsed.TotalSeconds * 1e9;
            r.MemLatNs = chaseNs / chaseSteps;
            r.Checksum = xorAcc ^ cur;
            _sink = r.Checksum;

            totalSw.Stop();
            r.ElapsedMs = totalSw.ElapsedMilliseconds;
            r.Note = r.SeqBufferMB + "MB 复制 " + r.SeqCopyMBs.ToString("0") + " MB/s · "
                   + r.ChaseBufferMB + "MB 延迟 " + r.MemLatNs.ToString("0.0") + " ns";
            return r;
        }

        public static PingPongResult RunThreadPingPong()
        {
            return RunThreadPingPong(400);
        }

        public static PingPongResult RunThreadPingPong(int rounds)
        {
            if (rounds < 20) rounds = 20;
            if (rounds > 5000) rounds = 5000;

            Stopwatch totalSw = Stopwatch.StartNew();
            PingPongResult r = new PingPongResult();
            r.Count = rounds;

            double[] samples = new double[rounds];
            AutoResetEvent ping = new AutoResetEvent(false);
            AutoResetEvent pong = new AutoResetEvent(false);
            AutoResetEvent workerReady = new AutoResetEvent(false);
            int stop = 0;
            Exception workerEx = null;

            Thread worker = new Thread(delegate ()
            {
                try
                {
                    workerReady.Set();
                    while (true)
                    {
                        if (!ping.WaitOne(2000))
                        {
                            if (Interlocked.CompareExchange(ref stop, 0, 0) != 0) break;
                            continue;
                        }
                        if (Interlocked.CompareExchange(ref stop, 0, 0) != 0) break;
                        pong.Set();
                    }
                }
                catch (ObjectDisposedException)
                {
                    if (Interlocked.CompareExchange(ref stop, 0, 0) == 0)
                    {
                        Exception ex = new ObjectDisposedException("ping");
                        Interlocked.CompareExchange(ref workerEx, ex, null);
                        Diag.Fail("bench.pingpong.worker", ex);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref workerEx, ex, null);
                    Diag.Fail("bench.pingpong.worker", ex);
                    try { pong.Set(); }
                    catch (ObjectDisposedException) { /* 停机清理时句柄已关闭 */ }
                }
            });
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.Highest;
            worker.Name = "go.bench.pingpong";

            ThreadPriority oldPrio = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                worker.Start();
                if (!workerReady.WaitOne(2000))
                    throw new TimeoutException("线程乒乓工作线程启动超时");

                // JIT 与调度预热
                for (int w = 0; w < 32; w++)
                {
                    Exception wex = Interlocked.CompareExchange(ref workerEx, null, null);
                    if (wex != null) throw new InvalidOperationException("乒乓工作线程异常", wex);
                    ping.Set();
                    if (!pong.WaitOne(2000))
                        throw new TimeoutException("线程乒乓预热等待超时");
                }

                double freqToUs = 1000000.0 / Stopwatch.Frequency;
                double sum = 0;
                double min = double.MaxValue;
                double max = 0;
                for (int i = 0; i < rounds; i++)
                {
                    Exception wex = Interlocked.CompareExchange(ref workerEx, null, null);
                    if (wex != null) throw new InvalidOperationException("乒乓工作线程异常", wex);
                    long t0 = Stopwatch.GetTimestamp();
                    ping.Set();
                    if (!pong.WaitOne(2000))
                        throw new TimeoutException("线程乒乓第 " + (i + 1) + " 轮等待超时");
                    long dt = Stopwatch.GetTimestamp() - t0;
                    double us = Math.Max(0.1, dt * freqToUs);
                    samples[i] = us;
                    sum += us;
                    if (us < min) min = us;
                    if (us > max) max = us;
                }
                Exception finalWex = Interlocked.CompareExchange(ref workerEx, null, null);
                if (finalWex != null) throw new InvalidOperationException("乒乓工作线程异常", finalWex);

                Array.Sort(samples);
                r.P50Us = Percentile(samples, 0.50);
                r.P99Us = Percentile(samples, 0.99);
                r.MaxUs = max;
                r.MinUs = min == double.MaxValue ? 0 : min;
                r.AvgUs = sum / rounds;
            }
            catch (Exception ex)
            {
                Diag.Fail("bench.pingpong", ex);
                r.Note = "失败: " + ex.Message;
            }
            finally
            {
                try { Thread.CurrentThread.Priority = oldPrio; }
                catch (Exception ex) { Diag.Fail("bench.pingpong.prio", ex); }

                Interlocked.Exchange(ref stop, 1);
                try { ping.Set(); } catch (Exception ex) { Diag.Fail("bench.pingpong.sig", ex); }
                try { if (!worker.Join(1000)) Diag.Hit("bench.pingpong.join"); }
                catch (Exception ex) { Diag.Fail("bench.pingpong.join", ex); }
                try { ping.Close(); pong.Close(); workerReady.Close(); }
                catch (Exception ex) { Diag.Fail("bench.pingpong.close", ex); }
            }

            totalSw.Stop();
            r.ElapsedMs = totalSw.ElapsedMilliseconds;
            if (string.IsNullOrEmpty(r.Note))
                r.Note = rounds + " 轮往返 P50=" + r.P50Us.ToString("0.0") + "µs P99=" + r.P99Us.ToString("0.0") + "µs";
            return r;
        }

        public static BenchRecord ToBenchRecord(MemoryBenchResult m)
        {
            BenchRecord rec = new BenchRecord();
            rec.Time = DateTime.Now.ToString("MM-dd HH:mm");
            rec.Mode = "mem";
            if (m != null)
            {
                rec.BandwidthMBs = m.SeqCopyMBs;
                rec.MemLatNs = m.MemLatNs;
                rec.Count = m.ChaseSteps;
                rec.Note = m.Note;
            }
            return rec;
        }

        public static BenchRecord ToBenchRecord(PingPongResult p)
        {
            BenchRecord rec = new BenchRecord();
            rec.Time = DateTime.Now.ToString("MM-dd HH:mm");
            rec.Mode = "pingpong";
            if (p != null)
            {
                rec.P50 = p.P50Us;
                rec.P99 = p.P99Us;
                rec.Max = p.MaxUs;
                rec.Count = p.Count;
                rec.Note = p.Note;
            }
            return rec;
        }

        private static double Percentile(double[] sorted, double q)
        {
            if (sorted == null || sorted.Length == 0) return 0;
            int idx = (int)Math.Ceiling(q * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }
    }
}
