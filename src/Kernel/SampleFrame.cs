// ============================================================================
//  SampleFrame.cs — 采样帧与原生入口（从 Perf.cs 分出来）
//
//  为什么单独一个文件：HANDOFF 约束单文件 < 800 行，而 PerfSampler 本身已经
//  只做一件事了。这里放"帧的数据形状"（Snapshot）和所有 P/Invoke 声明，
//  让 Perf.cs 只剩采样器的生命周期与节拍，读的时候不用在 800 行里找类边界。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Go
{
    /// <summary>
    /// 一帧采样结果。发布后即视为不可变：写缓冲只在采样线程手里，
    /// 读者拿到的对象要到三拍之后才会被重新写入。
    /// </summary>
    public class Snapshot
    {
        public double CpuTotal;
        public double[] Cores = new double[0];
        public double MemTotalMB;
        public double MemAvailMB;
        public double MemStandbyMB;
        public double MemModifiedMB;
        public double CommitTotalMB;
        public double CommitLimitMB;
        public double KernelPagedMB;
        public double KernelNonpagedMB;

        public double JitterAvgUs;
        public double JitterP99Us;
        public double JitterMaxUs;
        /// <summary>这一拍的抖动读数是不是"正在测"。探针只在显示抖动的页面可见时热跑，
        /// 其余时候没人在采样——界面必须把它显示成 —，而不是一个冻住的旧数字。</summary>
        public bool JitterLive;
        public double DpcPercent;
        public int ProcessCount;
        public double TimerMs;
        // 命名陷阱（Rev82 实测纠正）：NtQueryTimerResolution 的 MinimumResolution 是"最低分辨力"
        // = 最粗颗粒（典型 15.6ms），MaximumResolution 是"最高分辨力"= 最细（典型 0.5ms）。
        // 下面按 out 参数顺序赋值，因此 TimerMinMs 实为最粗、TimerMaxMs 实为最细。
        // 消费方请用 Math.Min/Max 取最细/最粗，不要按字段名望文生义。
        public double TimerMinMs;
        public double TimerMaxMs;
        public long Tick;

        public double MemUsedMB { get { return MemTotalMB - MemAvailMB; } }
        public double MemUsedPct { get { return MemTotalMB <= 0 ? 0 : (MemTotalMB - MemAvailMB) / MemTotalMB; } }

    }

    public static class Native
    {
        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
        [DllImport("ntdll.dll")]
        public static extern int NtSetTimerResolution(uint desired, bool set, out uint actual);
        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(int cls, IntPtr buf, int len);
        [DllImport("ntdll.dll")]
        public static extern int NtQueryTimerResolution(out uint min, out uint max, out uint cur);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX ms);
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetPerformanceInfo(IntPtr pPerfInfo, int cb);
        [DllImport("psapi.dll")]
        public static extern bool EmptyWorkingSet(IntPtr proc);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        /// <summary>
        /// 隐入托盘后把工作集交还给系统（进待机列表，不销毁）。
        /// 常驻守护不需要驻留 11 个页面的视觉树，唤醒时缺页换入即可。
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                Native.EmptyWorkingSet(Native.GetCurrentProcess());
            }
            catch { Diag.Hit("perf.trim"); }
        }
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>sizeof(MEMORYSTATUSEX) = 4 + 4 + 7×8 = 64。预计算，别每拍 Marshal.SizeOf。</summary>
        public static readonly int MemStatusSize = Marshal.SizeOf(typeof(MEMORYSTATUSEX));

        /// <summary>当前进程是否具备管理员令牌。</summary>
        public static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
