// ============================================================================
//  SelfTest.Rev130.cs — Rev 130 VANGUARD CONSOLE 后端遥测引擎与链路路由自检
// ============================================================================
using System;
using System.Collections.Generic;

namespace Go
{
    public static partial class SelfTest
    {
        private static void TestRev130Vanguard()
        {
            // 1) TelemetryEngine.Evaluate 四维健康评估：健康态 S 级 vs 降级/残留态
            Snapshot goodSnap = new Snapshot();
            goodSnap.CpuTotal = 0.12;
            goodSnap.MemTotalMB = 32768;
            goodSnap.MemAvailMB = 24576;
            goodSnap.CommitTotalMB = 10240;
            goodSnap.CommitLimitMB = 36864;
            goodSnap.JitterLive = true;
            goodSnap.JitterP99Us = 180;
            goodSnap.TimerMs = 0.5;
            goodSnap.DpcPercent = 0.3;

            SystemHealthScore hGood = TelemetryEngine.Evaluate(goodSnap, 28, 30, 0, false);
            Check(hGood.Grade == "S" && hGood.Composite >= 88 && hGood.SchedScore >= 95
                  && hGood.Bottleneck != null && hGood.Bottleneck.IndexOf("均衡健康", StringComparison.Ordinal) >= 0,
                  "Rev130 TelemetryEngine：低负载最优态准确评定为 S 级",
                  "grade=" + hGood.Grade + " score=" + hGood.Composite + " sched=" + hGood.SchedScore);

            Snapshot badSnap = new Snapshot();
            badSnap.CpuTotal = 0.85;
            badSnap.MemTotalMB = 32768;
            badSnap.MemAvailMB = 3768;
            badSnap.CommitTotalMB = 34000;
            badSnap.CommitLimitMB = 36864;
            badSnap.JitterLive = true;
            badSnap.JitterP99Us = 5500;
            badSnap.TimerMs = 15.6;
            badSnap.DpcPercent = 4.5;

            SystemHealthScore hBad = TelemetryEngine.Evaluate(badSnap, 10, 30, 2, true);
            SystemHealthScore hNull = TelemetryEngine.Evaluate(null, 0, 30, 0, false);
            Check(hBad.Grade != "S" && hBad.Composite < 58
                  && hBad.Bottleneck != null && hBad.Bottleneck.IndexOf("残留", StringComparison.Ordinal) >= 0
                  && hNull.Grade == "\u2014",
                  "Rev130 TelemetryEngine：高负载+限频+残留降级诊断与空快照兜底正确",
                  "bad=" + hBad.Grade + "/" + hBad.Composite + " null=" + hNull.Grade);

            // 2) TelemetryEngine.SummarizeCcds 双 CCD 热力聚合与最热核心定位
            Topology topo2 = new Topology();
            topo2.Ccds = 2;
            topo2.CoresPerCcd = 4;
            topo2.ThreadsPerCore = 2;
            topo2.Cores = 8;
            topo2.Logical = 16;
            double[] cores = new double[]
            {
                0.15, 0.20, 0.18, 0.22, 0.25, 0.16, 0.19, 0.21,
                0.65, 0.70, 0.72, 0.95, 0.68, 0.66, 0.74, 0.62
            };
            CcdHeatSummary ccd = TelemetryEngine.SummarizeCcds(cores, topo2);
            Check(ccd.HotCoreIndex == 11 && Math.Abs(ccd.HotCoreLoad - 0.95) < 0.001
                  && ccd.Ccd0Avg > 0.15 && ccd.Ccd0Avg < 0.25
                  && ccd.Ccd1Avg > 0.65 && Math.Abs(ccd.Ccd1Peak - 0.95) < 0.001,
                  "Rev130 TelemetryEngine：双 CCD 平均/峰值负载与最热逻辑核零分配统计准确",
                  "hot=#" + ccd.HotCoreIndex + "(" + ccd.HotCoreLoad.ToString("0.00") + ") ccd0=" + ccd.Ccd0Avg.ToString("0.00") + " ccd1=" + ccd.Ccd1Avg.ToString("0.00"));

            // 3) TelemetryEngine.ClassifyRoute 链路路由质量分级与 WAN-LAN 增量计算
            DualPathLatencyReport rFast = new DualPathLatencyReport();
            rFast.Lan.Reachable = true; rFast.Lan.RttMs = 0.8;
            rFast.Wan.Reachable = true; rFast.Wan.RttMs = 14.2;
            rFast.TunnelActive = false;
            NetRouteSummary sFast = TelemetryEngine.ClassifyRoute(rFast);

            DualPathLatencyReport rTun = new DualPathLatencyReport();
            rTun.Lan.Reachable = true; rTun.Lan.RttMs = 1.0;
            rTun.Wan.Reachable = true; rTun.Wan.RttMs = 38.0;
            rTun.TunnelActive = true;
            rTun.ActiveTunnelName = "Meta (Clash/Mihomo)";
            NetRouteSummary sTun = TelemetryEngine.ClassifyRoute(rTun);

            DualPathLatencyReport rIso = new DualPathLatencyReport();
            rIso.Lan.Reachable = true; rIso.Lan.RttMs = 1.2;
            rIso.Wan.Reachable = false; rIso.Wan.RttMs = -1;
            rIso.TunnelActive = true;
            NetRouteSummary sIso = TelemetryEngine.ClassifyRoute(rIso);

            Check(sFast.Grade == "极速直连" && Math.Abs(sFast.RoutePenaltyMs - 13.4) < 0.05
                  && sTun.Grade == "隧道活跃" && sTun.HopVerdict.IndexOf("Meta", StringComparison.Ordinal) >= 0
                  && sIso.Grade == "隧道隔离",
                  "Rev130 TelemetryEngine：直连 / TUN 隧道 / 隧道隔离三态路由分级与增量准确",
                  "fast=" + sFast.Grade + "(" + sFast.RoutePenaltyMs.ToString("0.0") + "ms) tun=" + sTun.Grade + " iso=" + sIso.Grade);

            // 4) NetTrace.ProbeDualPath 新增 RouteDeltaMs 与 ActiveTunnelName 字段自洽
            DualPathLatencyReport liveRep = NetTrace.ProbeDualPath();
            bool deltaValid = (liveRep.Lan.Reachable && liveRep.Wan.Reachable)
                ? (liveRep.RouteDeltaMs >= 0)
                : (liveRep.RouteDeltaMs < 0);
            bool tunNameValid = !liveRep.TunnelActive || !string.IsNullOrEmpty(liveRep.ActiveTunnelName);
            Check(liveRep != null && deltaValid && tunNameValid,
                  "Rev130 NetTrace：双路径探针 RouteDeltaMs 与 ActiveTunnelName 字段自洽",
                  "tun=" + liveRep.TunnelActive + " name=" + (liveRep.ActiveTunnelName ?? "") + " delta=" + liveRep.RouteDeltaMs.ToString("0.0"));
        }
    }
}
