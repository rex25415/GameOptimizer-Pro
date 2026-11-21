// ============================================================================
//  Telemetry.Engine.cs — Rev130 后端实时四维系统健康评估 / CCD 热力聚合 / 网络链路分级
//  全部纯值类型（struct）与零堆分配算术路径，供总览驾驶舱、调度舞台与网络拓扑台共享。
// ============================================================================
using System;
using System.Collections.Generic;

namespace Go
{
    /// <summary>四维实时系统健康评分（0-100，零分配值类型）。</summary>
    public struct SystemHealthScore
    {
        public int SchedScore;    // 调度与唤醒（抖动 + 定时器颗粒度 + DPC）
        public int CpuScore;      // 处理器余裕（负载 + 限频惩罚）
        public int MemScore;      // 内存余裕（物理占比 + 提交压力）
        public int TuneScore;     // 调优与洁净度（已优化覆盖 + 第三方残留扣分）
        public int Composite;     // 综合评分
        public string Grade;      // S / A / B / C
        public uint GradeTint;
        public string Bottleneck; // 当前首要制约因素（纯中文实话）
    }

    /// <summary>CCD 级热力与峰值核心统计（零分配值类型）。</summary>
    public struct CcdHeatSummary
    {
        public double Ccd0Avg;
        public double Ccd0Peak;
        public double Ccd1Avg;
        public double Ccd1Peak;
        public int HotCoreIndex;
        public double HotCoreLoad;
    }

    /// <summary>网络链路路由质量分类结果。</summary>
    public struct NetRouteSummary
    {
        public string Grade;          // 极速 / 良好 / 偏高 / 隧道 / 离线
        public uint Tint;
        public double RoutePenaltyMs; // WAN - LAN 路由增量（不可算时 < 0）
        public string HopVerdict;     // 节点诊断简语
    }

    public static class TelemetryEngine
    {
        /// <summary>
        /// 计算四维系统态势评分（透明可验证，绝不虚构硬件读数）。
        /// </summary>
        public static SystemHealthScore Evaluate(
            Snapshot s,
            int tunedApplied,
            int tunedTotal,
            int remPending,
            bool cpuThrottled)
        {
            SystemHealthScore h = new SystemHealthScore();
            if (s == null)
            {
                h.Grade = "\u2014";
                h.GradeTint = T.Mute;
                h.Bottleneck = "等待首次遥测采样";
                return h;
            }

            // 1. 调度与唤醒（抖动 P99 + 定时器颗粒度 + DPC 占比）
            double sched = 100.0;
            if (s.JitterLive && s.JitterP99Us > 0)
            {
                double jp = Math.Min(42.0, s.JitterP99Us / 120.0);
                sched -= jp;
            }
            if (s.TimerMs > 4.0) sched -= 18.0;
            else if (s.TimerMs > 1.5) sched -= 6.0;
            if (s.DpcPercent > 2.0) sched -= Math.Min(25.0, (s.DpcPercent - 2.0) * 6.0);
            h.SchedScore = Clamp100(sched);

            // 2. 处理器余裕（整机占用 + 限频扣分）
            double cpu = 100.0;
            double cpuPct = s.CpuTotal * 100.0;
            if (cpuPct > 25.0) cpu -= (cpuPct - 25.0) * 1.15;
            if (cpuThrottled) cpu -= 16.0;
            h.CpuScore = Clamp100(cpu);

            // 3. 内存余裕（物理占比 + 提交压力）
            double mem = 100.0;
            double usedPct = s.MemTotalMB > 0 ? (s.MemUsedMB / s.MemTotalMB * 100.0) : 0;
            double commitPct = s.CommitLimitMB > 0 ? (s.CommitTotalMB / s.CommitLimitMB * 100.0) : 0;
            if (usedPct > 50.0) mem -= (usedPct - 50.0) * 1.35;
            if (commitPct > 75.0) mem -= (commitPct - 75.0) * 1.2;
            h.MemScore = Clamp100(mem);

            // 4. 调优与洁净度（覆盖率 + 第三方残留扣分）
            double tune = tunedTotal > 0 ? (tunedApplied * 100.0 / tunedTotal) : 100.0;
            if (remPending > 0) tune -= remPending * 12.0;
            h.TuneScore = Clamp100(tune);

            h.Composite = Clamp100(
                h.SchedScore * 0.34 +
                h.CpuScore * 0.24 +
                h.MemScore * 0.22 +
                h.TuneScore * 0.20);

            if (h.Composite >= 88) { h.Grade = "S"; h.GradeTint = T.Ok; }
            else if (h.Composite >= 74) { h.Grade = "A"; h.GradeTint = T.Accent; }
            else if (h.Composite >= 58) { h.Grade = "B"; h.GradeTint = T.Warn; }
            else { h.Grade = "C"; h.GradeTint = T.Bad; }

            // 诚实指出当前最低维度的制约点
            if (remPending > 0)
                h.Bottleneck = "检测到 " + remPending + " 项第三方负优化残留，建议立即一键修复";
            else if (cpuThrottled)
                h.Bottleneck = "CPU 当前处于功耗/温控限频状态，请检查供电与散热档位";
            else if (h.SchedScore <= h.CpuScore && h.SchedScore <= h.MemScore && s.JitterLive && s.JitterP99Us > 2000)
                h.Bottleneck = "默认系统唤醒颗粒度较高（P99 " + ((int)Math.Round(s.JitterP99Us)) + " µs），游戏时建议启用 0.5ms";
            else if (h.MemScore <= h.CpuScore && h.MemScore <= h.SchedScore && s.MemStandbyMB >= 4096)
                h.Bottleneck = "内存占用偏高（可回收待机页 " + (s.MemStandbyMB / 1024.0).ToString("0.1") + " GB）";
            else if (h.CpuScore < 70)
                h.Bottleneck = "后台 CPU 负载偏高（" + ((int)Math.Round(cpuPct)) + "%），建议在进程页压制高占后台";
            else
                h.Bottleneck = "四维核心指标均衡健康 · 随时可进入低延迟竞技状态";

            return h;
        }

        /// <summary>零分配统计双 CCD 平均/峰值负载与最热逻辑核。</summary>
        public static CcdHeatSummary SummarizeCcds(double[] cores, Topology topo)
        {
            CcdHeatSummary r = new CcdHeatSummary();
            r.HotCoreIndex = -1;
            if (cores == null || cores.Length == 0) return r;

            int perCcd = (topo != null && topo.Ccds > 1 && topo.CoresPerCcd > 0 && topo.ThreadsPerCore > 0)
                ? (topo.CoresPerCcd * topo.ThreadsPerCore)
                : cores.Length;
            if (perCcd <= 0 || perCcd > cores.Length) perCcd = cores.Length;

            double s0 = 0, p0 = 0, s1 = 0, p1 = 0;
            int n0 = 0, n1 = 0;
            for (int i = 0; i < cores.Length; i++)
            {
                double v = cores[i];
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                if (v > r.HotCoreLoad || r.HotCoreIndex < 0)
                {
                    r.HotCoreLoad = v;
                    r.HotCoreIndex = i;
                }
                if (i < perCcd)
                {
                    s0 += v;
                    if (v > p0) p0 = v;
                    n0++;
                }
                else
                {
                    s1 += v;
                    if (v > p1) p1 = v;
                    n1++;
                }
            }
            r.Ccd0Avg = n0 > 0 ? s0 / n0 : 0;
            r.Ccd0Peak = p0;
            r.Ccd1Avg = n1 > 0 ? s1 / n1 : 0;
            r.Ccd1Peak = p1;
            return r;
        }

        /// <summary>根据双路径探针结果计算链路拓扑质量评级与公网附加跳延迟。</summary>
        public static NetRouteSummary ClassifyRoute(DualPathLatencyReport rep)
        {
            NetRouteSummary s = new NetRouteSummary();
            s.RoutePenaltyMs = -1;
            if (rep == null)
            {
                s.Grade = "待测";
                s.Tint = T.Mute;
                s.HopVerdict = "双路径探针尚未完成首轮采样";
                return s;
            }
            if (rep.Lan != null && rep.Wan != null && rep.Lan.Reachable && rep.Wan.Reachable)
                s.RoutePenaltyMs = Math.Max(0.0, rep.Wan.RttMs - rep.Lan.RttMs);

            if (rep.Wan == null || !rep.Wan.Reachable)
            {
                s.Grade = rep.TunnelActive ? "隧道隔离" : "离线";
                s.Tint = T.Warn;
                s.HopVerdict = rep.TunnelActive
                    ? "检测到虚拟 TUN 接管流量 · 公网直连探针未响应"
                    : "公网探针无响应 · 请检查上行链路与防火墙";
                return s;
            }

            if (rep.Lan != null && rep.Lan.Reachable && rep.Lan.RttMs >= 15.0)
            {
                s.Grade = "内网偏高";
                s.Tint = T.Warn;
                s.HopVerdict = "网关往返已达 " + rep.Lan.RttMs.ToString("0.0") + " ms，瓶颈在本地无线/路由器段";
                return s;
            }

            if (rep.TunnelActive)
            {
                s.Grade = "隧道活跃";
                s.Tint = T.Accent;
                s.HopVerdict = "当前经过 " + (string.IsNullOrEmpty(rep.ActiveTunnelName) ? "TUN/VPN" : rep.ActiveTunnelName)
                    + " · WAN " + rep.Wan.RttMs.ToString("0.0") + " ms";
                return s;
            }

            if (rep.Wan.RttMs <= 25.0)
            {
                s.Grade = "极速直连";
                s.Tint = T.Ok;
                s.HopVerdict = "物理直连低延迟 · 适合高对抗竞技";
            }
            else if (rep.Wan.RttMs <= 60.0)
            {
                s.Grade = "链路良好";
                s.Tint = T.Accent;
                s.HopVerdict = "网关与外网延迟均处于常规畅玩区间";
            }
            else
            {
                s.Grade = "公网偏高";
                s.Tint = T.Warn;
                s.HopVerdict = "外网段附加延迟 " + (s.RoutePenaltyMs >= 0 ? s.RoutePenaltyMs.ToString("0.0") : rep.Wan.RttMs.ToString("0.0")) + " ms";
            }
            return s;
        }

        private static int Clamp100(double v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return (int)Math.Round(v);
        }
    }
}
