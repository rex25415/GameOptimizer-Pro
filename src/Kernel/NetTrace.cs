// ============================================================================
//  NetTrace.cs — PID 级活跃 TCP 摘要 / 双路径延迟探针 / TUN 虚拟网卡识别
//
//  硬约束：
//   · P/Invoke iphlpapi.dll GetExtendedTcpTable (AF_INET, TCP_TABLE_OWNER_PID_ALL=5)。
//   · 双路径探针真实测量 LAN 网关 vs 公网 DNS (223.5.5.5 / 114.114.114.114)，
//     ICMP 不通时走 TCP 端口兜底（WAN :53 / LAN :80,:443）；全失败置 Reachable=false, RttMs=-1，
//     绝不伪造 0ms 读数。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Go
{
    public sealed class PidTcpSummary
    {
        public int Pid;
        public string ProcessName = "";
        public int Established;
        public int SynWait;
        public int TimeWait;
        public int Listen;
        public int ActiveCount;
        public int TotalCount;
        public string TopRemote = "";
        internal bool TopRemoteIsEstab;
        internal bool NameResolved;
    }

    public sealed class VirtualNicInfo
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public string Kind = "";
        public bool Up;
        public string Ipv4 = "";
    }

    public sealed class PathProbeResult
    {
        public string Target = "";
        public string Method = "\u2014";
        public bool Reachable;
        public double RttMs = -1;
        public double MinMs = -1;
        public double MaxMs = -1;
        public double JitterMs;
        public int Sent;
        public int Received;
        public string Error = "";
    }

    public sealed class DualPathLatencyReport
    {
        public string GatewayIp = "";
        public string GatewayInterface = "";
        public PathProbeResult Lan = new PathProbeResult();
        public PathProbeResult Wan = new PathProbeResult();
        public List<VirtualNicInfo> VirtualNics = new List<VirtualNicInfo>();
        public bool TunnelActive;
        public string ActiveTunnelName = "";
        public double RouteDeltaMs = -1;
        public string Diagnosis = "";
    }

    public static partial class NetTrace
    {
        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;

        private const uint MIB_TCP_STATE_CLOSED = 1;
        private const uint MIB_TCP_STATE_LISTEN = 2;
        private const uint MIB_TCP_STATE_SYN_SENT = 3;
        private const uint MIB_TCP_STATE_SYN_RCVD = 4;
        private const uint MIB_TCP_STATE_ESTAB = 5;
        private const uint MIB_TCP_STATE_TIME_WAIT = 11;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        private static readonly string[] TunKeywords =
        {
            "wintun", "wireguard", "clash", "mihomo", "meta", "sing-box", "nekoray",
            "v2ray", "tun2socks", "utun", "tun0", "tap-windows", "openvpn", "tailscale",
            "zerotier", "softether", "warp", "vpn", "tunnel"
        };

        private static readonly string[] WanDnsTargets = { "223.5.5.5", "114.114.114.114" };

        // Rev140：复用持久化 TCP 非托管缓冲 + 短 TTL PID 名称缓存，彻底消除每次 1.5MB ProcessScan 分配与重复 OpenProcess
        private sealed class PidNameCacheEntry
        {
            public string Name;
            public int Tick;
        }

        private const int TcpInitialCap = 64 * 1024;
        private const int TcpMaxCap = 32 * 1024 * 1024;
        private const int PidCacheTtlMs = 3000;
        private const int PidCacheMaxEntries = 512;

        private static readonly object _tcpBufLock = new object();
        private static IntPtr _tcpBuf = IntPtr.Zero;
        private static int _tcpBufCap;

        private static readonly object _pidCacheLock = new object();
        private static readonly Dictionary<int, PidNameCacheEntry> _pidCache = new Dictionary<int, PidNameCacheEntry>(128);

        internal static void ClearPidNameCache()
        {
            lock (_pidCacheLock) { _pidCache.Clear(); }
        }

        private static bool EnsureTcpBufLocked(int want)
        {
            if (want < TcpInitialCap) want = TcpInitialCap;
            if (want > TcpMaxCap) return false;
            if (_tcpBuf != IntPtr.Zero && _tcpBufCap >= want) return true;
            int alloc = Math.Min(TcpMaxCap, Math.Max(want, _tcpBufCap > 0 ? _tcpBufCap * 2 : TcpInitialCap));
            IntPtr next = Marshal.AllocHGlobal(alloc);
            IntPtr old = _tcpBuf;
            _tcpBuf = next;
            _tcpBufCap = alloc;
            if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
            return true;
        }

        public static List<PidTcpSummary> ReadActiveTcpByPid()
        {
            return ReadTcpByPid(false);
        }

        public static List<PidTcpSummary> ReadTcpByPid(bool includeListen)
        {
            Dictionary<int, PidTcpSummary> map = new Dictionary<int, PidTcpSummary>(64);
            try
            {
                lock (_tcpBufLock)
                {
                    if (!EnsureTcpBufLocked(TcpInitialCap))
                    {
                        Diag.Hit("nettrace.tcp.alloc");
                        return new List<PidTcpSummary>();
                    }
                    int size = _tcpBufCap;
                    uint st = GetExtendedTcpTable(_tcpBuf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (st == ERROR_INSUFFICIENT_BUFFER)
                    {
                        if (size < 4 || !EnsureTcpBufLocked(size + 8192))
                        {
                            Diag.Hit("nettrace.tcp.size");
                            return new List<PidTcpSummary>();
                        }
                        size = _tcpBufCap;
                        st = GetExtendedTcpTable(_tcpBuf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                    }
                    if (st != 0)
                    {
                        Diag.Hit("nettrace.tcp.query");
                        return new List<PidTcpSummary>();
                    }

                    uint count = (uint)Marshal.ReadInt32(_tcpBuf, 0);
                    long maxEntries = (size - 4L) / 24L;
                    if (count > maxEntries) count = (uint)Math.Max(0, maxEntries);

                    long p = _tcpBuf.ToInt64() + 4;
                    for (uint i = 0; i < count; i++, p += 24)
                    {
                        uint state = (uint)Marshal.ReadInt32(new IntPtr(p));
                        uint remAddr = (uint)Marshal.ReadInt32(new IntPtr(p + 12));
                        uint remPortRaw = (uint)Marshal.ReadInt32(new IntPtr(p + 16));
                        int pid = Marshal.ReadInt32(new IntPtr(p + 20));
                        ApplyTcpRow(map, state, remAddr, remPortRaw, pid, includeListen, true);
                    }
                }
                FillUnresolvedPidNames(map);
            }
            catch (Exception ex)
            {
                Diag.Fail("nettrace.tcp", ex);
            }

            List<PidTcpSummary> list = new List<PidTcpSummary>(map.Values);
            list.Sort(delegate (PidTcpSummary a, PidTcpSummary b)
            {
                int c = b.Established.CompareTo(a.Established);
                if (c != 0) return c;
                c = b.ActiveCount.CompareTo(a.ActiveCount);
                if (c != 0) return c;
                return b.TotalCount.CompareTo(a.TotalCount);
            });
            return list;
        }

        internal static void ApplyTcpRow(
            Dictionary<int, PidTcpSummary> map, uint state, uint remAddr, uint remPortRaw,
            int pid, bool includeListen, bool resolveName)
        {
            if (map == null || pid < 0) return;
            bool isListen = state == MIB_TCP_STATE_LISTEN;
            bool isClosed = state == MIB_TCP_STATE_CLOSED;
            if (!includeListen && (isListen || isClosed)) return;

            PidTcpSummary s;
            if (!map.TryGetValue(pid, out s))
            {
                s = new PidTcpSummary();
                s.Pid = pid;
                if (resolveName)
                {
                    bool resolved;
                    s.ProcessName = ResolvePidName(pid, out resolved);
                    s.NameResolved = resolved;
                }
                else
                {
                    s.ProcessName = "PID " + pid;
                    s.NameResolved = true;
                }
                map[pid] = s;
            }
            s.TotalCount++;
            if (isListen) s.Listen++;
            else if (!isClosed) s.ActiveCount++;

            bool isEstab = state == MIB_TCP_STATE_ESTAB;
            if (isEstab) s.Established++;
            else if (state == MIB_TCP_STATE_SYN_SENT || state == MIB_TCP_STATE_SYN_RCVD) s.SynWait++;
            else if (state == MIB_TCP_STATE_TIME_WAIT) s.TimeWait++;

            if (!isListen && !isClosed && remAddr != 0)
            {
                if (isEstab)
                {
                    if (!s.TopRemoteIsEstab)
                    {
                        s.TopRemote = FormatIpv4Port(remAddr, remPortRaw);
                        s.TopRemoteIsEstab = true;
                    }
                }
                else if (s.TopRemote.Length == 0)
                {
                    s.TopRemote = FormatIpv4Port(remAddr, remPortRaw);
                }
            }
        }

        private static string ResolvePidName(int pid, out bool resolved)
        {
            if (pid == 0) { resolved = true; return "Idle"; }
            if (pid == 4) { resolved = true; return "System"; }
            int now = Environment.TickCount;
            lock (_pidCacheLock)
            {
                PidNameCacheEntry cached;
                if (_pidCache.TryGetValue(pid, out cached))
                {
                    int age = unchecked(now - cached.Tick);
                    if (age >= 0 && age <= PidCacheTtlMs)
                    {
                        resolved = true;
                        return cached.Name;
                    }
                }
            }
            string n = ProcName.Of(pid);
            if (!string.IsNullOrEmpty(n))
            {
                StorePidNameCache(pid, n, now);
                resolved = true;
                return n;
            }
            resolved = false;
            return "PID " + pid;
        }

        private static void StorePidNameCache(int pid, string name, int now)
        {
            if (pid <= 4 || string.IsNullOrEmpty(name)) return;
            lock (_pidCacheLock)
            {
                if (_pidCache.Count >= PidCacheMaxEntries) _pidCache.Clear();
                PidNameCacheEntry e;
                if (!_pidCache.TryGetValue(pid, out e))
                {
                    e = new PidNameCacheEntry();
                    _pidCache[pid] = e;
                }
                e.Name = name;
                e.Tick = now;
            }
        }

        private static bool NeedsNameScan(PidTcpSummary s)
        {
            return s != null && !s.NameResolved && s.Pid > 4;
        }

        /// <summary>
        /// 对 OpenProcess 受限的进程，复用 ProcessScan 静态共享高水位缓冲一次补齐短名并写入短 TTL 缓存，
        /// 消除旧版每次 new ProcessScan() 反复分配/释放 1.5MB 非托管内存的开销。
        /// </summary>
        private static void FillUnresolvedPidNames(Dictionary<int, PidTcpSummary> map)
        {
            if (map == null || map.Count == 0) return;
            Dictionary<int, string> unresolved = null;
            foreach (KeyValuePair<int, PidTcpSummary> kv in map)
            {
                if (!NeedsNameScan(kv.Value)) continue;
                if (unresolved == null) unresolved = new Dictionary<int, string>(16);
                unresolved[kv.Key] = "";
            }
            if (unresolved == null) return;

            try
            {
                Go.Daemon.ProcessScan.FillPidNames(unresolved);
                int now = Environment.TickCount;
                foreach (KeyValuePair<int, string> kv in unresolved)
                {
                    PidTcpSummary item;
                    if (!string.IsNullOrEmpty(kv.Value) && map.TryGetValue(kv.Key, out item))
                    {
                        item.ProcessName = kv.Value;
                        StorePidNameCache(kv.Key, kv.Value, now);
                    }
                    else
                    {
                        // 进程已瞬时退出：同样短缓存回落标识避免紧邻下一拍再扫全表
                        StorePidNameCache(kv.Key, "PID " + kv.Key, now);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("nettrace.pidscan", ex);
            }
        }

        private static string FormatIpv4Port(uint addrNetOrder, uint portNetOrder)
        {
            uint b0 = addrNetOrder & 0xFF;
            uint b1 = (addrNetOrder >> 8) & 0xFF;
            uint b2 = (addrNetOrder >> 16) & 0xFF;
            uint b3 = (addrNetOrder >> 24) & 0xFF;
            uint port = ((portNetOrder & 0xFF) << 8) | ((portNetOrder >> 8) & 0xFF);
            return b0 + "." + b1 + "." + b2 + "." + b3 + ":" + port;
        }

        // ---- TUN / VPN 虚拟网卡识别 ----------------------------------------
        public static List<VirtualNicInfo> DetectVirtualNics()
        {
            try { return DetectVirtualNicsFrom(NetworkInterface.GetAllNetworkInterfaces()); }
            catch (Exception ex) { Diag.Fail("nettrace.virtualnic", ex); return new List<VirtualNicInfo>(); }
        }

        private static List<VirtualNicInfo> DetectVirtualNicsFrom(NetworkInterface[] all)
        {
            List<VirtualNicInfo> res = new List<VirtualNicInfo>();
            if (all == null) return res;
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    NetworkInterface ni = all[i];
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string kind;
                    if (!IsVirtualTunNic(ni, out kind)) continue;

                    VirtualNicInfo info = new VirtualNicInfo();
                    info.Id = ni.Id ?? "";
                    info.Name = ni.Name ?? "";
                    info.Description = ni.Description ?? "";
                    info.Kind = kind;
                    info.Up = ni.OperationalStatus == OperationalStatus.Up;
                    info.Ipv4 = FirstIpv4(ni);
                    res.Add(info);
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("nettrace.virtualnic", ex);
            }
            return res;
        }

        public static bool IsVirtualTunNic(NetworkInterface ni, out string kind)
        {
            kind = "";
            if (ni == null) return false;
            return IsVirtualTunType((int)ni.NetworkInterfaceType, ni.Name ?? "", ni.Description ?? "", out kind);
        }

        internal static bool IsVirtualTunType(int typeVal, string rawName, string rawDesc, out string kind)
        {
            kind = "";
            string name = (rawName ?? "").ToLowerInvariant();
            string desc = (rawDesc ?? "").ToLowerInvariant();

            // 排除系统内置 6to4/Teredo/IP-HTTPS 等固定过渡接口，避免干扰真实代理/VPN 判别
            if (desc.IndexOf("teredo", StringComparison.Ordinal) >= 0 ||
                desc.IndexOf("ip-https", StringComparison.Ordinal) >= 0 ||
                desc.IndexOf("6to4", StringComparison.Ordinal) >= 0)
                return false;

            if (desc.IndexOf("clash", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("clash", StringComparison.Ordinal) >= 0 ||
                desc.IndexOf("mihomo", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("mihomo", StringComparison.Ordinal) >= 0 ||
                name.Equals("meta", StringComparison.Ordinal))
            {
                kind = "Clash/Mihomo";
                return true;
            }
            if (desc.IndexOf("wireguard", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("wireguard", StringComparison.Ordinal) >= 0 ||
                name.StartsWith("wg", StringComparison.Ordinal))
            {
                kind = "WireGuard";
                return true;
            }
            if (desc.IndexOf("wintun", StringComparison.Ordinal) >= 0)
            {
                kind = "Wintun";
                return true;
            }
            if (desc.IndexOf("sing-box", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("sing-box", StringComparison.Ordinal) >= 0 ||
                desc.IndexOf("v2ray", StringComparison.Ordinal) >= 0)
            {
                kind = "TUN";
                return true;
            }

            for (int i = 0; i < TunKeywords.Length; i++)
            {
                if (name.IndexOf(TunKeywords[i], StringComparison.Ordinal) >= 0 ||
                    desc.IndexOf(TunKeywords[i], StringComparison.Ordinal) >= 0)
                {
                    kind = "VPN/TUN";
                    return true;
                }
            }

            // 53 = PropVirtual, 131 = Tunnel (注意 243 是 Wwanpp 蜂窝移动网卡，不能误判为隧道)
            if (typeVal == 53 || typeVal == 131)
            {
                kind = "虚拟网卡";
                return true;
            }
            return false;
        }

        private static string FirstIpv4(NetworkInterface ni)
        {
            try
            {
                UnicastIPAddressInformationCollection addrs = ni.GetIPProperties().UnicastAddresses;
                for (int i = 0; i < addrs.Count; i++)
                {
                    if (addrs[i].Address.AddressFamily == AddressFamily.InterNetwork)
                        return addrs[i].Address.ToString();
                }
            }
            catch (Exception ex) { Diag.Fail("nettrace.ipv4", ex); }
            return "";
        }

        // ---- 双路径延迟探针（LAN 网关 vs WAN 公网 DNS）--------------------
        public static bool FindDefaultGateway(out string gatewayIp, out string nicName)
        {
            gatewayIp = "";
            nicName = "";
            try { return FindDefaultGatewayFrom(NetworkInterface.GetAllNetworkInterfaces(), out gatewayIp, out nicName); }
            catch (Exception ex) { Diag.Fail("nettrace.gateway", ex); return false; }
        }

        private static bool FindDefaultGatewayFrom(NetworkInterface[] all, out string gatewayIp, out string nicName)
        {
            gatewayIp = "";
            nicName = "";
            if (all == null) return false;
            try
            {
                string fallbackIp = "";
                string fallbackNic = "";
                for (int i = 0; i < all.Length; i++)
                {
                    NetworkInterface ni = all[i];
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string kind;
                    bool isVirt = IsVirtualTunNic(ni, out kind);

                    GatewayIPAddressInformationCollection gws = ni.GetIPProperties().GatewayAddresses;
                    for (int g = 0; g < gws.Count; g++)
                    {
                        IPAddress addr = gws[g].Address;
                        if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = addr.ToString();
                        if (s == "0.0.0.0") continue;
                        if (!isVirt)
                        {
                            gatewayIp = s;
                            nicName = ni.Name ?? ni.Description ?? "";
                            return true;
                        }
                        if (fallbackIp.Length == 0)
                        {
                            fallbackIp = s;
                            fallbackNic = ni.Name ?? ni.Description ?? "";
                        }
                    }
                }
                if (fallbackIp.Length > 0)
                {
                    gatewayIp = fallbackIp;
                    nicName = fallbackNic;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("nettrace.gateway", ex);
            }
            return false;
        }
    }
}
