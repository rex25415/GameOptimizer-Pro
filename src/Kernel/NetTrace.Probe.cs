// ============================================================================
//  NetTrace.Probe.cs — 双路径并行延迟探针（LAN 网关 vs WAN 公网 DNS）
//  Rev140 从 NetTrace.cs 按职责分片，并将 LAN / WAN 探测由串行改为并行执行。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace Go
{
    public static partial class NetTrace
    {
        public static DualPathLatencyReport ProbeDualPath()
        {
            return ProbeDualPath(2, 450);
        }

        public static DualPathLatencyReport ProbeDualPath(int samples, int timeoutMs)
        {
            if (samples < 1) samples = 1;
            if (samples > 5) samples = 5;
            if (timeoutMs < 150) timeoutMs = 150;

            DualPathLatencyReport rep = new DualPathLatencyReport();
            // 单次枚举全部网卡供虚拟隧道识别与默认网关发现共享
            NetworkInterface[] allNics = null;
            try { allNics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) { Diag.Fail("nettrace.nics", ex); }

            rep.VirtualNics = DetectVirtualNicsFrom(allNics);
            for (int i = 0; i < rep.VirtualNics.Count; i++)
            {
                if (rep.VirtualNics[i].Up)
                {
                    rep.TunnelActive = true;
                    rep.ActiveTunnelName = !string.IsNullOrEmpty(rep.VirtualNics[i].Kind)
                        ? rep.VirtualNics[i].Kind
                        : rep.VirtualNics[i].Name;
                    break;
                }
            }

            string gw, gwNic;
            bool hasGw = FindDefaultGatewayFrom(allNics, out gw, out gwNic);
            if (hasGw)
            {
                rep.GatewayIp = gw;
                rep.GatewayInterface = gwNic;
            }
            else
            {
                rep.Lan = new PathProbeResult();
                rep.Lan.Target = "";
                rep.Lan.Error = "未发现可用默认网关";
            }

            // Rev140：LAN 与 WAN 双路并行探测，将最坏等待耗时减半
            PathProbeResult wan = null;
            ManualResetEvent wanDone = new ManualResetEvent(false);
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        PathProbeResult best = null;
                        for (int i = 0; i < WanDnsTargets.Length; i++)
                        {
                            PathProbeResult cand = ProbeTarget(WanDnsTargets[i], samples, timeoutMs, new int[] { 53, 443 });
                            if (best == null || (!best.Reachable && cand.Reachable)) best = cand;
                            if (best.Reachable) break;
                        }
                        wan = best;
                    }
                    catch (Exception ex) { Diag.Fail("nettrace.probe.wan", ex); }
                    finally
                    {
                        try { wanDone.Set(); }
                        catch (ObjectDisposedException) { /* 等待已超时释放 */ }
                    }
                });

                if (hasGw)
                    rep.Lan = ProbeTarget(gw, samples, timeoutMs, new int[] { 80, 443, 53 });

                int waitBudget = Math.Max(1200, timeoutMs * samples * 4);
                wanDone.WaitOne(waitBudget);
            }
            finally
            {
                wanDone.Close();
            }

            rep.Wan = wan ?? new PathProbeResult();
            if (rep.Lan != null && rep.Wan != null && rep.Lan.Reachable && rep.Wan.Reachable)
                rep.RouteDeltaMs = Math.Max(0.0, rep.Wan.RttMs - rep.Lan.RttMs);
            rep.Diagnosis = BuildDiagnosis(rep);
            return rep;
        }

        public static void ProbeDualPathAsync(int samples, int timeoutMs, Action<DualPathLatencyReport> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                DualPathLatencyReport rep;
                try { rep = ProbeDualPath(samples, timeoutMs); }
                catch (Exception ex)
                {
                    Diag.Fail("nettrace.probe.async", ex);
                    rep = new DualPathLatencyReport();
                    rep.Diagnosis = ex.Message;
                }
                if (done != null)
                {
                    try { done(rep); }
                    catch (Exception dex) { Diag.Fail("nettrace.probe.done", dex); }
                }
            });
        }

        public static PathProbeResult ProbeTarget(string host, int samples, int timeoutMs, int[] tcpFallbackPorts)
        {
            PathProbeResult r = new PathProbeResult();
            r.Target = host ?? "";
            if (string.IsNullOrEmpty(host))
            {
                r.Error = "目标地址为空";
                return r;
            }

            List<double> rtts = new List<double>(samples);
            string lastErr = "";
            using (Ping ping = new Ping())
            {
                for (int i = 0; i < samples; i++)
                {
                    r.Sent++;
                    try
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        PingReply reply = ping.Send(host, timeoutMs);
                        sw.Stop();
                        if (reply != null && reply.Status == IPStatus.Success)
                        {
                            double ms = reply.RoundtripTime > 0 ? reply.RoundtripTime : Math.Max(0.1, sw.Elapsed.TotalMilliseconds);
                            rtts.Add(ms);
                        }
                        else
                        {
                            lastErr = reply == null ? "Ping 无响应" : reply.Status.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        Diag.Hit("nettrace.icmp");
                        lastErr = ex.Message;
                    }
                }
            }

            if (rtts.Count > 0)
            {
                FillStats(r, "ICMP", rtts);
                return r;
            }

            if (tcpFallbackPorts != null && tcpFallbackPorts.Length > 0)
            {
                for (int p = 0; p < tcpFallbackPorts.Length; p++)
                {
                    int port = tcpFallbackPorts[p];
                    List<double> tcpRtts = new List<double>(samples);
                    int tcpSent = 0;
                    for (int i = 0; i < samples; i++)
                    {
                        tcpSent++;
                        double ms;
                        string tcpErr;
                        if (ProbeTcpPort(host, port, timeoutMs, out ms, out tcpErr))
                            tcpRtts.Add(ms);
                        else if (!string.IsNullOrEmpty(tcpErr))
                            lastErr = tcpErr;
                    }
                    if (tcpRtts.Count > 0)
                    {
                        r.Sent = tcpSent;
                        FillStats(r, "TCP:" + port, tcpRtts);
                        return r;
                    }
                }
            }

            r.Reachable = false;
            r.RttMs = -1;
            r.MinMs = -1;
            r.MaxMs = -1;
            r.Error = string.IsNullOrEmpty(lastErr) ? "请求超时" : lastErr;
            return r;
        }

        private static bool ProbeTcpPort(string host, int port, int timeoutMs, out double ms, out string err)
        {
            ms = -1;
            err = "";
            Socket s = null;
            WaitHandle wh = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                Stopwatch sw = Stopwatch.StartNew();
                IAsyncResult ar = s.BeginConnect(host, port, null, null);
                wh = ar.AsyncWaitHandle;
                bool ok = wh.WaitOne(timeoutMs);
                sw.Stop();
                if (!ok)
                {
                    err = "TCP:" + port + " 超时";
                    return false;
                }
                try
                {
                    s.EndConnect(ar);
                    ms = Math.Max(0.1, sw.Elapsed.TotalMilliseconds);
                    return true;
                }
                catch (SocketException sex)
                {
                    // 本地网关返回 ConnectionRefused (RST) 依然是实测到的往返时延
                    if (sex.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        ms = Math.Max(0.1, sw.Elapsed.TotalMilliseconds);
                        return true;
                    }
                    err = sex.SocketErrorCode.ToString();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Diag.Hit("nettrace.tcpprobe");
                err = ex.Message;
                return false;
            }
            finally
            {
                if (s != null)
                {
                    try { s.Close(); }
                    catch (Exception ex) { Diag.Fail("nettrace.sock.close", ex); }
                }
                if (wh != null)
                {
                    try { wh.Close(); }
                    catch (Exception ex) { Diag.Fail("nettrace.wh.close", ex); }
                }
            }
        }

        private static void FillStats(PathProbeResult r, string method, List<double> rtts)
        {
            r.Method = method;
            r.Received = rtts.Count;
            r.Reachable = true;
            double sum = 0, min = double.MaxValue, max = 0;
            for (int i = 0; i < rtts.Count; i++)
            {
                double v = rtts[i];
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            r.RttMs = sum / rtts.Count;
            r.MinMs = min;
            r.MaxMs = max;
            r.JitterMs = rtts.Count >= 2 ? (max - min) : 0;
        }

        private static string BuildDiagnosis(DualPathLatencyReport rep)
        {
            if (rep.Lan.Reachable && rep.Lan.RttMs >= 15.0)
                return "局域网网关延迟偏高（" + rep.Lan.RttMs.ToString("0.0") + " ms），建议排查 Wi-Fi 信号或网卡节能";
            if (!rep.Wan.Reachable)
                return rep.TunnelActive ? "外网探测未达（检测到活跃 TUN/VPN 隧道）" : "外网探针超时（请检查外网连通性）";
            if (rep.TunnelActive)
                return "已检测到活跃 TUN/VPN 虚拟网卡 · WAN " + rep.Wan.RttMs.ToString("0.0") + " ms";
            if (rep.Lan.Reachable)
                return "内网 " + rep.Lan.RttMs.ToString("0.0") + " ms · 外网 " + rep.Wan.RttMs.ToString("0.0") + " ms";
            return "外网 " + rep.Wan.RttMs.ToString("0.0") + " ms";
        }
    }
}
