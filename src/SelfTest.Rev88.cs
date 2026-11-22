// ============================================================================
//  SelfTest.Rev88.cs — Rev88 后端核心模块自检扩展
//  覆盖：双卡 LUID 遥测 / ReadAdapters / ReadDisplays / PhysicalOnlyMask /
//        ProcessPower(EcoQoS & Priority) / ProcessTable 同步 / NetTrace /
//        BenchSuite / BenchHistory 旧版兼容 / Actions Win11 电源动作探针。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Go
{
    public static partial class SelfTest
    {
        private static void TestHardwareAndGpuRev88()
        {
            List<Hardware.GpuAdapterInfo> adapters = Hardware.ReadAdapters();
            bool adaptersValid = adapters != null && adapters.Count >= 1;
            if (adaptersValid)
            {
                for (int i = 0; i < adapters.Count; i++)
                {
                    Hardware.GpuAdapterInfo a = adapters[i];
                    if (string.IsNullOrEmpty(a.DriverDesc) || string.IsNullOrEmpty(a.ShortName))
                    {
                        adaptersValid = false;
                        break;
                    }
                    _log.AppendLine("        GPU[" + i + "]: " + a.ShortName
                                    + " discrete=" + a.Discrete + " luid=" + a.Luid
                                    + " vramMB=" + (a.VramBytes / (1024 * 1024))
                                    + " drv=" + a.DriverVersion);
                }
            }
            Check(adaptersValid, "硬件 Rev88：ReadAdapters 返回有效物理显卡与驱动版本",
                  "count=" + (adapters == null ? -1 : adapters.Count));

            List<Hardware.DisplayInfo> displays = Hardware.ReadDisplays();
            bool hasValidDisplay = false;
            if (displays != null)
            {
                for (int i = 0; i < displays.Count; i++)
                {
                    Hardware.DisplayInfo d = displays[i];
                    if (d.Width >= 640 && d.Height >= 480 && d.RefreshHz > 0 && d.BitsPerPel >= 16)
                        hasValidDisplay = true;
                    _log.AppendLine("        Display[" + i + "]: " + d.DeviceName
                                    + " " + d.Name + " main=" + d.Main
                                    + " " + d.Width + "x" + d.Height + "@" + d.RefreshHz + "Hz " + d.BitsPerPel + "bit");
                }
            }
            Check(hasValidDisplay, "硬件 Rev88：无 WMI ReadDisplays 读取到有效显示器拓扑",
                  "count=" + (displays == null ? -1 : displays.Count));

            GpuSampler gs = new GpuSampler();
            try
            {
                gs.Start();
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2800)
                {
                    if (gs.Current.Available && gs.Current.AdapterCount > 0) break;
                    Thread.Sleep(150);
                }
                GpuSnapshot snap = gs.Current;
                bool luidOk = true;
                if (snap.Available)
                {
                    luidOk = snap.AdapterCount >= 1 && !string.IsNullOrEmpty(snap.Adapters[0].Luid)
                          && object.ReferenceEquals(snap.FindAdapter(snap.Adapters[0].Luid), snap.Adapters[0]);
                    for (int i = 0; i < snap.AdapterCount; i++)
                    {
                        GpuAdapterSample ga = snap.Adapters[i];
                        _log.AppendLine("        GpuSnap[" + i + "]: luid=" + ga.Luid
                                        + " name=" + ga.ShortName + " discrete=" + ga.Discrete
                                        + " 3d=" + ga.Load3D.ToString("0.0")
                                        + "% max=" + ga.MaxUtilPct.ToString("0.0")
                                        + "% vramMB=" + (ga.VramUsedBytes / (1024 * 1024)));
                    }
                }
                Check(snap.Adapters != null && snap.Adapters.Length == GpuSnapshot.MaxAdapters && luidOk,
                      "GPU Rev88：逐物理 LUID 采样三缓冲快照结构自洽",
                      "avail=" + snap.Available + " adapters=" + snap.AdapterCount + " engines=" + snap.Engines);
            }
            finally
            {
                gs.Stop();
            }
        }

        private static void TestAffinityAndProcessPowerRev88()
        {
            Topology topo = Topology.Detect();
            long physMask = Affinity.PhysicalOnlyMask(topo);
            long allMask = Affinity.AllMask();
            int bitCount = PopCount64(physMask);
            bool physOk = physMask > 0 && (physMask & allMask) == physMask
                && (topo.Logical <= 64 ? bitCount == topo.Cores : bitCount > 0);
            Check(physOk, "亲和度 Rev88：PhysicalOnlyMask 仅保留物理核主线程逻辑核",
                  "mask=0x" + physMask.ToString("X") + " bits=" + bitCount + " cores=" + topo.Cores);

            Process child = null;
            ProcessTable pt = null;
            try
            {
                child = new Process();
                child.StartInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
                child.StartInfo.Arguments = "-n 30 127.0.0.1";
                child.StartInfo.UseShellExecute = false;
                child.StartInfo.CreateNoWindow = true;
                child.StartInfo.RedirectStandardOutput = true;
                child.Start();
                int pid = child.Id;

                uint p0 = ProcessPower.ReadPriority(pid);
                string err;
                // 子进程会继承父进程（例如 CI runner / 任务计划）的初始优先级类（可能为 BelowNormal 0x4000）；
                // 仅当继承值碰巧已是 AboveNormal 时先不追踪地切回 Normal，随后验证“改写 -> 严格还原回真实原值 p0”。
                if (p0 == ProcessPower.PriorityAboveNormal)
                {
                    ProcessPower.SetPriority(pid, ProcessPower.PriorityNormal, false, out err);
                    p0 = ProcessPower.ReadPriority(pid);
                }
                bool setPrioOk = ProcessPower.SetPriority(pid, ProcessPower.PriorityAboveNormal, true, out err);
                uint p1 = ProcessPower.ReadPriority(pid);
                bool ecoSetOk = ProcessPower.SetEcoQoS(pid, true, true, out err);
                bool ecoState;
                bool ecoKnown = ProcessPower.ReadEcoQoS(pid, out ecoState);
                Check(p0 != 0 && p0 != ProcessPower.PriorityAboveNormal && setPrioOk && p1 == ProcessPower.PriorityAboveNormal,
                      "进程能效 Rev88：SetPriority 写入并回读校验",
                      "before=0x" + p0.ToString("X") + " after=0x" + p1.ToString("X") + " tracked=" + ProcessPower.TrackedCount);
                Check(ecoSetOk && ecoKnown && ecoState,
                      "进程能效 Rev88：SetEcoQoS 开启并回读校验",
                      "set=" + ecoSetOk + " known=" + ecoKnown + " state=" + ecoState + " err=" + (err ?? "-"));

                pt = new ProcessTable();
                ProcSnapshot snap = pt.Refresh();
                ProcEntry selfRow = null;
                ProcEntry childRow = null;
                int selfPid;
                using (Process self = Process.GetCurrentProcess()) selfPid = self.Id;
                for (int i = 0; i < snap.Count; i++)
                {
                    if (snap.Items[i] == null) continue;
                    if (snap.Items[i].Pid == selfPid) selfRow = snap.Items[i];
                    if (snap.Items[i].Pid == pid) childRow = snap.Items[i];
                }
                bool enriched = selfRow != null && pt.EnrichRow(selfRow) && selfRow.DetailsSynced && selfRow.AffinityMask != 0;
                bool rowUpdated = childRow != null && childRow.PriorityRank == 4 && childRow.PriorityLabel == "高于"
                    && pt.SetRowEcoQoS(pid, false, false, out err) && !childRow.EcoQoS;
                Check(enriched && rowUpdated,
                      "进程表 Rev88：缓存 PriorityRank/Label 与逐行控制双缓冲同步",
                      "selfRank=" + (selfRow == null ? -1 : selfRow.PriorityRank)
                      + " childLabel=" + (childRow == null ? "-" : childRow.PriorityLabel));

                int reverted = ProcessPower.RevertTracked();
                uint pReverted = ProcessPower.ReadPriority(pid);
                bool ecoReverted = true;
                ProcessPower.ReadEcoQoS(pid, out ecoReverted);
                Check(reverted >= 1 && pReverted == p0 && !ecoReverted && ProcessPower.TrackedCount == 0,
                      "进程能效 Rev88：RevertTracked 严格还原优先级与 EcoQoS 并清空追踪",
                      "reverted=" + reverted + " prio=0x" + pReverted.ToString("X") + " expect=0x" + p0.ToString("X") + " eco=" + ecoReverted);
            }
            finally
            {
                ProcessPower.RevertTracked();
                if (pt != null) pt.Dispose();
                if (child != null)
                {
                    try { if (!child.HasExited) child.Kill(); }
                    catch (Exception ex) { Diag.Fail("selftest.procpower.kill", ex); }
                    try { child.Dispose(); }
                    catch (Exception ex) { Diag.Fail("selftest.procpower.dispose", ex); }
                }
            }
        }

        private static void TestNetTraceRev88()
        {
            TcpListener srv = null;
            TcpClient cli = null;
            TcpClient accepted = null;
            try
            {
                srv = new TcpListener(IPAddress.Loopback, 0);
                srv.Start();
                int port = ((IPEndPoint)srv.LocalEndpoint).Port;
                cli = new TcpClient();
                cli.Connect(IPAddress.Loopback, port);
                accepted = srv.AcceptTcpClient();

                int selfPid;
                using (Process p = Process.GetCurrentProcess()) selfPid = p.Id;
                List<PidTcpSummary> tcp = NetTrace.ReadActiveTcpByPid();
                PidTcpSummary selfSum = null;
                for (int i = 0; i < tcp.Count; i++)
                {
                    if (tcp[i].Pid == selfPid) { selfSum = tcp[i]; break; }
                }
                Check(tcp.Count > 0 && selfSum != null && selfSum.Established >= 2,
                      "网络 Rev88：GetExtendedTcpTable 按 PID 聚合活跃 TCP 连接",
                      selfSum == null ? "self missing" : ("procs=" + tcp.Count + " selfEst=" + selfSum.Established + " remote=" + selfSum.TopRemote));

                List<VirtualNicInfo> nics = NetTrace.DetectVirtualNics();
                Check(nics != null, "网络 Rev88：TUN/VPN 虚拟网卡识别可枚举不崩溃",
                      "virtNics=" + (nics == null ? -1 : nics.Count));
                if (nics != null && nics.Count > 0)
                {
                    _log.AppendLine("        虚拟网卡：" + nics[0].Kind + " " + nics[0].Name + " up=" + nics[0].Up);
                }

                PathProbeResult lo = NetTrace.ProbeTarget("127.0.0.1", 2, 350, null);
                Check(lo.Reachable && lo.RttMs >= 0 && lo.RttMs < 100.0,
                      "网络 Rev88：真实探针测量 127.0.0.1 往返耗时",
                      "method=" + lo.Method + " rtt=" + lo.RttMs.ToString("0.00") + "ms");

                DualPathLatencyReport dp = NetTrace.ProbeDualPath(1, 350);
                bool dpSane = dp != null && !string.IsNullOrEmpty(dp.Diagnosis)
                    && (dp.Lan.Reachable ? dp.Lan.RttMs >= 0 : dp.Lan.RttMs == -1)
                    && (dp.Wan.Reachable ? dp.Wan.RttMs >= 0 : dp.Wan.RttMs == -1);
                Check(dpSane, "网络 Rev88：双路径 LAN/WAN 探针数值守恒（未达为 -1）",
                      dp == null ? "null" : ("gw=" + dp.GatewayIp + " lan=" + dp.Lan.RttMs.ToString("0.0") + "ms wan=" + dp.Wan.RttMs.ToString("0.0") + "ms :: " + dp.Diagnosis));
            }
            finally
            {
                if (accepted != null) try { accepted.Close(); } catch (Exception ex) { Diag.Fail("selftest.net.acc", ex); }
                if (cli != null) try { cli.Close(); } catch (Exception ex) { Diag.Fail("selftest.net.cli", ex); }
                if (srv != null) try { srv.Stop(); } catch (Exception ex) { Diag.Fail("selftest.net.srv", ex); }
            }
        }

        private static void TestBenchSuiteAndHistoryRev88()
        {
            MemoryBenchResult mb = BenchSuite.RunMemoryBench(2 * 1024 * 1024, 2 * 1024 * 1024, 50000);
            Check(mb.SeqCopyMBs > 0 && mb.MemLatNs > 0,
                  "基准 Rev88：MemoryBench 实测顺序带宽与随机追逐延迟",
                  mb.SeqCopyMBs.ToString("0") + " MB/s · " + mb.MemLatNs.ToString("0.0") + " ns");

            PingPongResult pp = BenchSuite.RunThreadPingPong(40);
            Check(pp.Count == 40 && pp.P50Us > 0 && pp.P99Us >= pp.P50Us && pp.MaxUs >= pp.P99Us,
                  "基准 Rev88：ThreadPingPong 实测线程往返唤醒 P50/P99/Max",
                  "p50=" + pp.P50Us.ToString("0.0") + "us p99=" + pp.P99Us.ToString("0.0") + "us max=" + pp.MaxUs.ToString("0.0") + "us");

            string dir = Path.Combine(Path.GetTempPath(), "GameOptimizer", "selftest-benchhist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string prevDir = BenchHistory.DirOverride;
            try
            {
                BenchHistory.DirOverride = dir;
                string legacy = "[{\"Time\":\"01-01 12:00\",\"P50\":11.5,\"P99\":25.0,\"Max\":48.0,\"Count\":100,\"TimerMs\":0.5}]";
                File.WriteAllText(BenchHistory.FilePath(), legacy, new UTF8Encoding(false));

                List<BenchRecord> loaded = BenchHistory.Load();
                bool legacyOk = loaded.Count == 1 && loaded[0].Mode == "wake" && Math.Abs(loaded[0].P50 - 11.5) < 0.01;
                BenchHistory.Append(BenchSuite.ToBenchRecord(mb));
                BenchHistory.Append(BenchSuite.ToBenchRecord(pp));
                List<BenchRecord> after = BenchHistory.Load();
                string csv = BenchHistory.ToCsv(after);
                bool csvOk = after.Count == 3 && csv.Contains("bandwidth_mbs") && csv.Contains(",mem,") && csv.Contains(",pingpong,");
                Check(legacyOk && csvOk, "基准 Rev88：BenchHistory 兼容旧版 JSON 并导出扩展 CSV",
                      "records=" + after.Count + " legacyMode=" + (loaded.Count > 0 ? loaded[0].Mode : "-"));
            }
            finally
            {
                BenchHistory.DirOverride = prevDir;
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception ex) { Diag.Fail("selftest.benchhist.cleanup", ex); }
            }
        }

        private static void TestActionsRev88()
        {
            List<ActionDef> all = Catalog.Build();
            string[] newIds = { "cpu-core-park", "pcie-aspm", "disk-idle-off" };
            int found = 0;
            bool probeOk = true;
            bool parkExpectOk = false;
            StringBuilder summary = new StringBuilder();
            List<Ops.OpJob> skipJobs = new List<Ops.OpJob>();
            for (int i = 0; i < newIds.Length; i++)
            {
                ActionDef a = null;
                for (int j = 0; j < all.Count; j++)
                {
                    if (all[j].Id == newIds[i]) { a = all[j]; break; }
                }
                if (a == null || a.Ops == null || a.Ops.Length != 1 || !a.NeedsAdmin)
                {
                    probeOk = false;
                    continue;
                }
                found++;
                string cur = Ops.Probe(a.Ops[0]);
                if (cur == Ops.ErrorText || cur == Ops.UnsupportedText || cur == Ops.ReadFailText || cur == Ops.Absent)
                    probeOk = false;
                if (a.Id == "cpu-core-park") parkExpectOk = Ops.Matches(a.Ops[0], "64");
                if (Ops.Matches(a.Ops[0], cur))
                    skipJobs.Add(new Ops.OpJob(a.Ops[0], a.Id, a.Name, 0));
                if (i > 0) summary.Append(" ");
                summary.Append(a.Id).Append("=").Append(cur);
            }
            bool batchOk = true;
            if (skipJobs.Count > 0)
            {
                Ledger l = new Ledger();
                Ops.BatchOutcome bo = Ops.ApplyBatch(skipJobs, l);
                batchOk = bo.Ok && bo.Applied == skipJobs.Count && bo.Results[0].Status == OpStatus.Skipped;
            }
            Check(found == 3 && probeOk && parkExpectOk && batchOk,
                  "目录 Rev88：Win11 电源计划新增动作可经 Ops.Probe 与 Ops.ApplyBatch 验证",
                  summary.ToString() + " matched=" + skipJobs.Count + " batch=" + batchOk);
        }

        private static void TestRev89Regression()
        {
            // 1. ProcessTable 双缓冲独立实例与无就地撕裂断言
            ProcessTable pt = null;
            try
            {
                pt = new ProcessTable();
                ProcSnapshot s1 = pt.Refresh();
                ProcSnapshot s2 = pt.Refresh();
                int selfPid;
                using (Process self = Process.GetCurrentProcess()) selfPid = self.Id;
                ProcEntry e1 = FindEntry(s1, selfPid);
                ProcEntry e2 = FindEntry(s2, selfPid);
                bool dblBufOk = s1.Ok && s2.Ok
                    && !object.ReferenceEquals(s1.Items, s2.Items)
                    && e1 != null && e2 != null
                    && !object.ReferenceEquals(e1, e2)
                    && object.ReferenceEquals(e1.Name, e2.Name);
                long savedCpu = e2 == null ? 0 : e2.CpuTicks;
                if (e2 != null) e2.CpuTicks = -999;
                ProcSnapshot s3 = pt.Refresh();
                ProcEntry e3 = FindEntry(s3, selfPid);
                if (e3 == null || !object.ReferenceEquals(e1, e3) || e3.CpuTicks < 0 || (e2 != null && e2.CpuTicks != -999))
                    dblBufOk = false;
                if (e2 != null) e2.CpuTicks = savedCpu;
                ProcSnapshot s4 = pt.Refresh();
                ProcEntry e4 = FindEntry(s4, selfPid);
                if (e4 == null || !object.ReferenceEquals(e2, e4) || e4.CpuTicks < 0)
                    dblBufOk = false;
                Check(dblBufOk, "进程表 Rev89：双缓冲各自持有独立 ProcEntry 实例且不互写",
                      "diffA_B=" + !object.ReferenceEquals(e1, e2) + " reuseA=" + object.ReferenceEquals(e1, e3) + " reuseB=" + object.ReferenceEquals(e2, e4));
            }
            finally
            {
                if (pt != null) pt.Dispose();
            }

            // 2. 混合架构单簇掩码与 Gpu LUID 回退名称断言
            Topology hyb = new Topology();
            hyb.Logical = 32;
            hyb.Cores = 24;
            hyb.Ccds = 1;
            hyb.Hybrid = true;
            Topology.FinalizeDerived(hyb);

            Topology hyb64 = new Topology();
            hyb64.Logical = 64;
            hyb64.Cores = 48;
            hyb64.Ccds = 1;
            hyb64.Hybrid = true;
            Topology.FinalizeDerived(hyb64);

            Topology dual = new Topology();
            dual.Logical = 32;
            dual.Cores = 16;
            dual.Ccds = 2;
            Topology.FinalizeDerived(dual);

            string luidFallback = GpuSnapshot.FormatFallbackLuidName("luid_0x00000000_0x00019BC5");
            Check(hyb.Ccd0Mask == 0xFFFFFFFFL && hyb64.Ccd0Mask == long.MaxValue && dual.Ccd0Mask == 0xFFFFL
                  && luidFallback == "虚拟/副显卡 (19BC5)",
                  "拓扑与GPU Rev89：单簇混合大小核 Ccd0Mask 覆盖全逻辑核 & LUID 友好回退名",
                  "24C32T=0x" + hyb.Ccd0Mask.ToString("X") + " 64T=0x" + hyb64.Ccd0Mask.ToString("X") + " luid=" + luidFallback);

            // 3. Perf.DrainInto 环绕追赶与 ResetJitterRing 对齐断言
            PerfSampler ps = new PerfSampler();
            for (int i = 0; i < 1500; i++) ps.RecordJitter(i);
            long cursor = 0;
            double[] drainBuf = new double[100];
            int d1 = ps.DrainInto(drainBuf, ref cursor);
            bool wrapOk = d1 == 100 && Math.Abs(drainBuf[0] - 300.0) < 0.001 && Math.Abs(drainBuf[99] - 399.0) < 0.001 && cursor == 400;
            int d2 = ps.DrainInto(drainBuf, ref cursor);
            bool nextOk = d2 == 100 && Math.Abs(drainBuf[0] - 400.0) < 0.001 && Math.Abs(drainBuf[99] - 499.0) < 0.001 && cursor == 500;
            ps.ResetJitterRing();
            for (int i = 0; i < 10; i++) ps.RecordJitter(2000.0 + i);
            long staleCursor = 100;
            int d3 = ps.DrainInto(drainBuf, ref staleCursor);
            bool resetOk = d3 == 10 && Math.Abs(drainBuf[0] - 2000.0) < 0.001 && Math.Abs(drainBuf[9] - 2009.0) < 0.001 && staleCursor == 1510;
            Check(wrapOk && nextOk && resetOk,
                  "采样器 Rev89：DrainInto 环绕与重置后游标先对齐最老样本不重放",
                  "wrap=" + wrapOk + " next=" + nextOk + " reset=" + resetOk);

            // 4. BenchHistory.ReadOnlyMode 不落盘也不隔离破坏原文件
            string bhDir = Path.Combine(Path.GetTempPath(), "GameOptimizer", "selftest-bhro-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bhDir);
            string prevBhDir = BenchHistory.DirOverride;
            bool prevRo = BenchHistory.ReadOnlyMode;
            try
            {
                BenchHistory.DirOverride = bhDir;
                BenchHistory.ReadOnlyMode = true;
                BenchHistory.Append(new BenchRecord { Time = "01-01 00:00", P50 = 12.3, Mode = "wake" });
                bool noWrite = !File.Exists(BenchHistory.FilePath());
                File.WriteAllText(BenchHistory.FilePath(), "{corrupt", new UTF8Encoding(false));
                List<BenchRecord> roLoaded = BenchHistory.Load();
                bool noQuarantine = roLoaded.Count == 0 && File.Exists(BenchHistory.FilePath())
                    && Directory.GetFiles(bhDir, "*.corrupt-*").Length == 0;
                Check(noWrite && noQuarantine,
                      "基准历史 Rev89：ReadOnlyMode 下禁止 Append 写盘与 Quarantine 破坏原文件",
                      "noWrite=" + noWrite + " noQuarantine=" + noQuarantine);
            }
            finally
            {
                BenchHistory.ReadOnlyMode = prevRo;
                BenchHistory.DirOverride = prevBhDir;
                try { if (Directory.Exists(bhDir)) Directory.Delete(bhDir, true); }
                catch (Exception ex) { Diag.Fail("selftest.bhro.cleanup", ex); }
            }

            // 5. NetTrace Tunnel(131) vs Wwanpp(243) 与 TopRemote ESTAB 覆盖断言
            string kTun, kWwan;
            bool isTun = NetTrace.IsVirtualTunType(131, "utun_test", "Tunnel Adapter", out kTun);
            bool isWwan = NetTrace.IsVirtualTunType(243, "Cellular", "Mobile Broadband Adapter", out kWwan);
            Dictionary<int, PidTcpSummary> tcpMap = new Dictionary<int, PidTcpSummary>();
            NetTrace.ApplyTcpRow(tcpMap, 11, 0x04030201u, 0x5000u, 9999, false, false);
            NetTrace.ApplyTcpRow(tcpMap, 3, 0x08070605u, 0xBB01u, 9999, false, false);
            NetTrace.ApplyTcpRow(tcpMap, 5, 0x0C0B0A09u, 0x8769u, 9999, false, false);
            NetTrace.ApplyTcpRow(tcpMap, 11, 0x100F0E0Du, 0x5000u, 9999, false, false);
            PidTcpSummary tcpSum = tcpMap[9999];
            Check(isTun && !isWwan && tcpSum.TopRemote == "9.10.11.12:27015"
                  && tcpSum.Established == 1 && tcpSum.SynWait == 1 && tcpSum.TimeWait == 2,
                  "网络 Rev89：Tunnel(131) 识别、排除 Wwanpp(243) 且 ESTAB 覆盖 TIME_WAIT",
                  "tun=" + isTun + " wwan=" + isWwan + " top=" + tcpSum.TopRemote);

            // 6. ProcessPower.SetEcoQoSIfMatch 防 PID 复用 & IsProtectedBackgroundName
            Process child = null;
            try
            {
                child = new Process();
                child.StartInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
                child.StartInfo.Arguments = "-n 20 127.0.0.1";
                child.StartInfo.UseShellExecute = false;
                child.StartInfo.CreateNoWindow = true;
                child.StartInfo.RedirectStandardOutput = true;
                child.Start();

                long realBorn = child.StartTime.ToUniversalTime().ToFileTimeUtc();
                string err;
                bool mismatchBlocked = !ProcessPower.SetEcoQoSIfMatch(child.Id, realBorn ^ 0x12345L, true, false, out err);
                bool ecoAfterMismatch = true;
                ProcessPower.ReadEcoQoS(child.Id, out ecoAfterMismatch);
                bool matchAllowed = ProcessPower.SetEcoQoSIfMatch(child.Id, realBorn, true, false, out err);
                bool ecoAfterMatch = false;
                ProcessPower.ReadEcoQoS(child.Id, out ecoAfterMatch);
                bool protOk = ProcessPower.IsProtectedBackgroundName("svchost.exe")
                    && ProcessPower.IsProtectedBackgroundName("GameOptimizer")
                    && !ProcessPower.IsProtectedBackgroundName("cs2.exe");
                Check(mismatchBlocked && !ecoAfterMismatch && matchAllowed && ecoAfterMatch && protOk,
                      "进程能效 Rev89：SetEcoQoSIfMatch 防 PID 复用与受保护进程识别",
                      "blocked=" + mismatchBlocked + " matched=" + matchAllowed + " prot=" + protOk);
            }
            finally
            {
                if (child != null)
                {
                    try { if (!child.HasExited) child.Kill(); }
                    catch (Exception ex) { Diag.Fail("selftest.r89.kill", ex); }
                    try { child.Dispose(); }
                    catch (Exception ex) { Diag.Fail("selftest.r89.dispose", ex); }
                }
            }

            // 7. TimerService 更细粒度降级、SampleClock 超长间隔与 Engine.ProbeForUi 异步缓存
            TimerService ts = new TimerService(ps, new AppState());
            ts.Request(TimerOwner.Overview, 4.0);
            bool tightened = ts.Request(TimerOwner.Daemon, 0.5) && Math.Abs(ts.RequestedMs - 0.5) < 0.001;
            ts.Release(TimerOwner.Daemon);
            bool relaxed = Math.Abs(ts.RequestedMs - 4.0) < 0.001;
            ts.Release(TimerOwner.Overview);

            bool clockSafe = false;
            using (SampleClock sc = new SampleClock())
            {
                Thread tWake = new Thread(delegate ()
                {
                    Thread.Sleep(60);
                    sc.Wake();
                });
                tWake.IsBackground = true;
                tWake.Start();
                clockSafe = sc.WaitTurn(3000000000L);
                tWake.Join(1000);
            }

            Engine eng = new Engine();
            ActionDef park = eng.Find("cpu-core-park");
            Ops.InvalidatePowerAcCache();
            Stopwatch swUi = Stopwatch.StartNew();
            string coldCur;
            eng.ProbeForUi(park, out coldCur);
            swUi.Stop();
            ManualResetEvent powerEv = new ManualResetEvent(false);
            eng.RefreshPowerAcCacheAsync(delegate { powerEv.Set(); });
            bool refreshed = powerEv.WaitOne(8000);
            powerEv.Close();
            string warmCur, syncCur;
            AState warmState = eng.ProbeForUi(park, out warmCur);
            AState syncState = eng.Probe(park, out syncCur);
            Check(tightened && relaxed && !ts.Active && clockSafe
                  && coldCur == Ops.PendingText && swUi.ElapsedMilliseconds < 100
                  && refreshed && warmState == syncState && warmCur == syncCur,
                  "内核时钟与电源缓存 Rev89：TimerService 精化、SampleClock 防溢出、ProbeForUi 非阻塞缓存",
                  "tight=" + tightened + " relax=" + relaxed + " coldMs=" + swUi.ElapsedMilliseconds + " refreshed=" + refreshed);

            TestRev90Fixes();
        }

        private static void TestRev90Fixes()
        {
            // 1. Power.ReadOnlyMode 阻断测试（保护 --render 不修改活动电源计划）
            bool prevPowerRo = Power.ReadOnlyMode;
            try
            {
                Power.ReadOnlyMode = true;
                bool setBlocked = !Power.SetActive(Power.Balanced, false);
                bool restoreBlocked = !Power.RestorePending("selftest-ro");
                Check(setBlocked && restoreBlocked,
                      "电源只读 Rev90：ReadOnlyMode 下拒绝切换方案与执行待恢复回滚",
                      "setBlocked=" + setBlocked + " restoreBlocked=" + restoreBlocked);
            }
            finally
            {
                Power.ReadOnlyMode = prevPowerRo;
            }

            // 2. Engine.AcceptEntry / AcceptAll（确认保留当前系统值，不回滚）
            string prevDir = Ledger.DirOverride;
            bool prevRo = Ledger.ReadOnlyMode;
            string root = Path.Combine(Path.GetTempPath(), "GameOptimizer", "selftest-r90-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                Ledger.DirOverride = root;
                Ledger.ReadOnlyMode = false;

                Engine eng = new Engine();
                LedgerEntry a1 = NewTestEntry("r90-a1");
                LedgerEntry a2 = NewTestEntry("r90-a2");
                LedgerEntry a3 = NewTestEntry("r90-a3");
                eng.Ledger.Add(a1);
                eng.Ledger.Add(a2);
                eng.Ledger.Add(a3);
                eng.Ledger.Save();

                ApplyReport repOne = eng.AcceptEntry(a1.Id);
                bool afterOne = repOne.Ok && repOne.Applied == 1 && eng.Ledger.Entries.Count == 2;
                ApplyReport repAll = eng.AcceptAll();
                Ledger reloaded = new Ledger();
                bool afterAll = repAll.Ok && repAll.Applied == 2 && eng.Ledger.Entries.Count == 0 && reloaded.Entries.Count == 0;
                Check(afterOne && afterAll,
                      "账本归档 Rev90：AcceptEntry / AcceptAll 正确归档待还原记录并持久化",
                      "afterOne=" + afterOne + " afterAll=" + afterAll);
            }
            catch (Exception ex)
            {
                Check(false, "账本归档 Rev90", ex.Message);
            }
            finally
            {
                Ledger.DirOverride = prevDir;
                Ledger.ReadOnlyMode = prevRo;
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception cx) { Diag.Fail("selftest.r90.cleanup", cx); }
            }

            // 3. Hardware.IsVirtualGpu 虚拟适配卡过滤
            bool virtOk = Hardware.IsVirtualGpu("MuMu Render Device")
                       && Hardware.IsVirtualGpu("Microsoft Basic Render Driver")
                       && !Hardware.IsVirtualGpu("NVIDIA GeForce RTX 4070")
                       && !Hardware.IsVirtualGpu("AMD Radeon(TM) Graphics");
            Check(virtOk, "显卡过滤 Rev90：精准识别 MuMu/虚拟渲染适配器且不误伤物理核显/独显", "ok=" + virtOk);

            // 4. Rev100 原生 powrprof AC 子项直读与只读保护 + 零句柄按名查 PID
            uint acMinCores;
            bool acReadOk = Power.ReadAcSetting("SUB_PROCESSOR", "CPMINCORES", out acMinCores);
            Op parkOp = new Op { Kind = "PowerAc", Path = "54533251-82be-4824-96c1-47b60b740d00", Name = "0cc5b647-c1df-4637-891a-dec35c318583" };
            string probeHex = Ops.Probe(parkOp);
            bool hexMatch = acReadOk && string.Equals(acMinCores.ToString("X"), probeHex, StringComparison.OrdinalIgnoreCase);
            bool prevRoAc = Power.ReadOnlyMode;
            bool acWriteBlocked = false;
            try
            {
                Power.ReadOnlyMode = true;
                acWriteBlocked = !Power.WriteAcSetting("SUB_PROCESSOR", "CPMINCORES", acMinCores);
            }
            finally { Power.ReadOnlyMode = prevRoAc; }
            Check(acReadOk && hexMatch && acWriteBlocked,
                  "原生电源子项 Rev100：powrprof 直读与 Ops.Probe 一致且 ReadOnlyMode 拦截写入",
                  "read=" + acReadOk + " val=0x" + acMinCores.ToString("X") + " probe=" + probeHex + " blocked=" + acWriteBlocked);

            int selfPid = Process.GetCurrentProcess().Id;
            int[] found = Go.Daemon.ProcessScan.FindPidsByName("GameOptimizer");
            bool foundSelf = Array.IndexOf(found, selfPid) >= 0;
            Check(foundSelf, "零句柄查进程 Rev100：ProcessScan.FindPidsByName 命中自身 PID",
                  "self=" + selfPid + " hits=" + found.Length);
        }

        private static ProcEntry FindEntry(ProcSnapshot s, int pid)
        {
            if (s == null || s.Items == null) return null;
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Items[i] != null && s.Items[i].Pid == pid) return s.Items[i];
            }
            return null;
        }

        private static int PopCount64(long x)
        {
            ulong u = unchecked((ulong)x);
            int c = 0;
            while (u != 0) { u &= (u - 1); c++; }
            return c;
        }
    }
}
