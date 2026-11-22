// ============================================================================
//  ProcessPower.cs — Win11 原生 EcoQoS 能效模式 + 进程优先级读写与可逆追踪
//
//  硬约束：
//   · EcoQoS 走 SetProcessInformation(ProcessPowerThrottling=4, EXECUTION_SPEED=1)。
//   · 所有通过本模块发起的临时压制/提权均登记 (PID, BornTime) 追踪表，
//     RevertTracked() 在游戏退出/守护停止时精准还原；PID 若被系统复用则自动跳过。
// ============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Go.Daemon;

namespace Go
{
    public static class ProcessPower
    {
        public const uint PriorityIdle = 0x00000040;
        public const uint PriorityBelowNormal = 0x00004000;
        public const uint PriorityNormal = 0x00000020;
        public const uint PriorityAboveNormal = 0x00008000;
        public const uint PriorityHigh = 0x00000080;
        public const uint PriorityRealtime = 0x00000100;

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessInformation(
            IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(
            IntPtr hProcess, out IntPtr processAffinityMask, out IntPtr systemAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr hProcess, out FILETIME create, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        private sealed class TrackedEntry
        {
            public int Pid;
            public long Born;
            public bool HasOrigEco;
            public uint OrigControlMask;
            public uint OrigStateMask;
            public bool HasOrigPriority;
            public uint OrigPriorityClass;
        }

        private static readonly int PowerThrottlingSize = Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE));
        private static readonly object _trackLock = new object();
        private static readonly Dictionary<int, TrackedEntry> _tracked = new Dictionary<int, TrackedEntry>();
        private static readonly HashSet<string> ProtectedBackgroundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memory compression", "secure system",
            "smss", "csrss", "wininit", "services", "lsass", "lsm", "winlogon",
            "fontdrvhost", "dwm", "svchost", "sihost", "msmpeng", "nissrv",
            "securityhealthservice", "audiodg", "wudfhost", "spoolsv",
            "explorer", "ctfmon", "taskhostw", "searchindexer",
            "gameoptimizer", "wmiprvse", "conhost", "dashost"
        };

        /// <summary>是否为关键系统/安全/托盘后台进程名（防 EcoQoS 误压制；供 Memory.Top 与 AutoPilot 共用）。</summary>
        public static bool IsProtectedBackgroundName(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;
            string n = procName.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            if (n.Length == 0) return false;
            // 兼容官方发布文件名（如 GameOptimizer-Pro-win-x64.exe）直接便携运行时的自保护
            if (n.StartsWith("gameoptimizer", StringComparison.OrdinalIgnoreCase)) return true;
            return ProtectedBackgroundNames.Contains(n);
        }

        public static int TrackedCount
        {
            get { lock (_trackLock) { return _tracked.Count; } }
        }

        public static bool IsTracked(int pid)
        {
            lock (_trackLock) { return _tracked.ContainsKey(pid); }
        }

        public static TrackedProcRecord[] SnapshotTrackedRecords()
        {
            lock (_trackLock)
            {
                if (_tracked.Count == 0) return new TrackedProcRecord[0];
                TrackedProcRecord[] arr = new TrackedProcRecord[_tracked.Count];
                int i = 0;
                foreach (TrackedEntry t in _tracked.Values)
                {
                    TrackedProcRecord r = new TrackedProcRecord();
                    r.Pid = t.Pid;
                    r.Born = t.Born;
                    r.OrigPriority = t.HasOrigPriority ? t.OrigPriorityClass : 0;
                    r.EcoSet = t.HasOrigEco;
                    arr[i++] = r;
                }
                return arr;
            }
        }

        public static int RevertTrackedRecords(TrackedProcRecord[] list)
        {
            if (list == null || list.Length == 0) return 0;
            int reverted = 0;
            for (int i = 0; i < list.Length; i++)
            {
                TrackedProcRecord r = list[i];
                if (r == null || r.Pid <= 0) continue;
                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, r.Pid);
                if (h == IntPtr.Zero) { Diag.Hit("procpower.recover.gone"); continue; }
                try
                {
                    long born = ReadBornTime(h);
                    if (r.Born != 0 && born != 0 && born != r.Born)
                    {
                        Diag.Hit("procpower.recover.reused");
                        continue;
                    }
                    bool any = false;
                    if (r.OrigPriority != 0 && SetPriorityClass(h, r.OrigPriority)) any = true;
                    if (r.EcoSet)
                    {
                        PROCESS_POWER_THROTTLING_STATE st = new PROCESS_POWER_THROTTLING_STATE();
                        st.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
                        st.ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                        st.StateMask = 0;
                        if (SetProcessInformation(h, ProcessPowerThrottling, ref st, PowerThrottlingSize))
                            any = true;
                    }
                    if (any) reverted++;
                }
                catch (Exception ex) { Diag.Fail("procpower.recover", ex); }
                finally { CloseHandle(h); }
            }
            return reverted;
        }

        /// <summary>将 NtQuerySystemInformation 的 BasePriority 映射为标准优先级类。</summary>
        public static uint BasePriorityToClass(int basePriority)
        {
            if (basePriority <= 0) return 0;
            if (basePriority <= 4) return PriorityIdle;
            if (basePriority <= 6) return PriorityBelowNormal;
            if (basePriority <= 8) return PriorityNormal;
            if (basePriority <= 10) return PriorityAboveNormal;
            if (basePriority <= 15) return PriorityHigh;
            return PriorityRealtime;
        }

        public static uint ParsePriorityMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return 0;
            if (string.Equals(mode, "High", StringComparison.OrdinalIgnoreCase)) return PriorityHigh;
            if (string.Equals(mode, "AboveNormal", StringComparison.OrdinalIgnoreCase)) return PriorityAboveNormal;
            if (string.Equals(mode, "Normal", StringComparison.OrdinalIgnoreCase)) return PriorityNormal;
            if (string.Equals(mode, "BelowNormal", StringComparison.OrdinalIgnoreCase)) return PriorityBelowNormal;
            if (string.Equals(mode, "Idle", StringComparison.OrdinalIgnoreCase)) return PriorityIdle;
            return 0;
        }

        public static int PriorityRank(uint priorityClass)
        {
            switch (priorityClass)
            {
                case PriorityRealtime: return 6;
                case PriorityHigh: return 5;
                case PriorityAboveNormal: return 4;
                case PriorityNormal: return 3;
                case PriorityBelowNormal: return 2;
                case PriorityIdle: return 1;
                default: return 0;
            }
        }

        public static string PriorityLabel(uint priorityClass)
        {
            switch (priorityClass)
            {
                case PriorityRealtime: return "实时";
                case PriorityHigh: return "高";
                case PriorityAboveNormal: return "高于";
                case PriorityNormal: return "正常";
                case PriorityBelowNormal: return "低于";
                case PriorityIdle: return "空闲";
                default: return "\u2014";
            }
        }

        public static uint ReadPriority(int pid)
        {
            if (pid <= 0) return 0;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                return GetPriorityClass(h);
            }
            catch (Exception ex)
            {
                Diag.Fail("procpower.prio.read", ex);
                return 0;
            }
            finally { CloseHandle(h); }
        }

        public static bool SetPriority(int pid, uint priorityClass, out string err)
        {
            return SetPriority(pid, priorityClass, false, out err);
        }

        public static bool SetPriority(int pid, uint priorityClass, bool track, out string err)
        {
            err = null;
            if (pid <= 0) { err = "无效的进程 PID"; return false; }
            if (PriorityRank(priorityClass) == 0) { err = "无效的优先级类别"; return false; }

            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                int w32 = Marshal.GetLastWin32Error();
                Diag.Hit("procpower.prio.open");
                err = FormatWin32Error(w32);
                return false;
            }
            try
            {
                uint prev = GetPriorityClass(h);
                long born = ReadBornTime(h);
                if (!SetPriorityClass(h, priorityClass))
                {
                    int w32 = Marshal.GetLastWin32Error();
                    Diag.Hit("procpower.prio.set");
                    err = FormatWin32Error(w32);
                    return false;
                }
                uint after = GetPriorityClass(h);
                if (after != 0 && after != priorityClass)
                {
                    err = "系统将优先级降级为 " + PriorityLabel(after) + "（可能需要管理员特权）";
                    if (track && prev != 0 && prev != after) TrackPriority(pid, born, prev);
                    return false;
                }
                if (track && prev != 0 && prev != priorityClass)
                    TrackPriority(pid, born, prev);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Fail("procpower.prio.set.ex", ex);
                err = ex.Message;
                return false;
            }
            finally { CloseHandle(h); }
        }

        public static bool SetPriorityMode(int pid, string mode, bool track, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(mode) || string.Equals(mode, "Keep", StringComparison.OrdinalIgnoreCase))
                return true;
            uint cls = ParsePriorityMode(mode);
            if (cls == 0)
            {
                err = "不支持的优先级策略: " + mode;
                return false;
            }
            return SetPriority(pid, cls, track, out err);
        }

        public static bool ReadEcoQoS(int pid, out bool enabled)
        {
            enabled = false;
            if (pid <= 0) return false;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                return ReadEcoQoSHandle(h, out enabled);
            }
            catch (Exception ex)
            {
                Diag.Fail("procpower.eco.read", ex);
                return false;
            }
            finally { CloseHandle(h); }
        }

        public static bool SetEcoQoS(int pid, bool enable, out string err)
        {
            return SetEcoQoS(pid, enable, enable, out err);
        }

        public static bool SetEcoQoS(int pid, bool enable, bool track, out string err)
        {
            return SetEcoQoSCore(pid, 0, false, enable, track, out err);
        }

        /// <summary>打开句柄后先校验进程创建时间 == expectedBorn，防止 PID 复用误操作无关进程。</summary>
        public static bool SetEcoQoSIfMatch(int pid, long expectedBorn, bool enable, bool track, out string err)
        {
            return SetEcoQoSCore(pid, expectedBorn, true, enable, track, out err);
        }

        private static bool SetEcoQoSCore(int pid, long expectedBorn, bool verifyBorn, bool enable, bool track, out string err)
        {
            err = null;
            if (pid <= 0) { err = "无效的进程 PID"; return false; }
            if (verifyBorn && expectedBorn <= 0)
            {
                err = "缺少进程创建时间校验基准，拒绝设置 EcoQoS";
                return false;
            }

            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                int w32 = Marshal.GetLastWin32Error();
                Diag.Hit("procpower.eco.open");
                err = FormatWin32Error(w32);
                return false;
            }
            try
            {
                long born = ReadBornTime(h);
                if (verifyBorn && (born <= 0 || born != expectedBorn))
                {
                    Diag.Hit("procpower.eco.bornmismatch");
                    err = "目标进程已退出或 PID 已被系统复用（创建时间不匹配）";
                    return false;
                }
                PROCESS_POWER_THROTTLING_STATE prev = new PROCESS_POWER_THROTTLING_STATE();
                prev.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
                bool hasPrev = GetProcessInformation(h, ProcessPowerThrottling, ref prev, PowerThrottlingSize);

                PROCESS_POWER_THROTTLING_STATE next = new PROCESS_POWER_THROTTLING_STATE();
                next.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
                next.ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                next.StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0;

                if (!SetProcessInformation(h, ProcessPowerThrottling, ref next, PowerThrottlingSize))
                {
                    int w32 = Marshal.GetLastWin32Error();
                    Diag.Hit("procpower.eco.set");
                    err = FormatWin32Error(w32);
                    return false;
                }

                // 写后回读校验
                bool readBack;
                if (ReadEcoQoSHandle(h, out readBack) && readBack != enable)
                {
                    Diag.Hit("procpower.eco.verify");
                    err = "EcoQoS 回读状态不一致";
                    return false;
                }

                if (track)
                {
                    uint origCtrl = hasPrev ? prev.ControlMask : PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                    uint origState = hasPrev ? prev.StateMask : 0;
                    TrackEco(pid, born, origCtrl, origState);
                }
                else if (!enable)
                {
                    UntrackEco(pid);
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Fail("procpower.eco.set.ex", ex);
                err = ex.Message;
                return false;
            }
            finally { CloseHandle(h); }
        }

        public static bool ReadPowerAndAffinity(int pid, out uint prioClass, out long affinityMask, out bool ecoKnown, out bool ecoEnabled)
        {
            prioClass = 0;
            affinityMask = 0;
            ecoKnown = false;
            ecoEnabled = false;
            if (pid <= 0) return false;

            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                prioClass = GetPriorityClass(h);
                IntPtr procMask, sysMask;
                if (GetProcessAffinityMask(h, out procMask, out sysMask))
                    affinityMask = procMask.ToInt64();
                ecoKnown = ReadEcoQoSHandle(h, out ecoEnabled);
                return prioClass != 0 || affinityMask != 0 || ecoKnown;
            }
            catch (Exception ex)
            {
                Diag.Fail("procpower.inspect", ex);
                return false;
            }
            finally { CloseHandle(h); }
        }

        public static void Untrack(int pid)
        {
            lock (_trackLock) { _tracked.Remove(pid); }
        }

        private static void UntrackEco(int pid)
        {
            lock (_trackLock)
            {
                TrackedEntry t;
                if (!_tracked.TryGetValue(pid, out t)) return;
                t.HasOrigEco = false;
                if (!t.HasOrigPriority) _tracked.Remove(pid);
            }
        }

        /// <summary>精准还原全部已追踪进程的优先级与 EcoQoS 设置。</summary>
        public static int RevertTracked()
        {
            List<TrackedEntry> copy;
            lock (_trackLock)
            {
                if (_tracked.Count == 0) return 0;
                copy = new List<TrackedEntry>(_tracked.Values);
                _tracked.Clear();
            }

            int reverted = 0;
            for (int i = 0; i < copy.Count; i++)
            {
                TrackedEntry t = copy[i];
                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, t.Pid);
                if (h == IntPtr.Zero)
                {
                    Diag.Hit("procpower.revert.gone");
                    continue;
                }
                try
                {
                    long born = ReadBornTime(h);
                    if (t.Born != 0 && born != 0 && born != t.Born)
                    {
                        Diag.Hit("procpower.revert.reused");
                        continue;
                    }
                    bool any = false;
                    if (t.HasOrigPriority && t.OrigPriorityClass != 0)
                    {
                        if (SetPriorityClass(h, t.OrigPriorityClass)) any = true;
                        else Diag.Hit("procpower.revert.prio");
                    }
                    if (t.HasOrigEco)
                    {
                        PROCESS_POWER_THROTTLING_STATE st = new PROCESS_POWER_THROTTLING_STATE();
                        st.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
                        // ControlMask 必须包含 EXECUTION_SPEED 位才能让 StateMask=0 生效取消节流
                        st.ControlMask = t.OrigControlMask | PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                        st.StateMask = t.OrigStateMask;
                        if (SetProcessInformation(h, ProcessPowerThrottling, ref st, PowerThrottlingSize))
                        {
                            any = true;
                        }
                        else Diag.Hit("procpower.revert.eco");
                    }
                    if (any) reverted++;
                }
                catch (Exception ex)
                {
                    Diag.Fail("procpower.revert", ex);
                }
                finally { CloseHandle(h); }
            }
            return reverted;
        }

        private static bool ReadEcoQoSHandle(IntPtr h, out bool enabled)
        {
            enabled = false;
            PROCESS_POWER_THROTTLING_STATE state = new PROCESS_POWER_THROTTLING_STATE();
            state.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
            if (!GetProcessInformation(h, ProcessPowerThrottling, ref state, PowerThrottlingSize))
                return false;
            enabled = (state.ControlMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0 &&
                      (state.StateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
            return true;
        }

        private static long ReadBornTime(IntPtr h)
        {
            FILETIME c, e, k, u;
            if (!GetProcessTimes(h, out c, out e, out k, out u)) return 0;
            return ((long)c.High << 32) | c.Low;
        }

        private static void TrackPriority(int pid, long born, uint origPrio)
        {
            lock (_trackLock)
            {
                TrackedEntry t;
                if (!_tracked.TryGetValue(pid, out t) || (t.Born != 0 && born != 0 && t.Born != born))
                {
                    t = new TrackedEntry();
                    t.Pid = pid;
                    t.Born = born;
                    _tracked[pid] = t;
                }
                if (!t.HasOrigPriority)
                {
                    t.HasOrigPriority = true;
                    t.OrigPriorityClass = origPrio;
                }
            }
        }

        private static void TrackEco(int pid, long born, uint origCtrl, uint origState)
        {
            lock (_trackLock)
            {
                TrackedEntry t;
                if (!_tracked.TryGetValue(pid, out t) || (t.Born != 0 && born != 0 && t.Born != born))
                {
                    t = new TrackedEntry();
                    t.Pid = pid;
                    t.Born = born;
                    _tracked[pid] = t;
                }
                if (!t.HasOrigEco)
                {
                    t.HasOrigEco = true;
                    t.OrigControlMask = origCtrl;
                    t.OrigStateMask = origState;
                }
            }
        }

        private static string FormatWin32Error(int code)
        {
            if (code == 5) return "拒绝访问（受保护进程或需要管理员权限）";
            if (code == 87) return "进程可能已退出或参数不支持 (ERROR_INVALID_PARAMETER)";
            if (code == 0) return "未知错误";
            return new Win32Exception(code).Message;
        }
    }
}
