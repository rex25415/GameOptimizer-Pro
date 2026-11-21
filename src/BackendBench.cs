// ============================================================================
//  BackendBench.cs — 后端关键路径基准（--backend-bench <outFile>）
//
//  为什么要有它：重构前后的"更快了"不能靠感觉。这里把五条会被真实调用链
//  反复走到的路径做成可复现的微基准，数字直接进报告：
//    · 进程表扫描（NtQuery 一次遍历）        ← 取代 Process.GetProcesses
//    · 旧式进程枚举（对照，含每进程 3 次 syscall）
//    · 注册表读取（探针热路径）
//    · 账本落盘（一次 fsync + Replace 的真实成本）
//    · 单动作应用（探测 → 记账 → 写入 → 校验的全链路）
//    · 状态总线同步广播
//
//  约束：只在临时沙箱账本与 HKCU\Software\GameOptimizer\Bench 上操作，
//  结束删除；绝不触碰用户真实账本。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Go.Daemon;

namespace Go
{
    public static class BackendBench
    {
        private static StringBuilder _log;
        private static int _fail;

        public static int Run(string outFile)
        {
            _log = new StringBuilder();
            _fail = 0;
            Line("GameOptimizer Rev" + Brand.Rev + " backend bench  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("cpus=" + Environment.ProcessorCount + "  x64=" + (IntPtr.Size == 8));
            Line("");

            string prevLedger = Ledger.DirOverride;
            try
            {
                Ledger.UseSandboxDir();
                BenchScan();
                BenchProcessTable();
                BenchLegacyEnum();
                BenchRegistry();
                BenchLedgerSave();
                BenchApply();
                BenchBus();
                BenchRev88();
            }
            catch (Exception ex)
            {
                _fail++;
                Line("[FAIL] 基准未完成: " + ex);
                Diag.Fail("bench", ex);
            }
            finally
            {
                Ledger.DirOverride = prevLedger;
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GameOptimizer\Bench", false); }
                catch (Exception ex) { Diag.Fail("bench.cleanup.key", ex); }
            }

            Line("");
            Line("RESULT  fail=" + _fail);
            string text = _log.ToString();
            try
            {
                File.WriteAllText(outFile, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Diag.Fail("bench.output", ex);
                Console.WriteLine("[FAIL] 报告无法写入 " + outFile + "：" + ex.Message);
                _fail++;
            }
            Console.WriteLine(text);
            return _fail == 0 ? 0 : 1;
        }

        // ====================================================================
        //  各段
        // ====================================================================
        private static void BenchScan()
        {
            ProcessScan scan = new ProcessScan();
            try
            {
                scan.ForEachRow(delegate (ProcessRow row) { return false; });   // 预热
                int rows = 0;
                double[] us = Measure(25, delegate
                {
                    rows = 0;
                    scan.ForEachRow(delegate (ProcessRow row) { rows++; return false; });
                });
                Report("proc.scan.ntquery", us, "rows=" + rows + " cap=" + scan.Capacity
                       + " growths=" + scan.Growths + " failures=" + scan.Failures);
            }
            finally { scan.Dispose(); }
        }

        private static void BenchProcessTable()
        {
            ProcessTable t = new ProcessTable();
            try
            {
                t.Refresh();   // 预热 A/B 双缓冲（含首次槽位与名字缓存填充）
                t.Refresh();
                long alloc0 = GC.GetAllocatedBytesForCurrentThread();
                double[] us = Measure(20, delegate { t.Refresh(); });
                long alloc1 = GC.GetAllocatedBytesForCurrentThread();
                ProcSnapshot warm = t.Get(60000);   // 复用已发布数据
                double[] cached = Measure(2000, delegate { t.Get(60000); });
                Report("proc.table.refresh", us,
                       "count=" + warm.Count + " alloc=" + ((alloc1 - alloc0) / 20) + " B/refresh");
                Report("proc.table.cached", cached, "age<=" + warm.AgeMs + "ms");
            }
            finally { t.Dispose(); }
        }

        /// <summary>对照：旧实现每 2 秒真的走的那条路。</summary>
        private static void BenchLegacyEnum()
        {
            int n = 0;
            double[] us = Measure(3, delegate
            {
                n = 0;
                Process[] all = Process.GetProcesses();
                for (int i = 0; i < all.Length; i++)
                {
                    Process p = all[i];
                    try
                    {
                        string name = p.ProcessName;
                        long ws = p.WorkingSet64;
                        TimeSpan cpu = p.TotalProcessorTime;
                        if (name != null && ws >= 0 && cpu.Ticks >= 0) n++;
                    }
                    catch (Exception) { /* 进程在枚举与读取之间退出：这是旧实现的常态 */ }
                    finally { try { p.Dispose(); } catch (Exception) { /* 句柄已失效 */ } }
                }
            });
            Report("proc.enum.legacy", us, "rows=" + n + " (Process.GetProcesses + 3 syscalls/row)");
        }

        private static void BenchRegistry()
        {
            const string path = @"Software\GameOptimizer\Bench";
            try
            {
                Reg.Write("HKCU", path, "Probe", "12345", "DWord");
                Reg.Read("HKCU", path, "Probe");   // 预热
                double[] us = Measure(2000, delegate { Reg.Read("HKCU", path, "Probe"); });
                Report("reg.read", us, "hit");
                Reg.Delete("HKCU", path, "Probe");
                Reg.Read("HKCU", path, "Probe");   // 负缓存路径也一样热
                double[] miss = Measure(1000, delegate { Reg.Read("HKCU", path, "Probe"); });
                Report("reg.read.miss", miss, "value absent (ERROR_FILE_NOT_FOUND 路径)");
            }
            finally
            {
                try { Reg.Delete("HKCU", path, "Probe"); }
                catch (Exception ex) { Diag.Fail("bench.cleanup", ex); }
            }
        }

        private static void BenchLedgerSave()
        {
            Ledger l = new Ledger();
            for (int i = 0; i < 8; i++)
            {
                LedgerEntry e = new LedgerEntry();
                e.Id = "bench-" + i + "-" + Guid.NewGuid().ToString("N");
                e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                e.ActionId = "bench";
                e.ActionName = "基准";
                e.Kind = "Reg";
                e.Hive = "HKCU";
                e.Path = @"Software\GameOptimizer\Bench";
                e.Name = "v" + i;
                e.Existed = false;
                l.Add(e);
            }
            l.Save();   // 预热
            double[] us = Measure(15, delegate { l.Save(); });
            Report("ledger.save", us, "8 entries, FlushFileBuffers + File.Replace");
        }

        private static void BenchApply()
        {
            const string path = @"Software\GameOptimizer\Bench";
            Engine eng = new Engine();
            try
            {
                ActionDef a = new ActionDef();
                a.Id = "bench-apply";
                a.Name = "基准应用";
                a.Group = "bench";
                a.Ops = new Op[] { new Op { Kind = "Reg", Hive = "HKCU", Path = path, Name = "Applied", Type = "DWord", Value = "1" } };
                eng.Apply(a);
                eng.RevertAction(a.Id);
                double[] us = Measure(12, delegate
                {
                    eng.Apply(a);
                    eng.RevertAction(a.Id);
                });
                Report("action.apply+revert", us, "probe + journal fsync + write + verify + revert fsync");
            }
            finally
            {
                try { Reg.Delete("HKCU", path, "Applied"); }
                catch (Exception ex) { Diag.Fail("bench.cleanup", ex); }
            }
        }

        private static void BenchBus()
        {
            AppState s = new AppState();
            int hits = 0;
            s.On(Dirty.Timer, delegate (Dirty d) { hits++; });
            const int N = 200000;
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) s.Raise(Dirty.Timer);
            sw.Stop();
            double ns = sw.Elapsed.TotalMilliseconds * 1000000.0 / N;
            Line("bus.raise  n=" + N + "  avg=" + ns.ToString("0.0") + " ns/op  hits=" + hits);
            Line("");
        }

        private static void BenchRev88()
        {
            MemoryBenchResult mb = BenchSuite.RunMemoryBench(16 * 1024 * 1024, 16 * 1024 * 1024, 200000);
            Line("mem.bench".PadRight(24) + " copy=" + mb.SeqCopyMBs.ToString("0") + " MB/s"
                 + "  chase=" + mb.MemLatNs.ToString("0.0") + " ns"
                 + "  (" + mb.ElapsedMs + "ms)");

            PingPongResult pp = BenchSuite.RunThreadPingPong(200);
            Line("thread.pingpong".PadRight(24) + " n=" + pp.Count
                 + " p50=" + pp.P50Us.ToString("0.0") + "us"
                 + "  p99=" + pp.P99Us.ToString("0.0") + "us"
                 + "  max=" + pp.MaxUs.ToString("0.0") + "us");

            int rows = 0;
            NetTrace.ReadActiveTcpByPid();
            double[] tcpUs = Measure(25, delegate { rows = NetTrace.ReadActiveTcpByPid().Count; });
            Report("net.tcp.pid", tcpUs, "activePids=" + rows);

            uint acIdx = 0;
            Power.ReadAcSetting("SUB_PROCESSOR", "CPMINCORES", out acIdx);
            double[] pwrUs = Measure(100, delegate { Power.ReadAcSetting("SUB_PROCESSOR", "CPMINCORES", out acIdx); });
            Report("power.ac.native", pwrUs, "val=0x" + acIdx.ToString("X"));

            int[] selfMatches = Go.Daemon.ProcessScan.FindPidsByName("GameOptimizer");
            double[] findUs = Measure(20, delegate { Go.Daemon.ProcessScan.FindPidsByName("GameOptimizer"); });
            Report("proc.find.name", findUs, "hits=" + selfMatches.Length);

            // Rev140 原生单句柄微基准（替代原 Process.GetProcessById ~13.8ms 全表扫描）
            int selfPid = Process.GetCurrentProcess().Id;
            long selfBorn = Go.Daemon.SessionStore.SelfBorn();
            Topology topo = Topology.Detect();
            Affinity.Read(selfPid);
            double[] affUs = Measure(200, delegate { Affinity.Read(selfPid); });
            Report("affinity.read.native", affUs, "mask=0x" + Affinity.Read(selfPid).ToString("X"));

            Affinity.PhysicalOnlyMask(topo);
            double[] physUs = Measure(500, delegate { Affinity.PhysicalOnlyMask(topo); });
            Report("affinity.phys.cached", physUs, "mask=0x" + Affinity.PhysicalOnlyMask(topo).ToString("X"));

            ProcName.IsAlive(selfPid, selfBorn);
            double[] aliveUs = Measure(200, delegate { ProcName.IsAlive(selfPid, selfBorn); });
            Report("proc.alive.native", aliveUs, "pid=" + selfPid);

            ProcName.ReadPrivateBytes(selfPid);
            double[] privUs = Measure(200, delegate { ProcName.ReadPrivateBytes(selfPid); });
            Report("proc.private.native", privUs, "MB=" + (ProcName.ReadPrivateBytes(selfPid) / 1048576));
            Line("");
        }

        // ====================================================================
        //  工具
        // ====================================================================
        private static double[] Measure(int n, Action a)
        {
            double[] us = new double[n];
            for (int i = 0; i < n; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                a();
                long t1 = Stopwatch.GetTimestamp();
                us[i] = (t1 - t0) * 1000000.0 / Stopwatch.Frequency;
            }
            return us;
        }

        private static void Report(string name, double[] us, string note)
        {
            if (us == null || us.Length == 0) return;
            double[] sorted = (double[])us.Clone();
            Array.Sort(sorted);
            double sum = 0;
            for (int i = 0; i < sorted.Length; i++) sum += sorted[i];
            double avg = sum / sorted.Length;
            double p50 = sorted[sorted.Length / 2];
            int p99i = (int)Math.Ceiling(sorted.Length * 0.99) - 1;
            if (p99i < 0) p99i = 0;
            if (p99i >= sorted.Length) p99i = sorted.Length - 1;
            double p99 = sorted[p99i];
            Line(name.PadRight(24) + " n=" + sorted.Length.ToString().PadRight(4)
                 + " avg=" + Fmt(avg) + "  p50=" + Fmt(p50) + "  p99=" + Fmt(p99)
                 + (string.IsNullOrEmpty(note) ? "" : "  :: " + note));
        }

        private static string Fmt(double us)
        {
            if (us >= 1000) return (us / 1000.0).ToString("0.00") + "ms";
            return us.ToString("0.0") + "us";
        }

        private static void Line(string s)
        {
            _log.AppendLine(s);
        }
    }
}
