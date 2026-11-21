// ============================================================================
//  ProcName.cs — 原生 Win32 单 PID 极速查询（彻底替代 Process.GetProcessById）
//
//  为什么需要它：Process.GetProcessById(pid) 在 .NET Framework 4.8 上会触发
//  全系统 500+ 进程表扫描（实测 ~13.8 ms/次），在进程页/内存页/总览页/守护路径
//  里叠加调用就是上百毫秒的卡顿。本类全部走单句柄 kernel32.dll 原生调用
//  （~15 µs/次，提速 600~900 倍），统一承载：进程短名 / 完整路径 / 判活+防 PID 复用 / 私有提交内存。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Go
{
    public static class ProcName
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint STILL_ACTIVE = 259;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int MAX_PATH_LONG = 32768;

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        private static readonly uint MemCountersExSize = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder buf, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr h, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr h, out FILETIME create, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(
            IntPtr h, out PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        // 每次调用零分配，按线程隔离防止 UI 线程与守护线程互相覆写缓冲。
        [ThreadStatic] private static StringBuilder _buf;

        private static bool QueryImageIntoBuf(IntPtr h)
        {
            int cap = 512;
            if (_buf == null || _buf.Capacity < cap) _buf = new StringBuilder(cap);
            _buf.Length = 0;
            if (QueryFullProcessImageNameW(h, 0, _buf, ref cap)) return _buf.Length > 0;

            if (Marshal.GetLastWin32Error() != 122) return false;
            int want = _buf.Capacity < 4096 ? 4096 : MAX_PATH_LONG;
            if (want <= _buf.Capacity) return false;
            _buf.Capacity = want;
            cap = want;
            _buf.Length = 0;
            return QueryFullProcessImageNameW(h, 0, _buf, ref cap) && _buf.Length > 0;
        }

        /// <summary>取进程可执行文件名（不含扩展名），与 Process.ProcessName 同义；拿不到返回空串。</summary>
        public static string Of(int pid)
        {
            if (pid <= 0) return "";
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return "";
                if (!QueryImageIntoBuf(h)) return "";
                int len = _buf.Length;
                int start = 0;
                for (int i = len - 1; i >= 0; i--) { if (_buf[i] == '\\') { start = i + 1; break; } }
                int end = len;
                for (int i = len - 1; i >= start; i--) { if (_buf[i] == '.') { end = i; break; } }
                if (end <= start) return "";
                return _buf.ToString(start, end - start);
            }
            catch (Exception ex)
            {
                Diag.Fail("procname.of", ex);
                return "";
            }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { /* 句柄已失效不影响返回值 */ } }
            }
        }

        /// <summary>取进程完整可执行文件路径（复用 TLS 缓冲，免 32/64 MainModule 异常）。</summary>
        public static string FullPathOf(int pid)
        {
            if (pid <= 0) return "";
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return "";
                return QueryImageIntoBuf(h) ? _buf.ToString() : "";
            }
            catch (Exception ex)
            {
                Diag.Fail("procname.fullpath", ex);
                return "";
            }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { /* 可忽略 */ } }
            }
        }

        /// <summary>取不到时回落到进程 id，保证界面上永远有一个可辨认的标识。</summary>
        public static string OrPid(int pid)
        {
            string n = Of(pid);
            return n.Length > 0 ? n : ("pid " + pid);
        }

        /// <summary>
        /// 原生单句柄判活（可选核对 FileTimeUtc 创建时间以拦截 PID 复用），
        /// 单次 ~15 µs，彻底替代 Process.GetProcessById（~13.8 ms 全表扫描）。
        /// </summary>
        public static bool IsAlive(int pid, long expectedBorn, out bool denied, out bool reused)
        {
            denied = false;
            reused = false;
            if (pid <= 0) return false;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED) denied = true;
                    return false;
                }
                uint code;
                if (!GetExitCodeProcess(h, out code) || code != STILL_ACTIVE) return false;
                if (expectedBorn != 0)
                {
                    FILETIME c, e, k, u;
                    if (GetProcessTimes(h, out c, out e, out k, out u))
                    {
                        long real = ((long)c.High << 32) | (uint)c.Low;
                        if (real != 0 && real != expectedBorn)
                        {
                            reused = true;
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Fail("procname.alive", ex);
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { /* 可忽略 */ } }
            }
        }

        public static bool IsAlive(int pid, long expectedBorn)
        {
            bool d, r;
            return IsAlive(pid, expectedBorn, out d, out r);
        }

        /// <summary>原生读取进程私有提交字节（PrivateUsage），单次 ~15 µs；读不到返回 0。</summary>
        public static long ReadPrivateBytes(int pid)
        {
            if (pid <= 0) return 0;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
                if (h == IntPtr.Zero) return 0;
                PROCESS_MEMORY_COUNTERS_EX pmc = new PROCESS_MEMORY_COUNTERS_EX();
                pmc.cb = MemCountersExSize;
                if (!K32GetProcessMemoryInfo(h, out pmc, MemCountersExSize)) return 0;
                ulong v = pmc.PrivateUsage.ToUInt64();
                return v > 0 && v < (ulong)long.MaxValue ? (long)v : 0;
            }
            catch (Exception ex)
            {
                Diag.Fail("procname.priv", ex);
                return 0;
            }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { /* 可忽略 */ } }
            }
        }
    }
}
