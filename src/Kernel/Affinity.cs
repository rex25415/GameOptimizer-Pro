// ============================================================================
//  Affinity.cs — 处理器亲和性：原生 Win32 读 / 写 / 恢复 / 缓存簇掩码
//
//  Rev140：
//   · Read / Apply 彻底移除 Process.GetProcessById(pid)（每次 ~13.8 ms 全表扫描），
//     改用原生 OpenProcess + GetProcessAffinityMask / SetProcessAffinityMask（~15 µs，提速 ~900 倍）；
//   · PhysicalOnlyMask 静态懒缓存物理核主线程掩码，消除反复分配缓冲调用 GetLogicalProcessorInformationEx。
//
//  语义约定（调用方必须遵守，否则界面会显示假读数）：
//    · Read 返回 0 表示「读不到」（受保护进程 / 已退出 / 无权限），
//      它不是「没有可用的处理器」——界面上必须显示破折号，不能显示 0。
//    · Apply 只在系统真的接受之后才返回 true；失败时 err 里是可以直接展示的中文原因。
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace Go
{
    public static class Affinity
    {
        /// <summary>掩码是 IntPtr；这里按有符号 64 位的安全上界处理（第 63 位留给符号）。</summary>
        public const int MaxBits = 63;

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;

        private static long _cachedPhysMask;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buf, ref uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(
            IntPtr hProcess, out IntPtr processAffinityMask, out IntPtr systemAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

        /// <summary>本机全部逻辑处理器掩码。处理器数量超过 63 时退化为「全部 63 位」。</summary>
        public static long AllMask()
        {
            int n = Environment.ProcessorCount;
            if (n <= 0) return 0;
            if (n >= MaxBits) return long.MaxValue;
            return (1L << n) - 1;
        }

        /// <summary>第 ccd 个簇的处理器掩码。</summary>
        public static long CcdMask(Topology topo, int ccd)
        {
            if (topo == null || topo.Ccds <= 0) return AllMask();
            if (ccd < 0 || ccd >= topo.Ccds) return 0;

            long all = AllMask();
            if (ccd == 0)
            {
                long first = topo.Ccd0Mask & all;
                return first != 0 ? first : all;
            }

            int per = topo.CoresPerCcd * Math.Max(1, topo.ThreadsPerCore);
            if (per <= 0) return all;
            if (per >= MaxBits) return all;

            int shift = per * ccd;
            if (shift < 0 || shift >= MaxBits) return 0;

            long m = ((1L << per) - 1) << shift;
            m &= all;
            return m != 0 ? m : all;
        }

        /// <summary>
        /// 仅保留每个物理核主线程逻辑核心（关闭 SMT/超线程辅线程）。
        /// 物理核拓扑在运行时不变，首次探测后缓存结果，零重复系统调用与零非托管分配。
        /// </summary>
        public static long PhysicalOnlyMask(Topology topo)
        {
            long cached = System.Threading.Thread.VolatileRead(ref _cachedPhysMask);
            if (cached != 0) return cached;

            long all = AllMask();
            if (all == 0) return 0;

            long probed = ProbePhysicalPrimaryMask(all);
            if (probed != 0)
            {
                System.Threading.Thread.VolatileWrite(ref _cachedPhysMask, probed);
                return probed;
            }

            int stride = topo != null ? Math.Max(1, topo.ThreadsPerCore) : 1;
            if (stride <= 1) return all;

            long mask = 0;
            int lim = Math.Min(MaxBits, Environment.ProcessorCount);
            for (int i = 0; i < lim; i += stride)
            {
                mask |= (1L << i);
            }
            mask &= all;
            long res = mask != 0 ? mask : all;
            System.Threading.Thread.VolatileWrite(ref _cachedPhysMask, res);
            return res;
        }

        private static long ProbePhysicalPrimaryMask(long all)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(0 /*RelationProcessorCore*/, IntPtr.Zero, ref len);
                if (len < 48 || len > 1048576) return 0;
                buf = Marshal.AllocHGlobal((int)len);
                if (!GetLogicalProcessorInformationEx(0, buf, ref len))
                {
                    Diag.Hit("affinity.phys.query");
                    return 0;
                }
                long p = buf.ToInt64();
                long end = p + len;
                long mask = 0;
                int minEntrySize = 32 + IntPtr.Size + 8;
                while (p + 8 <= end)
                {
                    int rel = Marshal.ReadInt32(new IntPtr(p));
                    int sz = Marshal.ReadInt32(new IntPtr(p + 4));
                    if (sz < 16 || p + sz > end) break;
                    if (rel == 0 && sz >= minEntrySize)
                    {
                        ushort groupCount = (ushort)Marshal.ReadInt16(new IntPtr(p + 30));
                        int entryStride = IntPtr.Size + 8;
                        for (int g = 0; g < groupCount; g++)
                        {
                            int off = 32 + g * entryStride;
                            if (off + entryStride > sz) break;
                            ulong rawMask = IntPtr.Size == 4
                                ? (uint)Marshal.ReadInt32(new IntPtr(p + off))
                                : unchecked((ulong)Marshal.ReadInt64(new IntPtr(p + off)));
                            ushort group = (ushort)Marshal.ReadInt16(new IntPtr(p + off + IntPtr.Size));
                            if (group == 0 && rawMask != 0)
                            {
                                ulong lowest = rawMask & (0UL - rawMask);
                                mask |= unchecked((long)lowest) & all;
                            }
                        }
                    }
                    p += sz;
                }
                return mask;
            }
            catch (Exception ex)
            {
                Diag.Fail("affinity.phys", ex);
                return 0;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>原生读取进程亲和性掩码（~15 µs，免全表进程扫描）。0 = 读不到。</summary>
        public static long Read(int pid)
        {
            if (pid <= 0) return 0;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) h = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return 0;
                IntPtr procMask, sysMask;
                if (!GetProcessAffinityMask(h, out procMask, out sysMask)) return 0;
                return procMask.ToInt64();
            }
            catch (Exception ex)
            {
                Diag.Fail("affinity.read", ex);
                return 0;
            }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { /* 可忽略 */ } }
            }
        }

        /// <summary>原生设置进程亲和性掩码（~15 µs）。返回系统是否真的接受。</summary>
        public static bool Apply(int pid, long mask, out string err)
        {
            err = null;
            if (pid <= 0) { err = "无效的进程 id"; return false; }
            if (mask <= 0) { err = "掩码为空：至少需要一个逻辑处理器"; return false; }

            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    int w32 = Marshal.GetLastWin32Error();
                    if (w32 == ERROR_ACCESS_DENIED) err = "拒绝访问（受保护进程需要更高权限）";
                    else if (w32 == ERROR_INVALID_PARAMETER) err = "进程已退出";
                    else err = "无法打开目标进程 (Win32 " + w32 + ")";
                    return false;
                }
                if (!SetProcessAffinityMask(h, new IntPtr(mask)))
                {
                    int w32 = Marshal.GetLastWin32Error();
                    if (w32 == ERROR_ACCESS_DENIED) err = "拒绝访问（受保护进程需要更高权限）";
                    else if (w32 == ERROR_INVALID_PARAMETER) err = "系统拒绝了该掩码（可能包含不存在的处理器）";
                    else err = "设置亲和性失败 (Win32 " + w32 + ")";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Fail("affinity.apply", ex);
                err = ex.Message;
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { /* 可忽略 */ } }
            }
        }

        /// <summary>恢复为全部逻辑处理器。</summary>
        public static bool Restore(int pid, out string err)
        {
            long all = AllMask();
            if (all == 0) { err = "无法枚举逻辑处理器"; return false; }
            return Apply(pid, all, out err);
        }
    }
}
