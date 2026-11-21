// ============================================================================
//  Actions.cs — 调优动作目录（数据驱动）
//  每条动作 = 若干原子 Op + 风险等级 + 机制说明 + 是否需要重启
//  风险：A=可逆且低风险  B=需重启/涉及服务  C=高风险（长按确认）
//
//  Rev86：Build 从 190 行的单方法按「组」拆开。它原来把所有动作平铺在一个方法里，
//  加一条动作要读完 190 行才知道自己插在哪；现在每组一个方法，顺序仍然就是界面上
//  的分组顺序（组内顺序也保持不变）。
// ============================================================================
using System;
using System.Collections.Generic;

namespace Go
{
    public class ActionDef
    {
        public string Id;
        public string Group;
        public string Name;
        public string Desc;     // 一句话作用
        public string Mech;     // 真实机制（悬停提示）
        public string Risk = "A";
        public bool Restart;
        public bool Remediation; // 修复第三方脚本留下的错误配置
        public Op[] Ops;

        public bool NeedsAdmin
        {
            get
            {
                if (Ops == null) return false;
                foreach (Op o in Ops)
                {
                    if ((o.Kind == "Reg" || o.Kind == "RegKv") && o.Hive != "HKCU") return true;
                    if (o.Kind == "Bcd" || o.Kind == "PowerAc" || o.Kind == "Hibernate") return true;
                }
                return false;
            }
        }
    }

    public enum AState { Applied, NotApplied, Partial, Denied }

    public static class Catalog
    {
        private const string SessionMgr = @"SYSTEM\CurrentControlSet\Control\Session Manager";
        private const string Multimedia = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

        public static List<ActionDef> Build()
        {
            List<ActionDef> L = new List<ActionDef>(24);
            AddRemediation(L);
            AddScheduling(L);
            AddGraphics(L);
            AddMemory(L);
            AddNetwork(L);
            AddInput(L);
            AddSystem(L);
            return L;
        }

        // ---------------- 修复第三方残留 ----------------
        private static void AddRemediation(List<ActionDef> L)
        {
            L.Add(new ActionDef
            {
                Id = "fix-prio", Group = "修复残留", Name = "恢复前台量子加权", Remediation = true,
                Desc = "把被改成固定时间片的 Win32PrioritySeparation 还原为客户端默认值 2",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl\\Win32PrioritySeparation。0x28 会取消前台线程的 3:1 量子加权，等于让后台线程和游戏抢同样的时间片；默认值 2 = 可变长度量子 + 前台加权。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "2") }
            });
            L.Add(new ActionDef
            {
                Id = "fix-mmcss", Group = "修复残留", Name = "恢复 MMCSS 基准值", Remediation = true,
                Desc = "SystemResponsiveness 回 20、Games 任务优先级回 2 / Medium",
                Mech = "微软文档明确：SystemResponsiveness 低于 10 会被夹回 20（设 0 是空操作），Tasks\\Games 的 GPU Priority 与 SFIO Priority 键当前并未被使用。把它改成 High 只会打乱调度档位。",
                Ops = new Op[] {
                    RegD("HKLM", Multimedia, "SystemResponsiveness", "20"),
                    RegD("HKLM", Multimedia + @"\Tasks\Games", "Priority", "2"),
                    RegS("HKLM", Multimedia + @"\Tasks\Games", "Scheduling Category", "Medium")
                }
            });
            L.Add(new ActionDef
            {
                Id = "fix-mpo", Group = "修复残留", Name = "恢复 MPO 多平面叠加", Remediation = true, Risk = "B", Restart = true,
                Desc = "删除 OverlayTestMode=5，让 DWM 重新启用多平面叠加",
                Mech = "HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm\\OverlayTestMode。禁用 MPO 曾是「解决闪烁」的偏方，代价是 DWM 退回更多合成拷贝，增加 GPU 带宽与功耗，现代驱动下通常是净损失。",
                Ops = new Op[] { RegDel("HKLM", @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode") }
            });
            L.Add(new ActionDef
            {
                Id = "fix-gtr", Group = "修复残留", Name = "关闭全局定时器请求", Remediation = true, Risk = "B", Restart = true,
                Desc = "GlobalTimerResolutionRequests=0，撤销全局 0.5ms 定时器",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel\\GlobalTimerResolutionRequests。设为 1 会让整个系统常驻高精度定时器，空闲功耗与温度明显上升；需要高精度的程序应各自调用 NtSetTimerResolution。",
                Ops = new Op[] { RegD("HKLM", SessionMgr + @"\kernel", "GlobalTimerResolutionRequests", "0") }
            });
            L.Add(new ActionDef
            {
                Id = "fix-sysmain", Group = "修复残留", Name = "恢复 SysMain 服务", Remediation = true, Risk = "B",
                Desc = "SysMain 启动类型回「自动」，恢复预读与内存预取",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\SysMain\\Start。禁用 SysMain 是流传最广的偏方之一：它并不占用游戏帧时间，反而让冷启动与地图加载变慢。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Services\SysMain", "Start", "2") }
            });
            L.Add(new ActionDef
            {
                Id = "fix-lsc", Group = "修复残留", Name = "关闭 LargeSystemCache", Remediation = true, Risk = "B", Restart = true,
                Desc = "LargeSystemCache 回 0，避免文件缓存挤占工作集",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management\\LargeSystemCache。设为 1 会让系统文件缓存优先扩大，游戏的工作集更容易被裁剪。",
                Ops = new Op[] { RegD("HKLM", SessionMgr + @"\Memory Management", "LargeSystemCache", "0") }
            });
        }

        // ---------------- 延迟与调度 ----------------
        private static void AddScheduling(List<ActionDef> L)
        {
            L.Add(new ActionDef
            {
                Id = "game-mode", Group = "延迟与调度", Name = "启用 Windows 游戏模式",
                Desc = "让系统识别游戏进程并抑制后台干扰",
                Mech = "HKCU\\Software\\Microsoft\\GameBar\\AutoGameModeEnabled。Windows 会在检测到游戏时降低 Windows Update / 驱动安装等后台任务的优先级。",
                Ops = new Op[] { RegD("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", "1") }
            });
            L.Add(new ActionDef
            {
                Id = "game-dvr", Group = "延迟与调度", Name = "关闭 Game DVR 后台录制",
                Desc = "停止 Xbox 后台捕获与录制开销",
                Mech = "HKCU\\System\\GameConfigStore\\GameDVR_Enabled + HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR\\AppCaptureEnabled。录制管线会常驻 GPU 编码会话，即使不按录制键。",
                Ops = new Op[] {
                    RegD("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", "0"),
                    RegD("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "0")
                }
            });
            L.Add(new ActionDef
            {
                Id = "maint-off", Group = "延迟与调度", Name = "关闭空闲维护任务",
                Desc = "禁止系统在空闲时自动跑磁盘整理/更新扫描",
                Mech = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\Maintenance\\MaintenanceDisabled。维护任务会在挂机时抢占 IO；注意启用后系统自动维护与部分定期诊断将推迟至手动触发或还原后执行。",
                Ops = new Op[] { RegD("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", "1") }
            });
            L.Add(new ActionDef
            {
                Id = "prio-boost", Group = "延迟与调度", Name = "前台加权（0x26 短量子）",
                Desc = "短时间片 + 3:1 前台加权，降低前台延迟",
                Mech = "Win32PrioritySeparation=0x26（38）：短可变量子 + 前台 3 倍加成。相比默认 2 更激进，CPU 占用高时可能略增切换开销。属有条件有效，建议 A/B 实测。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "38") }
            });
            L.Add(new ActionDef
            {
                Id = "cpu-core-park", Group = "延迟与调度", Name = "AC 下禁用 CPU 核心停放",
                Desc = "交流供电下保持所有物理核心在线，减少突发负载唤醒抖动",
                Mech = "powrprof 原生写入 SUB_PROCESSOR / CPMINCORES = 100（0x64）。插电游戏时消除核心从 C6 停放状态唤醒的微卡顿；仅改当前电源方案 AC 项，可由账本还原。",
                Ops = new Op[] { PowerAc("54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", "100", "64") }
            });
            L.Add(new ActionDef
            {
                Id = "cpu-min-state", Group = "延迟与调度", Name = "AC 下锁定处理器最小性能 100%", Risk = "B",
                Desc = "插电时保持处理器最低性能态 100%，消除轻载降频回拉造成的瞬间掉帧",
                Mech = "powrprof 原生写入 SUB_PROCESSOR / PROCTHROTTLEMIN = 100（0x64）。减少竞技游戏场景切换时的 P-State 升频延迟；代价是插电空闲功耗与待机温度略有上升，可随时还原。",
                Ops = new Op[] { PowerAc("54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", "100", "64") }
            });
        }

        // ---------------- 图形栈 ----------------
        private static void AddGraphics(List<ActionDef> L)
        {
            L.Add(new ActionDef
            {
                Id = "hags", Group = "图形栈", Name = "硬件加速 GPU 调度", Risk = "B", Restart = true,
                Desc = "HAGS：显存管理与命令提交交给 GPU 自主调度",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers\\HwSchMode=2。降低 CPU 侧提交开销，提升帧生成稳定性；个别老驱动/录屏软件组合下可能有兼容问题，重启后生效。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "2") }
            });
            L.Add(new ActionDef
            {
                Id = "win-opt-dx", Group = "图形栈", Name = "窗口化游戏延迟优化（Flip Upgrade）",
                Desc = "将 DX10/11 无边框与窗口游戏升级至现代翻转模型，逼近独占全屏延迟",
                Mech = "HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences\\DirectXUserGlobalSettings 精准合并 SwapEffectUpgradeEnable=1（保留 AutoHDR 等其他键）。微软 Windows 11 官方呈现优化，显著降低无边框窗口呈现延迟。",
                Ops = new Op[] { RegKv("HKCU", @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable", "1") }
            });
            L.Add(new ActionDef
            {
                Id = "vrr-opt-dx", Group = "图形栈", Name = "启用窗口化 VRR 可变刷新率优化",
                Desc = "让 G-Sync / FreeSync 高刷屏在无边框窗口游戏中也能平滑同步帧率",
                Mech = "HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences\\DirectXUserGlobalSettings 精准合并 VRROptimizeEnable=1（字段级可逆，不覆盖其他设置）。搭配 144Hz/240Hz 显示器可减少帧节奏撕裂与抖动。",
                Ops = new Op[] { RegKv("HKCU", @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "VRROptimizeEnable", "1") }
            });
            L.Add(new ActionDef
            {
                Id = "fse-on", Group = "图形栈", Name = "强制独立翻转全屏", Risk = "B",
                Desc = "关闭「全屏优化」的 DWM 兼容层，走独立 flip 呈现",
                Mech = "HKCU\\System\\GameConfigStore：GameDVR_FSEBehaviorMode=2 / HonorUserFSEBehaviorMode=1 / DXGIHonorFSEWindowsCompatible=1。可降低一帧输入延迟，但部分老游戏会黑屏或无法切换窗口。",
                Ops = new Op[] {
                    RegD("HKCU", @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", "2"),
                    RegD("HKCU", @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", "1"),
                    RegD("HKCU", @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", "1")
                }
            });
            L.Add(new ActionDef
            {
                Id = "pcie-aspm", Group = "图形栈", Name = "关闭 AC 下 PCIe ASPM 节能",
                Desc = "交流供电下禁用 PCIe 链路低功耗状态切换，稳住显卡与 NVMe 响应",
                Mech = "powrprof 原生写入 SUB_PCIEXPRESS / ASPM = 0（关闭链接状态电源管理）。部分笔记本在插电下仍沿用省电 ASPM 策略，关闭后可降低 PCIe 唤醒延迟并可完整还原。",
                Ops = new Op[] { PowerAc("501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", "0", "0") }
            });
        }

        // ---------------- 内存与存储 ----------------
        private static void AddMemory(List<ActionDef> L)
        {
            L.Add(new ActionDef
            {
                Id = "hiber-off", Group = "内存与存储", Name = "关闭休眠文件", Risk = "C",
                Desc = "删除 hiberfil.sys，回收等同内存容量的磁盘空间",
                Mech = "powercfg /h off。代价是失去休眠与「快速启动」；如果你依赖快速启动，请不要开。可完整还原（powercfg /h on）。",
                Ops = new Op[] { new Op { Kind = "Hibernate", Value = "0", Expect = "0", Name = "hibernate" } }
            });
            L.Add(new ActionDef
            {
                Id = "disk-idle-off", Group = "内存与存储", Name = "禁止 AC 下空闲停转磁盘",
                Desc = "DISKIDLE=0，避免游戏安装盘在加载间隙休眠导致瞬间冻结",
                Mech = "powercfg /setacvalueindex scheme_current SUB_DISK DISKIDLE 0（在此时间后关闭硬盘 = 0 秒 / 永不）。多盘位存游戏时可防止从盘休眠起转造成的几秒卡顿，可由账本还原原秒数。",
                Ops = new Op[] { PowerAc("0012ee47-9041-4b5d-9b77-535fba8b1442", "6738e2c4-e8a5-4a42-b16a-e040e769756e", "0", "0") }
            });
        }

        // ---------------- 网络 ----------------
        private static void AddNetwork(List<ActionDef> L)
        {
            L.Add(new ActionDef
            {
                Id = "net-throttle", Group = "网络", Name = "解除 MMCSS 网络节流",
                Desc = "取消后台网络流 10Mbps 的限速",
                Mech = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\NetworkThrottlingIndex=0xFFFFFFFF（-1）。默认 10 意味着多媒体流被限到 10Mbps。",
                Ops = new Op[] { RegD("HKLM", Multimedia, "NetworkThrottlingIndex", "4294967295") }
            });
            L.Add(new ActionDef
            {
                Id = "nagle", Group = "网络", Name = "关闭 Nagle 算法",
                Desc = "TCPNoDelay=1，小包不再攒批发送",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\TCPNoDelay=1。降低小包延迟，但会增加包数量；对已建立的连接需重连生效。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TCPNoDelay", "1") }
            });
            L.Add(new ActionDef
            {
                Id = "dns-cache", Group = "网络", Name = "延长 DNS 缓存",
                Desc = "MaxCacheTtl=86400，减少重复解析",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\Parameters\\MaxCacheTtl。减少重复 DNS 解析次数，对首次解析无帮助；注意若游戏服刚切换 CDN IP 期间可能需 `ipconfig /flushdns`。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxCacheTtl", "86400") }
            });
            L.Add(new ActionDef
            {
                Id = "p2p", Group = "网络", Name = "关闭更新 P2P 上传",
                Desc = "Delivery Optimization 不再对上传带宽收费",
                Mech = "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\DeliveryOptimization\\Config\\DODownloadMode=0。避免后台向其他机器上传更新包。",
                Ops = new Op[] { RegD("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", "0") }
            });
        }

        // ---------------- 输入与外设 ----------------
        private static void AddInput(List<ActionDef> L)
        {
            L.Add(new ActionDef
            {
                Id = "mouse-accel", Group = "输入与外设", Name = "关闭鼠标指针加速",
                Desc = "线性原始位移，甩枪不再被系统曲线扭曲",
                Mech = "HKCU\\Control Panel\\Mouse：MouseSpeed/MouseThreshold1/MouseThreshold2 = \"0\"（字符串类型）。这是少数确实影响瞄准一致性的设置。",
                Ops = new Op[] {
                    RegS("HKCU", @"Control Panel\Mouse", "MouseSpeed", "0"),
                    RegS("HKCU", @"Control Panel\Mouse", "MouseThreshold1", "0"),
                    RegS("HKCU", @"Control Panel\Mouse", "MouseThreshold2", "0")
                }
            });
            L.Add(new ActionDef
            {
                Id = "kbd-fast", Group = "输入与外设", Name = "键盘响应拉满",
                Desc = "按键重复延迟 0、重复率 31",
                Mech = "HKCU\\Control Panel\\Keyboard：KeyboardDelay=\"0\" / KeyboardSpeed=\"31\"（均为字符串）。",
                Ops = new Op[] {
                    RegS("HKCU", @"Control Panel\Keyboard", "KeyboardDelay", "0"),
                    RegS("HKCU", @"Control Panel\Keyboard", "KeyboardSpeed", "31")
                }
            });
            L.Add(new ActionDef
            {
                Id = "usb-suspend", Group = "输入与外设", Name = "禁用 USB 选择性暂停（驱动层）",
                Desc = "键鼠与外设不再被 USB 驱动空闲挂起",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\USB\\DisableSelectiveSuspend=1。避免无线接收器/手柄在空闲后出现唤醒延迟；注意笔记本使用电池续航时外设待机功耗会略增。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", "1") }
            });
            L.Add(new ActionDef
            {
                Id = "usb-power-ac", Group = "输入与外设", Name = "AC 电源方案关闭 USB 选择性暂停",
                Desc = "插电电源计划下直接关闭 USB 端口节能挂起，立即生效无需重启",
                Mech = "powrprof 原生写入 SUB_USB / USBSELECTSUSPEND = 0。与驱动层键互补，插电游戏时消除 2.4GHz 无线接收器、声卡与手柄的瞬时断联微卡顿；可由账本还原。",
                Ops = new Op[] { PowerAc("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", "0", "0") }
            });
        }

        // ---------------- 系统与后台 ----------------
        private static void AddSystem(List<ActionDef> L)
        {
            L.Add(new ActionDef
            {
                Id = "menu-delay", Group = "系统与后台", Name = "菜单零延迟",
                Desc = "右键与开始菜单立即展开（写入后实时广播生效）",
                Mech = "HKCU\\Control Panel\\Desktop\\MenuShowDelay=\"0\" + SystemParametersInfo 实时刷新。纯体感项。",
                Ops = new Op[] { RegS("HKCU", @"Control Panel\Desktop", "MenuShowDelay", "0") }
            });
            L.Add(new ActionDef
            {
                Id = "min-animate", Group = "系统与后台", Name = "关闭窗口动画",
                Desc = "最小化/最大化动画立即完成",
                Mech = "HKCU\\Control Panel\\Desktop\\WindowMetrics\\MinAnimate=\"0\"（字符串）。部分桌面合成动画需注销或重启 explorer.exe 才会完全刷新生效。",
                Ops = new Op[] { RegS("HKCU", @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0") }
            });
            L.Add(new ActionDef
            {
                Id = "power-throttle-off", Group = "系统与后台", Name = "关闭系统全局电源节流", Risk = "B", Restart = true,
                Desc = "禁用 Windows Power Throttling，防止游戏语音/反作弊/音频线程被限频压制",
                Mech = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling\\PowerThrottlingOff=1。防止系统对后台关键服务施加降频 QoS；笔记本离电续航时功耗将上升，重启生效且可随时还原。",
                Ops = new Op[] { RegD("HKLM", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", "1") }
            });
        }

        private static Op RegD(string hive, string path, string name, string val)
        {
            return new Op { Kind = "Reg", Hive = hive, Path = path, Name = name, Type = "DWord", Value = val };
        }

        private static Op RegS(string hive, string path, string name, string val)
        {
            return new Op { Kind = "Reg", Hive = hive, Path = path, Name = name, Type = "String", Value = val };
        }

        private static Op RegKv(string hive, string path, string name, string kvKey, string val)
        {
            return new Op { Kind = "RegKv", Hive = hive, Path = path, Name = name, KvKey = kvKey, Type = "String", Value = val };
        }

        private static Op RegDel(string hive, string path, string name)
        {
            return new Op { Kind = "Reg", Hive = hive, Path = path, Name = name, Delete = true, Expect = "(不存在)" };
        }

        private static Op PowerAc(string subGuid, string settingGuid, string writeVal, string expectHex)
        {
            return new Op { Kind = "PowerAc", Path = subGuid, Name = settingGuid, Value = writeVal, Expect = expectHex };
        }
    }
}
