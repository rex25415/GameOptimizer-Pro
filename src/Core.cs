// ============================================================================
//  Core.cs — 应用上下文：采样器、状态总线、定时器租约、档案库、页面注册
//  Rev61：所有跨页共享的东西都挂在这里，页面不允许再自建副本。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;

namespace Go
{
    public class Core
    {
        public readonly PerfSampler Perf = new PerfSampler();
        public readonly Engine Engine = new Engine();
        public readonly AppState State = new AppState();
        public readonly TimerService Timer;
        /// <summary>战术档案库：页面与守护引擎共用同一份（含同一把写盘锁）。</summary>
        public readonly ProfileStore Profiles = new ProfileStore();
        /// <summary>真实 CPU 拓扑：簇数 / 每簇核心 / 混合架构。全应用只探测一次。</summary>
        public readonly Topology Topo = Topology.Detect();
        /// <summary>GPU 真实占用/显存采样（PDH）。真实模式由 App 启动；离屏/自检不启动，读数显示 —。</summary>
        public readonly GpuSampler Gpu = new GpuSampler();
        /// <summary>
        /// 全应用共享的进程表快照（一次 NtQuery 遍历，零进程句柄）。
        /// 进程页、后续的内存/档案发现都读这一份，不再各自 Process.GetProcesses()。
        /// </summary>
        public readonly ProcessTable Procs = new ProcessTable();

        public Shell Shell;
        /// <summary>无头/离屏渲染纯读标志：禁止外部公网探测与任何磁盘/电源副作用。</summary>
        public bool HeadlessRender;
        /// <summary>守护引擎。由 App 在窗口显示后注入；离屏渲染与自检模式下为 null。</summary>
        public Go.Daemon.DaemonHost Daemon;
        public bool Elevated;

        private string _profile = "平衡";
        private string _profileEn = "BALANCED";

        public string Profile
        {
            get { return _profile; }
            set { if (_profile == value) return; _profile = value; State.Post(Dirty.Profile); }
        }

        public string ProfileEn
        {
            get { return _profileEn; }
            set { if (_profileEn == value) return; _profileEn = value; State.Post(Dirty.Profile); }
        }

        /// <summary>最近一个「不属于本工具」的前台进程 PID（用于锁核/亲和性目标）。</summary>
        public int LastForeignPid;
        public DateTime Started = DateTime.Now;

        /// <summary>CPU 短名（注册表 ProcessorNameString 去掉厂商与核心数后缀）。</summary>
        public string CpuName = "CPU";
        /// <summary>独立显卡短名（有独显取独显，没有则取第一个适配器）。</summary>
        public string GpuName = "GPU";
        /// <summary>物理显卡列表（注册表 + DXGI LUID 拓扑，首次 DetectHardware 时探测一次）。</summary>
        public List<Hardware.GpuAdapterInfo> Adapters = new List<Hardware.GpuAdapterInfo>();
        /// <summary>当前激活显示器列表（Win32 EnumDisplayDevicesW / EnumDisplaySettingsW，无 WMI）。</summary>
        public List<Hardware.DisplayInfo> Displays = new List<Hardware.DisplayInfo>();

        private Dictionary<string, Page> _pages = new Dictionary<string, Page>();
        private List<Page> _order = new List<Page>();

        public Core()
        {
            Timer = new TimerService(Perf, State);
            // 引擎写账本，总线通知界面：这条线不接上，"账本 8 项"就只能靠页面自己轮询。
            Engine.State = State;
        }

        public IList<Page> Pages { get { return _order; } }

        public void Register(Page p)
        {
            _pages[p.Key] = p;
            _order.Add(p);
        }

        public Page Get(string key)
        {
            Page p;
            return _pages.TryGetValue(key, out p) ? p : null;
        }

        /// <summary>读取真实硬件标识（失败则保留默认值）。</summary>
        public void DetectHardware()
        {
            try
            {
                Elevated = Native.IsElevated();
            }
            catch (Exception ex) { Diag.Fail("core.elevated", ex); }
            Engine.Elevated = Elevated;
            CpuName = Hardware.CpuShortName();
            Adapters = Hardware.ReadAdapters();
            string bestGpu = null;
            for (int i = 0; i < Adapters.Count; i++)
            {
                Hardware.GpuAdapterInfo a = Adapters[i];
                if (a == null || a.Software || string.IsNullOrEmpty(a.ShortName)) continue;
                if (bestGpu == null || a.Discrete)
                {
                    bestGpu = a.ShortName;
                    if (a.Discrete) break;
                }
            }
            GpuName = !string.IsNullOrEmpty(bestGpu) ? bestGpu : Hardware.GpuShortName();
            Displays = Hardware.ReadDisplays();
            State.Raise(Dirty.Elevated);
        }

        /// <summary>按需刷新显示器拓扑（分辨率/刷新率变更后可单独调用）。</summary>
        public void RefreshDisplays()
        {
            Displays = Hardware.ReadDisplays();
        }
    }
}
