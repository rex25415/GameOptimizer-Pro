## Rev 141 · 2026-11-21（DESKTOP CITIZEN 桌面软件化与快捷方式闭环：内建原生 COM IShellLinkW 一键安装 · 下载态温和引导与源码仓自动静音 · 便携包双击安装脚本）

- **原生零依赖桌面安装与快捷方式引擎（`src/Kernel/AppInstall.cs`）**：
  - 使用原生 COM `IShellLinkW + IPersistFile` 与 `SHChangeNotify`，零 WSH / 零 PowerShell 黑框闪烁即可完成单文件 exe 复制至 `%LOCALAPPDATA%\Programs\GameOptimizer\`（支持旧实例原子重命名热替换）、桌面与开始菜单 `.lnk` 创建与去重、内嵌 `Uninstall.ps1` 落盘及 `HKCU\...\Uninstall\GameOptimizerPro` 系统卸载项注册。
  - 智能区分运行来源：普通用户从下载/临时目录打开且未安装时触发引导；开发者在源码仓（含 `build.ps1 + src\App.cs`）自编译或已从安装目录启动时自动静音零打扰。
- **三处无侵入式桌面入口（`Shell.Banner.cs` / `Shell.Rail.cs` / `Shell.Palette.cs` / `Shell.Nav.cs`）**：
  - 顶部温和引导带：仅在下载便携态首次显示，支持一键「安装并建桌面图标」或永久「保持便携」（状态持久化至 `ui.json`）；
  - 侧栏底行零高度增量动作标签（`建桌面图标` / `桌面图标`）与 `Ctrl+K` 命令面板（「安装到本机并创建桌面快捷方式」「打开程序安装目录」），随时可补建或对齐图标。
- **部署与便携包双击体验升级（`tools/deploy.ps1` / `tools/Install-GameOptimizer.bat` / `tools/Uninstall-GameOptimizer.bat`）**：
  - `deploy.ps1` 补齐全新环境自动新建开始菜单快捷方式及便携包 `.exe` 通配识别；新增双击即用的 `Install-GameOptimizer.bat` 与 `Uninstall-GameOptimizer.bat`，无需普通用户手动输入 PowerShell 命令。
- **验收**：零警告编译；`--selftest 117/0`（新增 4 项 Rev 141 沙箱探测/原生 `.lnk` 创建去重/回读回归）、`--uitest 85/0`、`--alloc-test` 与 `--stress-test` 全通过，11 页离屏渲染冷启动 `rail_foot` 零回归。

---

## Rev 140 · 2026-11-21（IRONCLAD KERNEL 后端全面重构、原生 Win32 加速与并发稳定性升级：RegGetValueW 零异常直读 · 清零全仓 GetProcessById 全表扫描 · NetTrace 10.9× 加速 · ProcessScan 并发竞态修复）

- **原生 Win32 注册表引擎（`src/Reg.cs`）**：
  - `Reg.Read` 升级为单次原生 `advapi32.dll` `RegGetValueW(RRF_RT_ANY | RRF_NOEXPAND)` + `[ThreadStatic]` 复用缓冲，值不存在不再抛出 CLR `IOException` 栈回溯，全 7 种注册表类型精准解码；`Reg.KeyExists` 改为原生 `RegOpenKeyExW`。实测 `reg.read` **`42.4 → 18.7 µs`（2.3×）**、`reg.read.miss` **`38.0 → 16.3 µs`（2.3×，0 异常）**，`action.apply+revert` 缩短 **36.2%**。
- **全仓清零 `Process.GetProcessById` / `GetProcessesByName` 全表扫描（`ProcName.cs` / `Affinity.cs` / `AutoPilot.cs` / `SessionStore.cs` / `SessionRecorder.cs` / `Processes*.cs` / `Memory.Top.cs` / `Overview.Bottom.cs`）**：
  - `ProcName` 统一承载原生单句柄 `FullPathOf`、`IsAlive`（`2.6 µs`）与 `ReadPrivateBytes`（`K32GetProcessMemoryInfo`，`3.1 µs`）；`Affinity.Read/Apply` 改走原生 `GetProcessAffinityMask` / `SetProcessAffinityMask`（`2.9 µs`，较原 `~13.8 ms` 提速数千倍），`PhysicalOnlyMask` 懒缓存至 **`0.1 µs`**。
  - 彻底清理 `AutoPilot.AttachAffinity`、`SessionStore.Alive`、`SessionRecorder.NameOf`、`Processes.UpdateFocus`（消除 `27.6 ms` UI 卡顿）、`Memory.Top.PrivateOf`（消除 `138 ms` 重复全表扫描及重复系统进程黑名单）及 `Processes.Detail` / `Overview.Bottom` 重复 P/Invoke 与回退逻辑。
- **`NetTrace` 10.9× 提速与双路径并行探测（`NetTrace.cs` / `NetTrace.Probe.cs`）**：
  - 启用持久化高水位非托管 TCP 缓冲、关闭冗余内核 `bOrder` 排序、以共享 `ProcessScan.FillPidNames` + `3s` TTL 缓存与 `NameResolved` 标志消除反复 `1.5 MB` 分配与已退出端口重扫，实测 `net.tcp.pid` 从 **`19.98 ms` 降至 `1.86 ms`（10.9× 提速）**；拆出 `NetTrace.Probe.cs`，合并网卡枚举并将 LAN / WAN 双路探测改为并行执行。
- **并发安全、电源/内存与引擎优化（`ProcessScan.cs` / `Power*.cs` / `ProcessPower.cs` / `Ops*.cs` / `Engine.cs` / `Core.cs` / `Ledger.cs`）**：
  - 修复 `ProcessScan.ForEach` 与 `SnapshotCpu` 锁外覆写回调/字典的多线程数据竞态，新增 ASCII 快速比较与共享 `FillPidNames` / `LookupNameByPid`；`Power` 消除 `Guid` 装箱与 64 次循环非托管分配并缓存方案友好名，`Power.Ac` 别名优先查表并支持批量复用 `Active()`；`Ops` 补齐 `Mem` 异常防泄漏并新增 15s BCD 快照缓存；`Engine` 建立 O(1) 动作与分组索引；`Core.DetectHardware` 复用 `Adapters` 免二次扫注册表；`Ledger` 复用静态 `Utf8NoBom`。
- **验收**：零编译警告、`1,244,672 B`、全仓 `165` 文件最长 `798` 行；`selftest 113/0`（含 4 线程并发压测与 7 类型原生注册表测试）、`uitest 84/0`、`alloc-test 11/0`、`stress-test 8/0`、`backend-bench fail=0`、`daemon-test 56/4`（仅无桌面 `StageTray` 4 项环境限制）、11 页 `--render` 零副作用全通过。详见 `REV140.md`。

---

## Rev 130 · 2026-11-20（VANGUARD CONSOLE 全站空间架构大修、深度清虫与后端遥测引擎升级：11 页定制级非对称舞台 · 同心矢量主题色球 · Tactical Inspector · 虚拟多屏画布与四节点路由拓扑）

- **高危与隐蔽缺陷全面清零**：
  - **彻底根治左下角主题色球偏心与跳动（`Shell.Rail.cs`）**：废弃受高 DPI `UseLayoutRounding` 影响的 `Border` 包圆结构，改用 `20×20` 矢量同心 `Grid + 3 Ellipse`（外光晕 / 描边环 / 核心点），实现 `0.0 px` 数学同心；新增当前主题中文微标与窄导轨一键循环换肤圆钮。
  - **外壳、换肤、最大化与 `Ctrl+K` 全链路修复（`Shell*.cs`）**：实时换肤清零状态指纹并杜绝向历史栈追加重复记录；`WindowChrome` 修复「最大化→最小化→还原最大化」后采样永久停摆、补偿 `7px` 边缘溢出并动态切换最大化/还原图标；`Ctrl+K` 命令面板支持按快捷键后直接键入（自动清除占位）、键盘 `BringIntoView()` 滚随与 `MaxHeight=326` 防裁切；修复标题栏冷态 `JIT 0 µs` 闪现与侧栏管理员 Tooltip 误报只读。
  - **页面与通用容器缺陷修复**：修复 01 总览电源箭头旋转中心偏移与 Nagle 还原失败误报权限、09 基准测试 `1 µs` 银行家舍入误差（统一 `AwayFromZero`）、11 审计日志首次进入卡在顶部未贴底，以及 `Reflow` 缺 `Clear()` 导致重入页面出现幽灵空列的问题（`src/Rs.cs`）。
- **后端新增零分配遥测引擎与网络路由增强**：
  - **新增 `src/Kernel/Telemetry.Engine.cs`**：提供纯值类型零堆分配的 `TelemetryEngine.Evaluate`（四维系统健康评估：调度唤醒 34% / 算力余裕 24% / 内存余裕 22% / 调优洁净度 20% + `S/A/B/C` 评级 + 诚实中文瓶颈诊断）、`SummarizeCcds`（双 CCD 均值/峰值/最热核）与 `ClassifyRoute`（直连 / TUN 隧道 / 内网偏高 / 离线链路评级）。
  - **增强 `src/Kernel/NetTrace.cs`**：`DualPathLatencyReport` 新增 `ActiveTunnelName` 与 `RouteDeltaMs`（`WAN − LAN` 路由附加延迟），实时赋能网络拓扑视图。
- **全站 11 页空间架构大修（一页一舞台，彻底告别模板套壳）**：
  - **01 态势总览**：左 `338px` Mission Readiness 指挥塔（实时四维 `GRADE` 徽标 + 表盘 + 2×2 胶囊 + 自动换行瓶颈诊断）+ 右 2×2 宽体 Bento 遥测与四维健康条 + 下半区 `1.38* : 1*` 非对称舞台。
  - **02 调优矩阵**：升级 Master-Detail 随滚吸顶工作台，新增右侧 `296px` **Tactical Inspector（战术检视台，`Tuning.Inspector.cs`）**，通过零重排 `TranslateTransform` 随外层页面滚动全程吸顶跟随（底栏带 `01 / 30` 序号与 `‹ / ›` 步进钮），实时展示原值→目标值双栏 Diff、原子操作路径、工程机理与直达应用/还原。
  - **03 调度与定时器**：`1.56*` 左侧巨幅交互式 32 线程 `SchedMatrix` 主舞台 + `1*` 右侧 2×2 Bento 象限，全页一屏尽览。
  - **04 内存与提交**：`1.52*` 左侧 `26px` `MemRibbon 3.0` + 2×2 象限 + 实话诊断与右侧提交/动作台并排。
  - **05 图形栈**：新增自绘零分配 **`DisplayStageCanvas` 虚拟多屏蓝图画布（`Graphics.Canvas.cs`）**，按真实桌面坐标与刷新率/色深等比绘屏，且首屏直显 1Hz PDH GPU 引擎占用。
  - **06 网络与吞吐**：顶部新增 **四节点信号路由拓扑（`Network.Topology.cs`）** 实时呈现 `PC → NIC/TUN → LAN → WAN` 路径与跳段延迟，搭配 `224px` 高双通道示波器与 2×2 指标象限。
  - **07 进程 / 08 档案 / 09 基准 / 10 还原 / 11 日志**：进程页顶部重构为 `1.72*` 前台锁定雷达 + 2 枚实时态势卡；战术档案重构为 `Reflow` 2 列立体游戏卡牌墙；基准页新增 `S/A/B/C` 等级徽章；还原中心拆分为独立 `写前安全金库` 与法医时间线；审计日志首进即锁定最新底部记录。
- **品牌与验收**：品牌升级至 **`GameOptimizer Pro · VANGUARD CONSOLE (Rev 130)`**（`130.0.2026.1120`），同步更新闪屏与 9 尺寸图标；零警告、全仓 163 文件单文件 `<= 798` 行；`selftest 108/0`、`uitest 83/0`（11 页首建 `9–110 ms`）、`alloc-test 11/0`、`stress-test 8/0`、`daemon-test 56/4`（仅无桌面 `StageTray` 4 项环境限制，0 逻辑失败）、`backend-bench fail=0`、13 帧离屏目视核验零副作用全通过。详见 `REV130.md`。

---

## Rev 120 · 2026-11-20（HYPERION GLASS 前端美学与光学界面全面重构：深空蓝曜玻璃材质 · 双色相极光 · Bento 悬浮仪表 · 五大光学图元与全站 11 页精雕）

- **HYPERION GLASS 设计系统与双色相光学基底（`Theme.cs` / `Brand.cs` / `Ui.cs` / `Ui.Kit.cs` / `Gfx.cs`）**：
  - 全站彻底告别平涂炭灰，重塑为五层深空蓝曜玻璃层级（`#04070E → #172338`）+ 冷光玻璃描边（`#22CFE6FF`）+ 现代圆角体系（`Radius 12/14/8/6`）；为 4 套主题引入双色相副色 `Accent2`（冰蓝+电钴 / 琥珀+炽红 / 紫晶+靛蓝 / 薄荷+天青）及全套冻结画刷缓存（`AccentGrad` / `GlassPanelBg` / `HeroPanelBg` / `SubCardBg` / `HeroGlow2` 等）。
  - 新增 `Ui.GlassTile` 悬浮 Bento 指标子卡基元（顶部 2px 语义光丝 + 蓝曜内凹渐变）；升级 `Ui.HeroPanel` 对角双极光光场、26px 发光 LED 胶囊页头与 64px 轻盈光学准星蓝图背景；同步升级 `SplashNative.cs` 启动闪屏与 `assets/app.ico` 9 尺寸图标至 Rev 120。
- **外壳、遥测灵动胶囊、触感控件 2.0 与托盘统一（`Shell*.cs` / `NavRow.cs` / `Btn.cs` / `Switch.cs` / `Ui.ActionRow.cs` / `TrayMenu.cs`）**：
  - 主画布部署三枚静态径向极光光场，左侧栏升级半透蓝曜玻璃与 `36×36` 商标徽章、`34px` 柔光胶囊导航行；标题栏新增磨砂深空玻璃顶栏与居中 **Telemetry Island（遥测灵动胶囊）**（严格保持 `1600/1360/1000` 零重叠）。
  - `Switch 2.0` 开启态重铸为饱和双色相发光实体胶囊 + 纯白滑珠；`Btn 2.0` 引入对角能源渐变与钛金属磨砂渐变；`SegTabs 2.0` 升级玻璃滑轨；`ActionRow` 状态读数升级圆角胶囊与翠绿 `✓` 前缀；托盘菜单 `TrayMenu` 同步蓝曜玻璃、顶部折光棱与 `Switch 2.0` 风格开关。
- **五大零分配光学图元（2.0 – 4.0）**：
  - `ArcGauge 3.0`（内凹精密表盘 + 状态径向光晕 + 4 层光学弧 + 5 层激光指针，`307.2 B/帧`）、`Waveform 4.0`（3 级光晕面积 + 4 层白热内芯单调三次轨 + 悬浮胶囊标签，`20.5 B/帧`）、`CoreGrid 3.0`（圆角硅晶插座 + 发光液态顶帽，`40.7 B/帧`）、`MemRibbon 3.0`（3D 立体玻璃能量条，`0.0 B/帧`）与 `SegBar 2.0` / `Sparkline 2.0` / `BenchmarkHistogram 2.0`（频谱渐变柱 + 悬浮 `P99` 胶囊）。
- **全站 11 页定制级重塑**：
  - 01 总览（4 宫格语义 Bento 玻璃卡 + `220×182` 表盘 + 发光闪电指令卡）、02 调优矩阵（3 张立体战术预设玻璃卡 + 圆角磨砂搜索胶囊）、03 调度 / 04 内存 / 06 网络 / 09 基准 / 10 还原（统一 Bento 玻璃指标卡体系与增强型亲和矩阵/立体内存条）、05 图形栈（每屏新增**按真实宽高比绘制的迷你玻璃显示器缩略图**）、07 进程 & 11 日志（磨砂圆角搜索胶囊与 `HeroPanel` 聚焦）、08 战术档案（3 列战术模板玻璃卡网格）。
- **验收**：零编译警告、单文件 `<= 793` 行、零 `DropShadowEffect`；`selftest 103/0`、`uitest 78/0`（11 页首建 `9–102 ms`）、`alloc-test 11/0`、`stress-test 8/0`、`daemon-test 56/4`（仅无桌面 `StageTray` 4 项环境限制，0 逻辑失败）、`backend-bench fail=0`、`--render` + `--tray-shot` 13 帧目视全检零副作用通过。详见 `REV120.md`。

---

## Rev 110 · 2026-11-19（AETHER CONSOLE 全栈旗舰升级：字段级 RegKv 事务 · 30 项调优矩阵 · 零分配三次平滑波形 · 交互式亲和矩阵与 36.6% 扫表提速）

- **后端内核、原生快控与性能跃升**：
  - **字段级 `RegKv` 原子事务引擎（`src/Ops.RegKv.cs`）**：支持 `DirectXUserGlobalSettings` 等分号键值字符串的字段级探测、合并写入、独立写前落账与独立还原，并发施加多项 DirectX 调优绝不覆盖用户原有键值（如 `AutoHDREnable`）。
  - **调优矩阵扩充至 30 项（`Actions.cs` / `Power.Ac.cs` / `Ui.ActionRow.cs`）**：新增微软官方核实的 5 项 Win11 游戏调优（`cpu-min-state`、`win-opt-dx`、`vrr-opt-dx`、`usb-power-ac`、`power-throttle-off`）；扩展 `powrprof.dll` `SUB_USB / USBSELECTSUSPEND` 与 `PROCTHROTTLEMIN` 原生直读直写，并为 GUID/RegKv 行展示可读标签。
  - **零子进程原生 Win32 快控（`src/Kernel/WinNative.cs`）**：新增 `dnsapi.dll` `FlushDnsCache()` 与 `user32.dll` `SystemParametersInfoW` 即刻参数广播（鼠标/键盘/菜单调优免注销即时生效），严格遵守只读与沙箱边界。
  - **进程扫描提速 36.6% 与零分配频率摘要（`ProcessScan.cs` / `Hardware.cs`）**：高水位缓冲复用 + 共享按名查找，将 `proc.find.name` 均值从 `17.54 ms` 降至 **`11.12 ms`（`P50 = 11.10 ms`）**；`Hardware.ReadCpuPowerSummary()` 复用非托管缓冲检测实时频率与限频状态。
  - **根治 `SessionHistory` 落盘并发锁竞争**：`SaveInternal` 补齐 5 次指数退避重试，`--daemon-test` 30 条上限边界从偶发失败修复为连续稳定通过。
- **AETHER 光学设计语言、零分配数据可视化与 11 页交互升级**：
  - **全链品牌重铸**：升级为 `AETHER CONSOLE (Rev 110)`，重铸 40ms 原生 GDI+ 启动闪屏（`SplashNative.cs`）与 9 尺寸应用图标（`IconGen.cs` / `assets/app.ico`）；全站页头统一升级带描边模块徽标 + 渐隐端帽线，`HeroPanel` 叠加顶部冰蓝机加高光棱与径向光场。
  - **四大图元升级**：`Waveform 3.0`（零数组分配 Fritsch-Carlson 单调三次贝塞尔 + `AVG` 均值标尺 + 5 层光学脉搏头，`42.4 B/帧`）、`ArcGauge 2.0`（深凹内刻度环 + 双层光弧，`308.6 B/帧`）、`CoreGrid 2.0`（CCD 平均负载 + 悬停逐核 HUD，`42.1 B/帧`）、`MemRibbon 2.0`（顶部高光棱 + 悬停分段拆解，`1.4 B/帧`）。
  - **11 页核心体验升级**：总览 4 枚状态胶囊全可点击且修复长方案名截断、嵌入实时独显与限频读数、支持残留一键修复及首屏完整对称排版；调优矩阵新增实时关键字搜索（`Tuning.Filter.cs`）与预设进度条并修平右卡边距；调度页升级可点击单核/整簇交互式亲和矩阵；内存页补充释放差额反馈；网络页修复 Y 轴 `Mbps` 刻度重叠并重绘带渐变面积填充与悬停十字准星的吞吐图；基准页新增透明口径 S/A/B/C 评级，还原中心支持 `RegKv` 过滤与快照导出。
- **验收**：`selftest 103/0`、`uitest 78/0`、`alloc-test 11/0`、`stress-test 8/0`、`daemon-test 56/4`（仅无桌面 `StageTray` 4 项环境限制，0 逻辑失败）、`backend-bench fail=0`、`--render` 11 页 + 命令面板 + 托盘全零副作用通过。详见 `REV110.md`。

---

## Rev 100 · 2026-11-18（NEXUS / APEX 旗舰控制台重构：原生 Win32 电源索引 · 全站 DropShadow 清除 · 流体动效与仪表光场）

- **后端内核与稳定性重构**：
  - **原生电源子项直读直写（`Kernel/Power.Ac.cs`）**：新增 `powrprof.dll` P/Invoke（`PowerReadACValueIndex` / `PowerWriteACValueIndex`）及 `SUB_PROCESSOR / SUB_PCIEXPRESS / SUB_DISK` GUID 映射表，接入 `Ops` 探测/写入/还原优先路径并受 `Power.ReadOnlyMode` 保护；`--backend-bench` 实测 `power.ac.native` **`P50 = 188.7 µs`**（平均 `211.1 µs`）。
  - **零句柄进程名查找（`Daemon/ProcessScan.cs`）**：新增 `FindPids(string)` 与 `FindPidsByName(string)`，复用 `NtQuerySystemInformation` 扫表，彻底清除 `AutoPilot.cs` 与 `pages/Profiles.cs` 残留的 `Process.GetProcessesByName`。
  - **页面托管定时器与共享基元（`Pages.cs` / `Chrome/Shell.Nav.cs` / `Ui.cs`）**：新增 `Page.TrackTimer` / `StopTimers` / `LeavePage`，`Shell.Nav` 离页自动停表防止计时器泄漏；抽取 `Ui.Hairline` 统一分割线并清理 `Benchmark.cs` 重复实现。
  - **合成器零离屏阴影**：清除 `Btn.cs`、`Overview.Power.cs` 与 `TrayMenu.cs` 残留 `DropShadowEffect`，全面改用精密 1px 描边与冷暗分层。
- **前端设计系统、动效与视觉层级重构**：
  - **Obsidian 光场基底与阳极氧化外壳（`Theme.cs` / `Ui.cs` / `Shell*.cs`）**：新增 `Ui.HeroPanel` 径向环境光卡并接入总览与基准主面板，配合 `Shell` 对角深邃晕染与 `32×32` 阳极氧化商标框；升级 `NavRow` 3px 选中柱；「游戏模式」顶部胶囊空闲态隐藏灰点、激活态右点亮翠绿指示灯；Toast 升级 `3px` 左侧语义状态条；`Ctrl+K` 命令面板支持 150ms 缩放上浮入场动画。
  - **滑轨 `SegTabs` 与仪器光晕（`Ui.Kit.cs` / `ArcGauge.cs` / `Waveform.cs`）**：`SegTabs` 新增带 `150ms QuinticEase` 位移宽度的滑动底座与 2px 底部针脚（离线与降低动效模式零帧直达）；`ArcGauge` 指针尖端与 `Waveform` 最新采样点加入零分配冻结画刷外晕 halo，`--alloc-test` 下分别保持 `266.2 B` 与 `41.0 B`。
  - **页面布局校准**：`Overview` `TIMER RES` 补齐 `SegBar` 实现四格视觉对称并升级「打开调优矩阵 →」行动按钮；`Tuning` 预设全部就绪时展示翠绿「已就绪 ✓」。
- **品牌与构建**：`Brand.Rev = 100`、`AssemblyInfo` 更新至 `100.0.2026.1118`，重建 `tools/IconGen.exe` 与 `assets/app.ico`；详情见 `REV100.md`。

---

## Rev 90 · 2026-11-18（全面产品化升级：前端美学与动效精修 · 后端隔离与归档闭环 · 安装/卸载工程化）

- **前端美学、动效与性能精修**：
  - **合成器零离屏负担按钮（`Btn.cs`）**：移除所有按钮上的 `DropShadowEffect` 离屏 Direct3D 合成纹理分配，改用顶部机加高光 `_sheen`、1px 分层描边与按压位移；微调 `Motion.cs` 补间池至 `64` 槽位与 `QuinticEase` 阻尼，保持 `--alloc-test` 全项通过（`Motion.Step = 0 B / 500 帧`）。
  - **总览与仪表读数精修（`ArcGauge.cs` / `Overview*.cs`）**：仪表中心字号收敛至 `44` 适配环内留白；总览页顶边距对齐 `16px` 消除切页 4px 跳动；电源方案选择钮去掉突兀英文 `PROFILE` 方框，改用状态指示点，并在下拉底注诚实标明「用户手动选定方案退出后保留」。
  - **11 页信息密度与细节校准**：`Tuning` 修复预设 1 说明孤字换行；`ActionRow` 对已符合项直接展示 `已符合 · <目标值>` 消除 `2 → 2` 冗余箭头，提升只读开启态开关可视对比度；`Sparkline` 右对齐 `NOW` 时间轴；`Benchmark` 顶部改示 `HISTORY / BEST`（与主卡 `THIS RUN` 互补去重）并扩大直方图顶距（`PadT=32`）+ 三层 `MEAN / P50 / P95 / P99` 标注防碰撞排布；`Restore` 列头「风险」正名为「状态」，新增单条「保留」与页头「全部保留」归档能力（支持将确认满意的系统状态安全移出待还原账本并留审计日志，不误改系统）。
- **后端修复与只读安全隔离**：
  - **`--render` 真正零副作用**：`Power` 新增 `ReadOnlyMode`（离屏渲染与托盘预览下禁止更改/恢复活动电源计划）；`Network.Sample` 在 `HeadlessRender` 下跳过背景定时器与双路径 ICMP/TCP 探测。
  - **电源计划选择语义纠偏**：`Power.SetActive(Guid)` 默认 `trackTemporary = false` 持久化用户主动选定方案并清除过期恢复点；仅自动巡航临时提档开启 `trackTemporary = true`。
  - **`NetTrace` O(1) 单趟进程名解析与 GPU 去重**：移除 `Process.GetProcessById(pid)` 慢速回退改由 `ProcessTable` 单次批量补齐名；`Hardware.ReadAdapters` 通过 `FindPhysicalTwin` 合并笔记本双显 MUX/Optimus 同卡重复 LUID，`IsVirtualGpu` 独立识别并标注「虚拟」显示适配器（如 MuMu / Basic Render），不再混入物理核显计数。
  - **品牌与元数据统一**：`Brand.Rev = 90`，`AssemblyInfo` 统一为 `GameOptimizer Studio`，重建 `tools\IconGen.exe` 重新生成 9 尺寸 `assets\app.ico`。
- **部署与卸载工程化（`tools/deploy.ps1` / `tools/uninstall.ps1`）**：
  - 新增干净卸载脚本 `tools\uninstall.ps1`（终止进程、注销快捷方式/自启/卸载项、清理程序目录、默认安全保留 `%LOCALAPPDATA%\GameOptimizer` 写前账本并支持 `-PurgeUserData`）。

---

## Rev 89 · 2026-11-17（全栈深度清创与优化：进程表双缓冲修复 · 非阻塞探针 · 11 页生命周期与边界加固）

- **排查与清创方法**：在 Rev 88 部署上线后，对运行态进程、`%LOCALAPPDATA%\GameOptimizer` 产物与全仓 100+ 个 C# 源文件执行全栈法医级排查，定位并修复全部 P0/P1/P2 隐患；全仓单文件严格 `<= 793` 行，零编译警告，零空 `catch {}`。
- **P0 · `ProcessTable` 双缓冲复用字典对调修复**：修复 `RefreshInto` 将复用池固定传入 `_cacheB` 导致 `_a`/`_b` 跨代共享 `ProcEntry` 实例的错位问题；新增独立 `_staging`/`_peer` 池，并发读取零污染，单次刷新堆分配从 **4196 B 降至 76 B（-98.2%）**。
- **P0 · `Gpu` / `NetTrace` 非托管缓冲安全重分配**：`Gpu.EnsureCap` 与 `NetTrace.ReadTcpByPid` 改为先申请新 `AllocHGlobal` 成功后再释放旧缓冲，彻底消除 OOM 时重复释放导致的堆损坏风险；`ProbeTcpPort` 显式关闭 `AsyncWaitHandle`。
- **P0 · 游戏扫描器白名单收敛与 `--render` 零污染**：`GameScanner` 拦截浏览器 PWA（`chrome_proxy`/`msedge_proxy`）、开发/AI 工具与 `http/https` 网页书签，仅保留真实游戏快捷方式与 `steam://` 协议；`BenchHistory` 新增 `ReadOnlyMode` 与 `_ioLock`，`--render` 离屏截图不再向用户 `bench-history.json` 追加合成记录。
- **P1 · 非阻塞电源探针与页面首建提速**：`Ops`/`Engine` 新增 `ProbeForUi` 与 `RefreshPowerAcCacheAsync`，消除同步启动 `powercfg.exe` 的 UI 阻塞；总览页首建从 `122ms` 降至 **`8ms`**，系统调优页从 `122ms` 降至 **`40ms`**，图形页异步枚举显卡与显示器。
- **P1 · 进程 PID 复用校验与 11 页生命周期闭环**：`ProcessPower` 新增 `SetEcoQoSIfMatch(pid, expectedBorn, ...)`；`GameWatcher.ForceAttach` 切换前台游戏前先解绑还原旧游戏；修复内存页 `_ribSig` 64 位溢出、网络页多网卡展开共用 `AuditRow` 失灵、还原中心 `ActionRow` 无账本记录误报“已还原”、审计日志 `fail=0` 误判警告等全部页面级缺陷。
- **测试**：`selftest 92/0`、`uitest 74/0`、`alloc-test 11/0`、`stress-test 8/0`、`backend-bench fail=0`、`daemon-test 56/4`（仅孤立桌面 `exebox-*` 无 Explorer 托盘宿主导致 `StageTray` 4 项已知环境限制）。详见 `REV89.md`。

---

## Rev 88 · 2026-11-14（全栈多卡遥测 · Win11 EcoQoS · 多源发现 · 11 页交互与视觉精修）

- **协同方法**：双波次 `git worktree` 并行开发（Wave 1 后端核与 UI 基础层合入并验证后，Wave 2 外壳与 3 组页面并行合入 `rev88-main`，最终由独立只读 `final-verifier` 检出并复验修复 `1000×680` 窄屏两处文字硬裁切）。101 个文件改动，全仓单文件 `<= 796` 行，源码零空 `catch {}`。
- **后端升级**：`GpuSampler` 逐物理 LUID 遥测 + 无 WMI 显示输出枚举 `ReadDisplays()`（`2560×1600@240Hz`）；`Affinity.PhysicalOnlyMask()` 与 `ProcessPower`（Win11 `SetProcessInformation` 可逆 EcoQoS + 优先级管理）；`PowerMonitor` 升级 `WM_POWERBROADCAST` + `RegisterPowerSettingNotification`（含 `PowerSaverActive`）；`GameScanner` 拓展 `WeGame` / `Xbox` / `桌面` 快捷方式；`SessionHistory`（30 局持久化与损坏隔离）+ `GameProfile` 策略字段；`NetTrace`（`GetExtendedTcpTable` 进程活跃连接 + LAN/WAN 双延迟 + TUN/VPN 识别）；`BenchSuite`（真实指针追逐内存基准 + 跨线程事件往返乒乓基准）；修复 `DaemonTest` `StageWatch` 并发竞态与 Win11 65001 代码页下 `powercfg` 解析。
- **前端与体验**：修复 `Ui.Spaced()` ASCII 单词粘连与 `09-bench` 表头 `NNOTE` 碰撞；新增共用 `Ui.SegTabs` 分段器、零分配 `Waveform` 悬停/叠线与三态动态托盘图标（`Idle`/`Guard`/`Alert`）；顶栏加入可点击「一键游戏模式」胶囊与 `AC/电池/节能` 芯片，侧栏底端新增 4 主题快速切换色点；全 11 页接入对应 Rev 88 实测遥测、过滤与行内精度控制，并修平 `1000×680` 窄屏下总览 P99 与图形页脚注裁切。
- **测试**：`selftest 85/0`、`uitest 69/0`、`alloc-test 11/0`、`stress-test 8/0`、`backend-bench fail=0`；`daemon-test 52/4`（仅在孤立桌面 `exebox-*` 复现无 Explorer 导致的 `StageTray` 4 项已知限制，其余包括 `StageWatch` 全部通过）。详见 `REV88.md`。

---

## Rev 87 · 2026-11-13（后端地基重建：常驻尺度上的崩溃、空转与丢记录）

- **方法**：五路只读子代理并行审计（采样内核、守护、写入账本、生命周期/托盘、页面数据层），我逐条复核 → 按"会在真实环境造成什么后果"排序 → 分域重构，每批 `build + 对应自检` 闸门，最后全量 `verify`。不碰视觉。
- **P0 · 后台线程异常会杀进程**：`Perf.Loop` 只有 `try/finally`，`WaitTurn/Done` 在保护外；.NET 2.0 起采样线程上的未处理异常**终止整个进程**。GPU 采样同一缺陷且 `OnBeatError` 自身无二次保护。改成「内层一拍一兜底 + 500ms 后自愈」，抖动探针同样自愈（旧实现一次异常就永久没有抖动读数）。
- **P0 · 进程表扩容先 Free 后 Alloc**：`AllocHGlobal` 抛 OOM 时 `_buf` 是悬空指针、`_cap` 却已变大，下一拍把悬空指针交给内核写 → 堆破坏。改成先分配新块、成功后才交换；补终结器、内部锁、条目长度校验。
- **P0 · 守护线程关掉开关后单核空转**：`_wake` 是 `ManualResetEvent`，全类只在 `Start()` Reset；一次 `Set()`（关闭守护 / Reload）后 `WaitOne` 永久立即返回。改 `AutoResetEvent`，并加两条断言：250ms 节拍稳定、关闭后一秒内 CPU 增量 < 400ms。
- **P0 · 退出管线方向与注册顺序相反**：管线按逆序执行，而 `DaemonHost` 按执行顺序注册 → 托盘最后摘、会话锁最先放、崩溃落点先删（复原失败时再无依据）。改为「注册顺序 = 执行顺序」+ 8 秒总预算 + 三个 critical 步骤，断言钉死 `tray → revert → clear → lease`。
- **P0 · `--render` 会动真实账本**：渲染进程不占单实例锁却会构造 `Ledger`，账本损坏时 `Load` 会 Quarantine + Save。现在 `--render` 把账本钉成只读；读失败改为**粘性**（文件消失也不解除）；落盘前比对内容指纹拒绝覆盖外部改写；`Replace` 重试；崩溃残留的完整 `.tmp` 会被采纳。
- **写入吞吐 · 批量事务**：旧路径每个 op 一次 `FlushFileBuffers + File.Replace + 整份 JSON 重序列化`（8 动作预设 13 次 fsync 压在 UI 线程）。`Ops.ApplyBatch` 改为「探测全部 → 一次记账落盘 → 逐个写入校验 → 任一失败逆序回滚」，整个预设只付一次 fsync；`Ledger.Add` 返回真正生效的记录（修掉去重后回滚 `Remove` 打空留下的幽灵条目）。
- **共享进程表**：进程页每 2s `Process.GetProcesses()` + 每进程 3 次 syscall（实测 **26.8ms/次**、432 个对象、约 1300 次/秒句柄），内存页再做一遍。新增 `ProcessTable`：一次 `NtQuerySystemInformation` 遍历、双缓冲发布、名字按 `(pid, born)` 跨代复用。进程页与内存页改读它；内存页「私有」只对可见 5 行开句柄（430 → 5）。实测扫描 **8.79ms**、缓存读取 **0.1µs**。
- **常驻功耗**：抖动探针冷态从 50ms 轮询（20 次/秒唤醒）改为事件驱动；`BootDone / JitterHot / Dormant` 三个 setter 都会叫醒它。没有页面看抖动时**零唤醒**。
- **其余加固**：关停后 `DaemonHost.Start()` 直接返回（Background 投递可能在 Shutdown 之后执行）；`PowerMonitor` 的读-判-写进锁、`Stop→Start` 先 Join 旧线程；`AutoPilot` 崩溃落点先于任何系统写入、失败即放弃挂载、复原失败保留落点、电池下不重取 0.5ms、重试改单实例 Timer；`AppState` 关停期不再 Post、不在后台线程就地 Flush、同线程重入改为回填掩码；托盘放行 `TaskbarCreated` 的 UIPI 并加 20s 存活探针；`RefreshAllPages` 清空 `_wraps`（旧实现让其它页停在旧树且订阅数为 0）；`Jank` 的毫秒换算改整数并饱和（>35 分钟挂起后「最长停顿」不再记成 0）。
- **测试**：**183 项全绿**（self 69 / ui 56 / daemon 39 / alloc 11 / stress 8）。新增：进程表字段自洽与跨拍名字复用、账本只读/污染粘性/外部改写、批量事务回滚、守护节拍与空转、真实退出顺序、供电并发与重启。另加 `--backend-bench`（可复现的六段微基准）。详见 `REV87.md`。

---

## Rev 86 · 2026-11-12（仪器读数语言 + 三条后端地基重建）

- **方法**：先定契约、再并行。我先把"读数语言"（`Ui.Instrument`：Readout / DeltaChip / Tag / RowShell / Stat）与两页样板（总览、调优）落地并提交，然后开 7 个 git worktree 分头做——3 路后端（采样内核、写入账本、进程生命周期）、4 路前端（每路 2~3 页）。写入范围互不重叠，各自 `build + verify` 全绿后我逐支合并、逐支验收。
- **前端 · 读数语言**：单位沉到数值基线（旧代码 6 处手拼，时高时低）；变化量芯片**方向与好坏分开判色**（CPU 上升是坏事、可用内存上升是好事），无基准时占位不显示；徽标收成描边/实底两档；行外壳左缘 2px 风险色，替代"先背 A/B/C 再逐行翻译"的字母胶囊。列宽常量进共享层（112/96/56）——此前调优/档案/图形 110/92、网络 112/96，切页时那 2~4px 的错位看得见。
- **前端 · 十一页**：总览四格升级为"小仪表"（读数 + 变化量 + 32 拍趋势 + 注脚）；调优的"三张卡 + 一个洞"改成一条四段工具带，每行去掉复读的"已应用"；调度 3 套区块头收敛为 1 套 + 顶部读数带 + 矩阵三态；内存两套构成条并成一条 MemRibbon；网络 11 分片 → 7 分片（删掉 6 个重复画法：卡片表/独立 TCP 表/两套 hero）；进程 9 列 → 5 列、804 行拆成两片、表头与行共用一份列定义；档案卡片墙 → 行网格 + 抽屉；基准加四步状态机（运行中给"已用/预计剩余"，没样本写"计时中"不画假进度）；还原中心时间线 → 六列行网格；审计日志表头与行两套几何并成一套。
- **前端 · 诚实性三处**：波形窗口固定 30s，刚打开时左半没数据——旧渲染只画右段，读者以为图坏了，现在压暗未采集区 + 画采集起点竖线；前 9 拍没有基准时变化量不显示 `0.0`；字号下限 9/10/11.5/12.5/14 → 10/11/12/13/15（大数字一个没动，只把注脚提到读得清）。
- **后端 · 采样内核**：`SampleClock` 绝对时刻排程 + `WaitHandle.WaitAny` 取代 6 处 Sleep 轮询，`Stop()` 退出 **645ms → 0ms**（GPU 999ms → 0ms）；`Perf.Loop` 139 行/嵌套 7 → 18 行 + 7 个读取器，锁 6 → 1；新增 `SampleHealth` 账簿（层/次数/状态码/失败阶段/下次重试），旧实现失败时静默返回 0 或保留三拍前的旧值——界面显示一个看起来正常的数字；GPU 句柄单一出口 + 5s→60s 退避 + 三缓冲复用。
- **后端 · 写入账本**：`Apply` 改成显式步骤状态机（探测→记录→写入→校验），失败停在最后一个已提交步并带 `Step/Committed/RolledBack`；修掉三个真缺陷——`Engine.Apply` 的 `foreach` 不 break（第 2 个 Op 失败时第 3 个照写）、批量失败不回滚、账本落盘失败留下"从没发生过的改动"；账本 v2（版本封套 + 逐级回退截断抢救 + 同秒损坏按序号留证 + 读写锁分离）；还原路径修掉 `Reg` 读失败被判成"已不存在"从而静默删记录的老陷阱。
- **后端 · 进程生命周期**：`App.Run` 319 行/37 try/39 catch → 五阶段，异常一处呈现；四条退出路径 → 一个逆序、带超时、幂等的管线，托盘图标固定第二步删除并在 `ProcessExit` 再兜一次；守护归属从"PID 看起来还活着"改成命名互斥体句柄（进程死则自动释放），`AppBorn` 纳入判活；托盘 `WndProc` 拆开、`TaskbarCreated` 重注册可重入、菜单弹出失败清理重试。
- **顺手修的真 bug**：还原中心的风险分级判的是 `Kind == "Svc"/"File"`，而账本只有 `Reg/Bcd/PowerAc/Hibernate/Command` —— 那两个分支永不命中，风险分级实际上不存在。
- **测试**：**168 项全绿**（self 59 / ui 56 / daemon 34 / alloc 11 / stress 8）。新增 10 条读数语言断言（单位同基线、变化量自隐藏与判色、行外壳色条列宽、两档徽标权重、统计格五层）+ 6 条守护生命周期断言（PID 复用、会话租约、退出管线逆序与超时）。详见 `REV86.md` 与 `DESIGN-86.md`。

---

## Rev 85 · 2026-11-12（滚动条位置：Stretch + 固定尺寸 = 居中）

- **用户报**：滚轮滑动时右侧滚动条滑块位置不对。**真因**：滑块只设 `Margin.Top`，而 `VerticalAlignment` 是默认 `Stretch` —— WPF 把剩余空间当"居中"分配，真实位置 = margin + 居中余量。顶部时滑块出现在轨道 1/3 处，往下滚追不上内容，到底才对齐；内容越长偏得越厉害（短页看不出来，所以一直没被发现）。
- **修法**：`_thumb.VerticalAlignment = VerticalAlignment.Top`（一行）。实测 Y 与 margin 完全一致且线性；窗口缩放、页面重挂载都正确重算。
- **为什么没抓到**：旧断言只验"滑块存在/可拖动"，从没验过真实位置。新增 3 条断言直接量 `TransformToAncestor` 的 Y；并先用旧代码验证断言会红（`顶部 :: 175.8`、`中点 :: 262.8`、`底部 PASS`）——底部本来就对，与分析吻合。
- **测试**：**152 项全绿**（self 59 / ui 46 / daemon 28 / alloc 11 / stress 8）。详见 `REV85.md`。

---
## Rev 84 · 2026-11-12（后端总清算：48 个真缺陷，3 个 P0）

- **方法**：六路只读子代理并行审计（守护/托盘、写入引擎/账本、页面数据层、内核采样、外壳/控件、测试工具链）+ 我逐行复核写入路径 → 逐条亲自验证 → 分域修复 → 每批 `verify` 闸门。不碰视觉。
- **P0 · 全部还原永远是失败**：`rep.Ok = rep.Ok && rep.Failed == 0` 是自赋值（`Ok` 初值 false 且全程无人置 true），10 项全成功也返回失败，界面显示「有 0 项未能还原」。
- **P0 · 提权重启从未生效**：新进程抢单实例锁时旧进程还握着它（`Close()` 只进托盘），`created=false` → 立刻退出；旧进程随后缩进托盘。现在新进程带 `--wait-pid <旧pid>`，旧进程放锁后 `Shutdown()` 完成交接。
- **P0 · 关掉的策略照样生效**：`Append(p.HighGpu, "GPU", AttachGpu(p))` 参数照常求值，独显/亲和/定时器三项无条件执行，战报却写「未配置策略」。改成先判开关再求值。
- **读数诚实**：`Ops.Run` 用 UTF-8 解码中文系统上 OEM(936) 的 `powercfg/bcdedit` 输出（探针恒判"不存在"）；同标签同行的电源值被 `continue` 跳过；回读失败被当成"原值未改变"导致账本记录被删；目标已成立仍落账（一次"全部还原"会删掉工具没改过的值）；休眠还原漏了快速启动。
- **守护**：手动挂载时 `_activePid<=0` 整段判活被跳过（状态永不解挂）；档案只比 `Count`（改 exe 永不生效，新增 `ProfileStore.Version`）；判活只比 PID（PID 复用后策略不回滚，补创建时间）；`Shutdown` 在 Join 超时后仍 `Recorder.Dispose()`（use-after-free）；多开时删掉对方崩溃复原所需的落点；通知文案写死「系统参数已复原」。
- **并发**：定时器租约位域 `_owners` 无锁读-改-写（会丢位：钉在 0.5ms 或撤掉别人的租约）；Detach 在 UI 线程持 `_scanLock` 做注册表回滚（界面冻结）；`SessionStore` 临时名写死 `.tmp`（并发 Save 撞车丢落点）。
- **采样**：`NtQuerySystemInformation(8)` 忽略 `ret`（核数不符时 CPU 恒 0；注意该 API 要**精确长度**，多给同样返回 `0xC0000004` —— 我第一版改错、实测把 CPU 打成恒 0，改成「按 ProcessorCount 传、失败用 `ret` 重试」）；抖动线程起手 `Highest` 且降温分支永不执行；`ReadKernelPerf` 隔拍读导致界面周期性闪 `PROC 0 / 池 0 MB`；抖动积压样本被静默丢弃；`Jank.Begin` 用永不返回 null 的 `Dispatcher.CurrentDispatcher` 判可用性（死代码）。
- **内存**：三个页面的动作行列表重建时不清空（行数翻倍、`22/22` 变 `0/44`、Probe 次数倍增，随重建无界增长）；恢复页无上限渲染（限 200 条并注明剩余）；待机胶囊每秒重建；沙箱账本目录无界增长（启动清 2 天前的）；`--uitest` 会覆盖用户真实 `ui.json`（备份/还原）。
- **静默失败**：全仓空 `catch { }` **78 → 0**，每处要么写明理由，要么换成 `Diag.Fail/Hit`。
- **度量工具**：`verify.ps1` 先删旧报告并校验时间戳（否则上一轮 fail=0 会被当本轮成绩）；`build.ps1` 换不到目标时 `exit 0` → `exit 2`（旧 exe 被拿去跑全绿）；`deploy.ps1`/`apply-update.ps1` 失败仍报成功；报告写不出去不再返回 0；`-like "*[FAIL]*"` 是字符类（改 `-match`）；`Start-Process -ArgumentList` 不加引号；分配测试分母用累计 Tick 且写回快照；Toast 断言固定 3800ms 造成 flaky（改有界等待）。
- **测试**：**149 项全绿**（self 59 / ui 43 / daemon 28 / alloc 11 / stress 8）。详见 `REV84.md`。

---
## Rev 83 · 2026-11-12（一个异常类型，让「修复」永远是假的）

- **起因**：用户截图问「它说有一项第三方优化需要重置，是什么东西」。查下来：横幅的告警**是真的**（这台机器确实被一键优化工具改过 8 处非默认值），但工具自己**修不掉**其中第 8 项 —— 删一次、回滚一次，值原样回来，横幅永远挂在那里。
- **根因**：`Reg.Read` 判「值是否存在」只接 `ArgumentException`。实测 .NET Framework 4.8 抛的是 `System.IO.IOException`（`HResult 0x2 = ERROR_FILE_NOT_FOUND`，消息「指定的注册表项不存在」）。于是每个**本来就该不存在**的值都被读成 `(读取失败)`。
- **连锁后果**：① 删除型动作（删 `Dwm\OverlayTestMode` 恢复 MPO）删成功也判失败，`ValueIsOriginal` 跟着判错 → 走回滚 → `Revert` 把用户的第三方残留**重新钉回去**（审计日志里 7 次 `ROLLBACK … 写入后校验不一致：(读取失败)` 就是它）；② 每次探测都往 `Diag` 记一条 `reg.read` 假故障，把真故障淹掉；③ 页面把「读不到」当状态显示，而不是「没配过」。
- **为什么 148 项没抓到**：自检里那条 `Check(!before.Existed, "删除后读到「不存在」")` —— 读失败时 `Existed` 也是 `false`。**尺子量不出它要量的东西**。加固为 `!Existed && Error == null`，并新增「整键不存在」一条（self 58 → **59**）。
- **改动**：`Reg.cs` 补 `catch (IOException)` + `HResult & 0xFFFF == 2` 判「不存在」（其余 IO 异常仍上抛，保留「没有 / 读不到」的区分）；`SelfTest.cs` 两条断言加固；版本三处归位 83。
- **现场处置**：本机 8 处第三方残留，工具 14:22 修好 7 处（`0x28→2`、`14→20`、`6→2`、`High→Medium`、`1→0`、`4→2`、`1→0`），第 8 处（禁 MPO）我以管理员删除并给出撤销命令；托盘重启后横幅在 10 秒复判周期内自动消失。
- **测试**：**149 项全绿**（self 59 / ui 43 / daemon 28 / alloc 11 / stress 8）；部署位与 `out\` sha256 一致。详见 `REV83.md`。

---
## Rev 82 · 2026-11-12（后端深优化：守护不会悄悄死）

- **方法**：四路只读子代理并行审计（守护/内核/启动/数据层）→ 逐条亲自复核 → 分四阶段修 → 每阶段五套自检闸门 → 离屏渲染视觉回归。不碰视觉风格。
- **P0 无锁竞态**：`ProfileStore.Items` 公开 List 无锁，巡检线程按旧 Count 索引、UI 同时 RemoveAt 移位 → 越界异常终结巡检线程、守护静默失效而界面仍写"守护中"。收敛为私有列表+锁+Snapshot 快照+Diag+损坏隔离+Guid tmp+批量单次落盘；10 处调用点改新 API。
- **P0 守护生命周期**：巡检循环包异常边界；Stop 事件唤醒+有界 Join；挂载期单遍历；扫描失败≠进程退出（不误复原）；关守护即解挂（开关不再弹回）；ForceAttach 解析真实 PID（修托盘档案切换 1 秒自动回滚）；PollOnce 与巡检串行化共用缓冲；休眠降频 250→1500ms。
- **P0 关停/跨线程**：Shutdown 先 Join 巡检再收尾战报（修 _accum 并发迭代）；OnPowerChanged 经 Post 搬回 UI（修电池切换跨线程崩溃）；游戏事件加关停闸门；AutoPilot._last 加锁+序号 Interlocked。
- **P0 战报诚实**：SessionRecorder 按 JitterLive 门控抖动统计（旧把冻值当实时，挂机秒数变"捕获 N 次抖动"）；结论区分"未采样/平稳"；_accum 清死 PID；缓冲真交换。
- **P1 读数诚实**：7 处页面把 Reg.Read 读失败渲染成"不存在/已启用"（MPO 尤甚）→ 补 Error 分支；Reg.Read 存在性改单次 GetValueKind（不再枚举整键）；Power 恢复点原子写（修断电 0 字节击穿恢复）；Jank warm 前置；Topology len<10；ProcName 超长重试。
- **P1 采样开销**：Gpu 休眠判定前置（旧收托盘反而 2Hz）+按拍 try/catch+字典/解析缓存复用+x64 守卫；Perf 冷→热清环+冷态跳统计+首帧闸门 BootDone（show 段不再与渲染抢核）；Overview/Scheduler 抖动按 JitterLive 显示 —。
- **P1 启动**：ToolTip 惰性构造+删 Loaded 同步测量（最大杠杆，导轨 14 个+各页数十个移出首建）；闪屏 Close 去 WaitOne(50) 挪到首帧后；删死代码 WPF Splash；DeckBrush 缓存；图标预热补缺；BuildRail Boot.M 细分；--render 打印 boot marks（无头安全测量）。
- **P2 泄漏/死代码**：Motion.Step 清 Apply（修 32 槽持有已关闭菜单视觉树）；TrayMenu 每行缓存补间委托；TrayHost 提升定时器纳管；删 Engine.Changed/BusSnapshot 每域死访问器/TrayProbe.Log；Engine.Probe out 重载+ActionRow 复用首 Op 读数（去双探针）。
- **自我纠正**：审计误报"定时器范围写反"，我改后离屏渲染抓出反转——实为 NtQueryTimerResolution 命名陷阱（MinimumResolution=最粗）。改 Math.Min/Max 与命名解耦，渲染复核 0.5 – 15.6 ms。
- **测试**：**148 项全绿**（self 58 / ui 43 / daemon 28 / alloc 11 / stress 8）；11 页离屏渲染无错误。详见 `REV82.md`。

---
## Rev 81 · 2026-11-12（先造尺子，再谈流畅）

- **新尺子**：`Kernel/Jank.cs` —— UI 线程 15ms 心跳，间隔 >38ms 记一次 stall（带相位标签）。步进 15ms 是实测出来的（16ms 会踩 15.625ms 节拍的双峰）；`--diag` 新增 `--- jank ---` 段与 `--diag-secs <n>`。
- **量到的结论**：全部成规模的 UI 停顿（5~7 次/30s、最长 102ms）**来自 Rev80 加的空闲预建**，且每条都能与「预建那一页自己的构建耗时」逐条对上（102↔101、90↔87、70↔68、48↔47）。1Hz 节拍整拍仅 0.3ms，`tick:`/`beat:misc` 相位 0 条停顿。
- **删掉空闲预建**：那 50~110ms 从没消失，只是搬到了「用户正在看首屏的头十几秒」。首访成本要靠把页面建得更快，不能靠挪时间。
- **抖动探针按需热跑**：`Highest + Sleep(1)` 永久循环空闲吃 11% 单核、把 UI 节拍 p95 抬到 26~28ms；改成只有显示抖动的页面可见时热跑，其余 `BelowNormal + 50ms`。配套 `Snapshot.JitterLive`，没采样时标题栏显示 `JIT —`。
- **结果**：UI 停顿 5~7 次/30s → **1 次**（启动首屏构建）；节拍 p95 26~28 → **18ms**；空闲 CPU 0.34% 整机 → **测不出**；托盘工作集 33 MB。
- **静默失败收口**：字面 `catch { }` 44 → **0**；带理由的静默 catch 93 → 28；上报 Diag 3 → **127**。顺带挖出并修掉三个真缺陷：`Ops` 读原值失败仍继续写（还原会删掉用户旧值）、`Engine` 保存失败仍报成功、`AutoPilot` 复原失败仍报已复原。新增 11 条断言（账本沙箱 4、Diag 容量 4、线程退出等）。
- **动作行去重**：`Ui.ActionRow`（580 行）取代三页各写一遍的实现 —— Tuning 713→489、Graphics.Actions 208→109、Network.Actions 233→106，合计删 450 行；三页现在共用同一份「失败必回真实状态」的反馈。Processes 的亲和性收敛到 `Kernel/Affinity`。
- **千行页面拆开（纯搬移）**：Memory 1265 → 7 文件（最大 305）、Scheduler 1130 → 6 文件（最大 274）、Overview 891 → 5 文件（最大 305）。用「还原成单体作基准 + 两份 exe 交替渲染」验证：像素差异 ≤ 同版本重渲噪声，面板边框位置完全一致。
- **总览节拍减负**：484us → **264us**（冻结读数对照 362→141）；`Power.SyncInto` 2s→5s、`CpuCurrentMhz` 每拍→4s；说明行/统计标签/矩阵全部加变化检测；分配 6.8KB/拍 → **约 24B/拍**。
- **定时器读数不再撒谎**：`NtSetTimerResolution(0.5ms)` 常返回成功但只给 1.0ms，旧代码照单全收。现在只如实返回系统真值，界面显示「请求 0.5 · 实测 1.0」，断言改成「报告值必须等于系统当前值」（与机器状态无关）。
- **启动**：`daemon.Start()`（37~53ms）移出首帧路径；`--timing` 改在 `ContentRendered` 落盘并新增 `first_frame`（旧的 `first_layout` 恒为 0，是废数）。实测 show 的 61~74% 是 WPF 自身地板。
- **测试**：**148 项全绿**（self 58 / ui 43 / daemon 28 / alloc 11 / stress 8）。详见 `REV81.md`。

---
## Rev 80 · 2026-11-12（INSTRUMENT GRADE II · 总线、铁律与四页重造）

- **外壳**：导轨中段那片 400px 纯黑改成 **SYSTEM DRIFT**（账本待还原条目数 + 3px 比例尺 + 语义色 + 点击进还原中心）；折叠导轨时隐藏。
- **页头**：11 页共享的 `Ui.PageHeader` 从「三级标题」改成仪表铭牌 —— 标题与模块号同基线，下压一条贯通全宽的刻度线（56px 强调色端帽）。
- **审计日志**：整行文本 → **TIME / KIND / EVENT** 三列（级别按语义着色）；修掉「从不 ScrollToEnd」（永远停在最旧一屏）、「内层滚动条从不出现」（星号行被无限高度测量，220 行撑出 4000px）、「系统浅色滚动条」；页脚新增 **内部降级 N 次**（Diag 计数 + 悬停看堆栈）。
- **自检不再污染账本**：所有测试/渲染模式在 `new Core()` 前调 `Ledger.UseSandboxDir()`，账本与审计落到 TEMP；实测跑一次 uitest 真实审计字节数不变（62023 → 62023）。
- **内存页（重做）**：标题改「内存与提交」；hero = 提交压力（45.1/50.1 GB = 90.0% → 危险）+ 物理分层缎带（已用/待机/已修改/空闲，按真实 MB 比例）+ 内核池量条 + 提交刻度尺 + 吃内存前 5 名（5 秒后台扫）+ 三个写明副作用的动作。
- **调度页（重做）**：硅片矩阵成为主角（横贯 1236×144，CCD 间 26px 物理间隙 + 簇标注 + 每簇负载与 60s 走势）；**把亲和性掩码画在矩阵上**（排除的处理器压暗 + 45° 斜线），「锁定 CCD0」因此可见；hero = 内核定时器颗粒；参数审计改对齐表格。
- **调度页热路径**：注册表打开 163 → **37** 次/30s，Process.GetProcessById 60 → **3**，刷新自身耗时 70.7 → **11.7 ms**（注入计数器的实测）。新增 `Kernel/Affinity.cs` 收敛 5 份拷贝（并修掉 `1L << 64` 静默折返的掩码 bug）。
- **网络页（拆分+重做）**：805 行 → **11 个文件，最大 209 行**；首建 **554 → 97 ms**（根因是三段同步 IO：枚举网卡 / ICMP 网关 / 注册表；改为后台线程 + 门闩 + 5s/15s 慢拍）；hero 上下行 Mbps + 60s 双线走势 + 适配器卡 + 网关往返 + TCP 审计 + 动作撤销路径。
- **基准页（重做）**：直方图根因修掉（24 箱钉在整数档导致样本全落最后几箱）→ `[min,max]±5%` 定域 + 48 等宽箱 + P50/P95/P99/MEAN 竖标记；长尾场景补 **P5–P95 放大带**（不裁剪任何样本）；判定改成可被推翻的一句话（噪声阈值 = √2·σ_rel，样本不足/边缘一律不下结论）。
- **图形页**：GPU 面板从「左边一个 7% + 右边三行小字」改成仪表面板（hero FsMega + 全宽占用率尺 + 三列事实 + 显存比例尺）；显存总量与用量口径不同，只在两者都拿到时才画尺。注册表复读 2s → 5s。
- **状态总线通电**：`AppState` 曾有 21 个 Raise、**0 个生产订阅者**。补 `OnToken/OffToken`、`Post()` 合并广播（守护 5 次/秒 → 1 次）、`Page.Watch` 生命周期（重建先退订、跨线程搬 UI、异常落 Diag）；Overview/Profiles 接通 6 条订阅；补 `Dirty.Ledger` 生产者（Engine 四个写账本出口）；守护侧 8 处 Raise 改 Post。
- **稳定性（六个真缺陷）**：托盘图标句柄泄漏（失败安装后消息窗与图标永不释放）、Explorer 重启后图标永久消失（无 `TaskbarCreated`，现 1.5s×8 重试）、**电源方案退出不还原**（现恢复点 + 退出恢复 + 崩溃后启动恢复，且只在「当前 == 我们设的」时才切回）、注册表类型不匹配却报已生效（现比 `RegistryValueKind` 并回滚）、写入失败不回滚（现判断「到底改了没有」再决定回滚/撤账）、账本损坏即历史全丢（现截断抢救 → 再隔离）。
- **顺手**：`ProcName` 静态 StringBuilder 跨线程竞争（改 ThreadStatic）、`BenchHistory` 损坏静默清空、`SplashNative` 关闭竞态（TOPMOST 卡片会一直盖屏）、`Profiles.Reload` 的 `if (!none) return;`（有档案时卡片墙从未被填过）。
- **启动**：新增**空闲预建**（ApplicationIdle + 4 秒无操作门，一次一页），首访任何页面不再有 50–110ms 空白；新增 `--diag <path>` 支持包（工作集/句柄/线程/总线计数/预建报告/被吞异常/账本）。实测真实进程 9 秒：预建 4 页、47.3 MB、**被吞异常 0**。
- **版本**：`Brand.Rev` 78 → **80**、`AssemblyVersion` 77 → **80**（上一轮漏改，导轨与资源管理器各说各话）。
- **测试**：50 + 43 + 27 + 11 + 6 = **137 项全绿**；网页首建最慢 97ms（上一轮 554ms）。详见 `REV80.md`。

---
## Rev 79 · 2026-11-11（INSTRUMENT GRADE · 把界面做成仪器）

- **材质**：`Ui.Panel` 升格为四层构件（受光面渐变 + 斜向光泽 + 14px 下缘内阴影 + 机加工棱）；整窗底改 `T.DeckBrush()` 渐变 + 3.1% 点阵 + 四角压暗。
- **仪表**：弧线重做为表盘 —— 分区带（红/琥珀/绿）、量纲数字 0–25–50–75–100、机加工外环；中心数字 40 → 60px（新增 `T.FsMega`）；修掉半径计算把整圈切掉的缺陷（-10 → -38）。
- **量条**：`SegBar` 从 28 段小方块改为「轨道 + 刻度 + 连续填充 + 游标」，并在低值下不再被误读为数据缺失；顺带变成真正零分配（20.5 → 0.0 B/帧）。
- **核心矩阵**：整格热力色改为「凹槽 + 自下而上生长的柱体 + 柱顶亮盖」，负载高度可直接读出。
- **签名元素**：新增 `Widgets/Sparkline.cs`，标题栏右侧 120 样本活体脉搏带（零分配）。
- **动效**：新增 `Motion.PageIn`（0.994→1 缩放 + ±16px 位移 + 淡入，260ms），与首建页的 35ms 阶梯入场叠加。
- **减法**：只读横幅从 46px 通栏琥珀警报压成 26px 低对比状态带（圆点 + 小字 + 提权重启 + 关闭），每页多出 20px 给内容。
- **修功能盲区**：离屏渲染路径从不启动 GPU 采样，导致图形页在**所有截图里**都显示"PDH 不可用"——该功能在评审口径下从未被演示过。已修复。
- **测试**：137 项全绿。详见 `REV79.md`。

---
## Rev 78 · 2026-11-11（APEX · 把仪表做成仪器）

- **结构**：新增 `Ui.PageRoot` + `ThinScroll.FitContent()`，让页面把剩余高度真正分给主体；新增 `Ui.Blueprint` 蓝图基底（16/80 双频网格，2.4%/5.9%），把「没内容」读成「待测区域」而不是渲染失败。
- **重做**：还原中心从 5 列稀疏表格改为**纵向账本时间轴**（节点/轨道/变化胶囊/×N 批量标记/渐隐续线/END OF LEDGER）；战术档案改为**垂直居中引导面板**；概览的核心拓扑进星号行、快速调优地脚压底；网络空图表与 TCP 表补真实语义（「未设置」而非「—」）。
- **修缺陷**：`Ui.Panel` 里 `content.SetValue(Border.PaddingProperty, pad)` 会在 content 本身是 Border 时把内边距静默变成 2×pad（已删除）；关闭态开关轨道对比度 1.15:1 → 2.6:1。
- **可信度**：`Perf.ReadKernelPerf` 与 `Topology.Walk` 补偏移断言（不满足就这一拍不给读数，而不是兜底成 4096）；新增 `Switch.Blocked`，只读点击不再「点了没反应」；审计日志改增量追加，去掉 190ms 整窗重建。
- **稳定性**：补上全库缺失的顶层兜底（`DispatcherUnhandledException` + `AppDomain.UnhandledException` + `UnobservedTaskException`），错误写 error.log 并带 tag 与 512KB 轮换；页面 Tick 就地拦截，单页异常不再带走整个进程。
- **构建**：`--render` 渲染完不退出会泄漏进程并锁住 exe，导致下一次编译 CS0016 假故障——已在 render 分支 `Environment.Exit(0)`；build.ps1 改为先编到 `out\_stage\` 再重试交换。
- **性能**：`T.Bevel` / `T.Grad` / `Ui.Icon` 三处缓存。核心矩阵 61.4→41.0 B/帧，仪表 245.8→225.3，负载条 41.0→20.5，波形 61.4→41.0。
- **热路径**：新增 `ProcName`，用 `QueryFullProcessImageNameW`（20.4 µs）取代 `Process.ProcessName`（全表查询 13,836 µs），替换调度页 2Hz 热路径等 3 处；调度页首建 64→54 ms、数据装配 20→8 ms。
- **冷启动**：图标几何改后台线程预热（字体预热实测无效，只有几何可以）——`shell_construct` 1602→1389 ms，`shown` 2831→**2497 ms**。
- **测试**：50 + 43 + 27 + 11 + 6 = **137 项全绿**。详见 `REV78.md`（含诚实留账与子代理分工）。

---
## Rev 76 · 2026-11-11（史诗升级 R71–76）

- **诚信地基**：清除假读数（ZEN4 徽章/仪表 68/缎带/CCD/量子初值）、CCD 均值按真实拓扑、全库 CCD0 掩码改 `Topo.Ccd0Mask`、Profiles GPU 写入改走账本、Ctrl+F NRE、Overview 890 行拆分。
- **性能债**：Benchmark 合并重复排序+零分配文本；LogPage 元数据指纹+尾读；Network/Memory 后台枚举；T.P 精确键；TrayMenu/WMI/Profiles 泄漏修复。
- **视觉系统**：运行时四主题强调色（冰蓝/琥珀/紫晶/薄荷）+ 持久化 + 即时换色 + 23 处字面量令牌化 + 辉光跟随；T.Mute 对比度 ≥4.5:1；新图标。
- **动效**：页面首建 35ms 阶梯入场；仪表刻度跟随 Tint；面板快捷键脚注；reduce-motion 补全。
- **epic 功能**：GPU 真实占用/显存（PDH，任务管理器口径，温度不伪造）；一键游戏模式+全屏检测提示（账本可逆）；Benchmark 历史持久化+CSV 导出；进程搜索。
- **无障碍**：Pressable 可聚焦 + Enter/Space + 强调色焦点环。
- **测试**：50 + 43 + 27 + 6 = **126 项全绿**；四主题离屏渲染确认。详见 `REV76.md`（含留账）。

---
## Rev 70 · 2026-11-11

- **命令面板**：预计算小写检索串 + 每查询单次评分（修掉排序比较器里 ~810 字符串/击键）；模糊子序列匹配加连续命中加分；输入框聚焦搜索图标变冰蓝。
- **焦点可见性**：审计日志过滤框聚焦边框变冰蓝（此前 TextBox 无焦点态）。
- **版本唯一真源**：`Brand.Rev/RevStamp` 统一导轨脚注/启动卡/程序集版本（修 63/66/63.0 三处漂移）→ Rev 70。
- **测试**：50 + 38 + 27 + 6 = **121 项全绿**；真实光标回归无崩溃。

---
## Rev 69 · 2026-11-11

- **Processes 页**：16 行骨架一次预建、原地更新（每 2 秒新建元素 399→0）；全量枚举只读 4 字段、线程/优先级/亲和性只给前 16 行补读（句柄 350→16，分配 ~178KB/s→<5KB/s）；排序移后台；`Btn.Compact` + Quiet 去阴影。
- **Memory**：长按修剪的 `Thread.Sleep(600)` UI 硬冻结 → 一次性定时器；缎带数组复用。
- **审计日志**：修"进页即空白"回归（Clear 移到指纹闸门后）+ `LineCount` 断言。
- **托盘**：`Suspend` 停呼吸灯、`Resume` 恢复 → 常驻真正静默；标题栏时钟分钟闸门。
- **Benchmark**：150ms 热路径 `Drain`→`DrainInto` 零分配。
- **测试**：50 + 37 + 27 + 6 = **120 项全绿**。详见 `REV69.md`（含留账）。

---
## Rev 68 · 2026-11-11

- **键盘滚动**：`ThinScroll` 支持 ↑↓/PgUp/PgDn/Home/End（焦点在容器内时），输入类控件让行；点击页面空白把焦点交给容器（不进 Tab 序）；到顶/到底复用撞墙反馈。滚动逻辑抽为 `ScrollByKey`，以 extent/viewport 自守卫，无头环境同样可用。
- **悬浮提示缓存**：`Tip` 的文案尺寸只量一次（`ConditionalWeakTable` 弱引用缓存），重复悬浮不再重新 Measure。
- **导航行微动效**：悬停底色独立一层做 120/140ms Opacity 补间（合成层）；选中指示条 140ms 淡入 + 160ms 滑入；补间委托缓存为字段，遵守 Motion「调用方不得每次分配闭包」约定。
- **新回归工具**：`tools/diag-hover.ps1` 真实光标 hover 扫掠 / 导航点击 / 键盘滚动 / 命令面板开合，只管理自己的实例、绝不强杀用户常驻进程；实测无崩溃。
- **测试**：50 + 36 + 27 + 6 = **119 项全绿**（+3 键盘滚动、+2 提示缓存断言）。

---
## Rev 67 · 2026-11-11

- **设计系统收敛**：全仓 23 档字号压到 9 档令牌（`FsNano9/FsMicro10/FsSmall11.5/FsBody12.5/FsTitle14/FsH119/FsPage23/FsNumL32/FsNumXL40`），约 360 处赋值归位；`Ui.Label` 的 `+0.5` 去除一次收敛 64 处。详见 `REV67.md`。
- **间距/尺度/圆角令牌**：新增 `PadPanel/PadInset/PadCard/BtnH/BtnHSm/GapIcon/GapTile/RadiusMd/RadiusLg/DisabledOpacity`；Panel/Inset 内边距、Reflow gap、Btn/HoldBtn/Switch 默认尺度归令牌。
- **颜色归族**：新增 `LineFaint/PanelTop/PanelBottom/AccentRing/CloseHot/CloseDown`；同值白色 alpha 梯归 Line 族；修 Tuning 风险 A 绿漂与面板类型标签灰漂；`Chip` 前景画刷补 Freeze。
- **死代码**：删 `Ui.SetTop/Label(string)/Rule()/Panel(UIElement)`、`Motion.Press/SlideDur` 与孤儿 summary。
- **验证**：119 全绿 + 11 页离屏渲染逐页目视对照（大数字改档无溢出）。
- 注：本轮与 Rev68 并行开发，Rev68 先合入、Rev67 后合入；版本号按主题而非合入顺序。

---
## Rev 66 · 2026-11-11

- **冷启动 3.64s → 2.37s**：删掉 WPF 启动页（它要吃 949ms 构造 + 1144ms 开首窗，等于把首窗开销挪到自己身上），换成 GDI+ 原生启动卡（约 40ms 出图，`Kernel/SplashNative.cs`）；命令面板与任务栏图标解码移出关键路径；改为「内容备好再 Show」。
- **切页停顿**：审计日志 220ms → 33ms（每 2 秒重读目录 + 重建 220 个 TextBlock 的老毛病，改为内容指纹闸门）；网络 190→111、调度 139→63、基准 100→63、图形 86→55。`--uitest` 新增逐页首建计时与 600ms 上限断言。
- **分配**：总览抖动泵 `Drain()` → `DrainInto()`（−14.2 KB/s）；核心矩阵 33.9 → 13.7 B/帧；仪表前景弧与波形几何改复用；44px 数字只在整数变化时重排。采样内核仍 0 B/拍。
- **死代码**：删除 14 个零引用成员，修 3 处与实现相反的过期注释。
- **诚实读数**：调度页 15.6ms、内存页 0.0 GB、总览四个 KPI 的假初值全部改为 `—`；内存页「系统文件缓存」因无法可靠测量而整行删除。
- **交互**：禁用按钮不再有悬停高亮；导航行按下与选中区分；`Motion.StopDriver` 的假退订与定时器 churn 修掉；无托盘时 `Park()` 现在会真的停表。
- **最小化语义修正**：点任务栏按钮不再把窗口 `Hide()` 到托盘（那会让任务栏图标一起消失，看起来像退出）。现在最小化只最小化并暂停采样，图标留在任务栏；只有关窗 × 与托盘菜单「隐藏到托盘」才真正收进托盘。实测最小化后 UIA 仍能读到任务栏按钮。
- **测试**：50 + 31 + 27 + 6 = **114 项全绿**。

---
## Rev 65 · 2026-11-08

- **总览页右上角的电源计划选择器从装饰改成真控件（高危）**：旧实现文案写死「平衡 · BALANCED」、带下拉箭头却没有任何点击处理，`Core.Profile` 全代码库无人赋值——不只是「只能选平衡」，显示的内容本身就是错的（实测机器在「高性能」，界面显示「平衡」）。新增 `Kernel/Power.cs` 直读 powrprof：读活动方案 / 枚举全部方案 / 读本地化名称 / 切换方案，不 spawn powercfg。下拉列出本机全部方案并打勾，选中即切，失败明确提示；标签每 2 秒与系统真实值核对一次。详见 `REV65.md`。
- **唤醒抖动分位数修正**：直方图上限 8191µs，机器繁忙时 P99 被钉死在 8160，导致页面出现「P99 8160 < AVG 13524」的自相矛盾读数。改为对最近 1200 个样本原地 quickselect 求精确 P99（预分配、零分配、O(n)）。
- **定时器租约偶发丢失**：`NtSetTimerResolution` 在繁忙时约 1/30 概率被拒一次，旧实现一次失败即清租约（表现为 0.5ms 开关自己弹回）。改为失败后立即重试一次。
- **测试**：50 + 30 + 27 + 6 = **113 项全绿**（连跑两轮）。

---
## Rev 64 · 2026-11-03

- **托盘（高危）**：图标被 Windows 11 默认收进溢出面板且面板内点击不转发给应用 → 安装后自动把 `IsPromoted` 提升为 1（仅缺失时写、不覆盖用户选择）。菜单开合改为真正的切换，450ms 内重复请求视为同一次点击。同一次点击产生的两条消息（v0 码 + v4 码）按逻辑事件去重。详见 `REV64.md`。
- **交互内核**：新增 `Kernel\Gesture.cs`——按钮一律"抬起且仍在控件内才触发"，拖出即取消，右键永不触发，丢捕获即收摊。导航行 / 历史按钮 / Ctrl+K / 三个窗口按钮 / 横幅关闭 / 侧栏提权 / Toast / 命令面板结果 / 托盘菜单 / 整行开关，全部统一到 `Pressable`。
- **标题栏**：放弃自绘 `DragMove`，改用 `WindowChrome.CaptionHeight`。拖动、双击最大化、边缘吸附、右键系统菜单、Alt+Space 回到 Windows 原厂语义；可点控件用 `IsHitTestVisibleInChrome` 单独挖出。命令面板打开时自动关掉拖动区。
- **双触发修复**：Switch / Btn 现在吞掉自己的左键事件，修掉"开关翻一次、整行再翻一次"。
- **托盘菜单重写**：键盘完整可用（↑↓/Home/End/PgUp/PgDn/Enter/Space/Esc）；弹出 130ms 内与"按下早于弹出"的点击一律丢弃；永不把光标放在菜单上；高度自适应（先收副标题再收页脚）；目标屏 DPI 二次校正。
- **托盘左键语义**：改为"把窗口带到眼前"，不做显隐切换；菜单内新增「隐藏到托盘」。
- **新自检**：`--tray-probe` 用 `SetCursorPos` + `mouse_event` 真实合成输入验证托盘全链路（4 项 ×3 轮全绿）。
- **测试**：44 + 25 + 27 + 6 = **102 项全绿**。

---
## Rev 63 · 2026-11-03

- **托盘交互修复（高危）**：启用 NOTIFYICON_VERSION_4 后，外壳发送的是 NIN_SELECT(0x0400) / NIN_KEYSELECT / WM_CONTEXTMENU(0x007B)，旧实现只认 0x0202/0x0205，导致点击全部落空。现已按 v4 语义解包并保留旧码兜底；锚点改从 wParam 取。
- **第二实例唤回**：已在托盘运行时再次双击图标，新进程不再静默退出，而是通过命名事件唤回并置前已有实例。
- **停靠态通知路由**：窗口在托盘里时 Toast 自动改走系统气泡，不再丢失。
- **强制置前**：Unpark 补 SetForegroundWindow，绕过前台锁。
- **触屏 / 掌机**：新增 Touch 探测，导航行高、热区外扩与托盘菜单行高按设备放大。
- **测试**：--daemon-test 增至 27 项（+5 托盘消息解包断言），四套件合计 82 项全绿。

---
## Rev 62 · 2026-11-03

- **反作弊边界**：每条策略独立归因（Applied / Denied / Skipped / Failed）；被内核保护时明确说「亲和度未生效」而不是谎报成功；拒绝后 1.4s 延迟重试一次。
- **崩溃自愈**：新增 daemon-session.json 落点，启动时若发现上次未正常收尾，按账本复原 daemon-gpu-* 条目，再由监视器重新挂载。
- **真实 CPU 拓扑**：新增 Topology（RelationProcessorCore + L3 实例 + EfficiencyClass），修正一处把 SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX 头部读成 WORD 的偏移错误——它导致探测静默回退到估算。总览/调度/侧栏/守护掩码全部改用真实值。
- **只读横幅**：未提权时内容区顶部显示一条带「以管理员身份重启」的横幅，可本会话忽略。
- **键盘流**：命令面板关闭后归还焦点；Tab / Shift+Tab 在面板内改为结果上下移动。
- **托盘菜单**：投影 + 主题感知边框；新增 --tray-shot 离屏渲染回归。
- **游戏嗅探**：Steam libraryfolders.vdf + appmanifest StateFlags 过滤 + Epic LauncherInstalled.dat，exe 打分与有界 BFS；档案页一键建档 / 全部建档。
- **战报**：守护期间每秒采集进程 CPU 差分与抖动统计，退出时给出「几次抖动 / 最坏多少 / 谁在抢 CPU」的 POST-MORTEM 卡片。
- **能效自适应**：拔电自动撤下 0.5ms 精度租约，插回自动恢复；状态幂等广播。
- **验证**：--selftest 34、--uitest 15、--daemon-test 22、--alloc-test 6，共 77 项全绿。

---
## Rev 61 · 2026-11-03

- **架构**：4 个千行单体（Ctl 1,440 / Shell 1,324 / Graphics 1,204 / Benchmark 1,131）拆为 24 个职责文件，最大单文件 756 行。
- **清理**：删除 410 行死代码（TreeDump / Stubs / Sparkline / Page.Hint ×11 / 7 个 Ui 原语 / 11 个主题令牌 / Perf 的 4 个只写字段 …），Gap ×11 与 FirstLine ×3 去重。
- **状态归一**：新增 `AppState` 状态总线与 `TimerService` 引用计数租约，修掉"三页共用 0.5ms 开关互相打翻"的泄漏；GPU 首选项空态与读失败分离；档案库统一到 `Core.Profiles`。
- **零分配**：采样内核改三缓冲快照 + 直方图 P99 + 复用非托管缓冲，实测 **0 B/拍、gen0 零回收**；渲染图元改冻结画笔缓存与热力 LUT，波形 14 B/帧、核心矩阵 35 B/帧。
- **新能力**：动态游戏守护（零分配进程巡检 ≤250ms、自动挂载 CCD0/独显/0.5ms、退出按账本复原）、系统托盘常驻（Shell_NotifyIcon + 自绘菜单、最小化到托盘、静默 10 MB / 0.0% CPU、开机自启）。
- **验证**：`--selftest` 28、`--uitest` 14、`--daemon-test` 16、`--alloc-test` 6，共 64 项全绿。

---

# 变更记录

## Rev 60.2（2026-11-02）— 交互与弹性打磨

### 判定
- 修复按钮"按下拖出再松开仍触发"的穿透问题（按下捕获 + 指针校验）。
- 修复自绘滚动条轨道拦截页面右缘 6px 点击的死区。
- 修复标题栏按钮被 DragMove 劫持（点最小化变成拖窗口）。
- 调优行 / 快速调优行 / 档案策略行改为整行可点；开关命中区 36×20 → 44×30。
- 补齐 Hover / Pressed / Focus / Disabled 四态与键盘 Tab/Enter/Space 支持。

### 自适应
- 新增 Rs.cs：断点 + Reflow 流式重排，按容器宽度判定。
- 8 处两/三栏结构改为 Reflow；侧栏 236↔68 折叠；标题栏遥测条按宽度隐藏。
- 修复"内容可宽于视口导致 Reflow 断点失效"的根因（横向滚动 Disabled）。
- 最小窗口 1120×700 → 960×600。

### 导航
- 左导轨分组（调优内核 / 保障与档案）。
- 新增命令面板 Ctrl+K（页面 / 动作 / 安全操作，危险项二次确认）。
- 历史前进后退（Alt+←/→ + 标题栏按钮）；起始页进入历史栈。
- 窗口状态与当前页持久化。

### 走查修复（59 条审计中的 P0/P1）
- 总览「应用推荐方案」死键接上真实批量应用。
- 「锁前台进程」锁到自身 → 新增 400ms 前台追踪器，只记录非本进程 PID。
- 网络节流数值校验（4294967295 与 -1 等价）修复。
- 「网络低延迟轮询」假开关改为绑定 nagle 动作真实读写。
- 命令面板危险项二次确认选错条目修复。
- 图形栈 WMI 同步卡顿 → 后台线程 + 占位。
- 调优矩阵 3 秒软同步；诊断条与账本读数实时化；调度页参数表跟刷。
- Ops.Run 双流死锁风险修复；注册表还原增加回读校验。
- 日志过滤 250ms 防抖；档案删除二次确认；档案写盘容错。
- 新增 --uitest 无头交互自检（14 项）。

## Rev 60（2026-11-02）— 全量重写

以「测量闭环 + 绝对可逆」为核心重构，替换 v50 的 PowerShell 单体实现。

### 架构

- **语言/运行时**：PowerShell 5.1 单体脚本（9781 行）→ C# 5 + WPF 代码构建，编译为约 250 KB 单文件 exe。
- **UI**：13 页代码拼接 → 11 页统一设计系统（令牌 / 原语 / 控件 / 自绘图元 四层）。
- **采样**：WMI 轮询 → NtQuerySystemInformation 直读，单后台线程，1Hz < 0.5ms。
- **状态目录**：修复旧版硬编码 <legacy-workspace>\state（13 处）导致「备份写不进去则全部动作静默中止」的问题，统一迁到 %LOCALAPPDATA%\GameOptimizer。
- **异常策略**：旧版 495 个 catch 中 394 个为空；新版只在「读取可选值」处吞异常，所有写入链路失败必须向上返回并在 UI 呈现。

### 功能

- **新增**：写前日志账本（原子写 + 变更校验 + 逐条/按动作/整体还原）。
- **新增**：第三方优化残留诊断（6 类负优化自动识别 + 一键修复）。
- **新增**：唤醒抖动探针与实时示波器（Thread.Sleep(1) 唤醒超调分布）。
- **新增**：硅片矩阵（16 核 × 2 线程逐核逐线程负载，CCD 分簇显示）。
- **新增**：CCD0 亲和性一键锁定（前台进程 / 任意进程），含恢复全核。
- **新增**：内存四级构成缎带（活动/已修改/待机/空闲）+ 内核池读数。
- **新增**：战术档案（按游戏的亲和性 / GPU 首选项 / 定时器策略，持久化）。
- **新增**：基准与体检（8s 采样、P50/P99/MAX、24 桶直方图、前后对比）。
- **新增**：无界面自检 --selftest 与离屏渲染 --render。
- **修正**：Win32PrioritySeparation=0x28 等负优化不再作为「优化」提供，而是作为「残留」供修复。
- **修正**：鼠标加速/键盘延迟等值按字符串类型写入（旧版曾按 DWord 写导致读回不一致）。

### 设计

- 全新视觉语言 PRECISION INSTRUMENT：石墨底 + 冰蓝信号色 + 1px 发丝几何 + Bahnschrift / Cascadia Mono。
- 无边框窗口、自绘标题栏与窗口按钮、自绘细滚动条、长按确认按钮、Toast 结果提示。
- 全部图标为 24 网格手绘线性几何；应用图标为 7 尺寸多分辨率 ICO。

### 验证

- --selftest 28 项全部通过（含真实注册表往返与还原）。
- 11 个页面离屏渲染 1.4× PNG 完成，逐页视觉评审。
- 真实窗口启动验证：DPI 144、2340×1436、工作集 119 MB。