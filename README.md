# GameOptimizer Pro

<p align="center">
  <strong>简体中文</strong> · <a href="README.en.md">English</a>
</p>

<p align="center">
  <strong>Windows 10 / 11 零依赖单文件游戏性能控制台 · 写前原子账本 · 逐项可还原 · 原生 Win32 实时遥测</strong><br/>
  <em>VANGUARD CONSOLE · IRONCLAD KERNEL (Rev 140)</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0ea5e9?style=flat-square" alt="Platform"/>
  <img src="https://img.shields.io/badge/Runtime-.NET%20Framework%204.8%20(Built--in)-3b82f6?style=flat-square" alt="Runtime"/>
  <img src="https://img.shields.io/badge/Dependencies-Zero%20External%20DLLs-10b981?style=flat-square" alt="Zero Dependencies"/>
  <img src="https://img.shields.io/badge/Binary-~1.24%20MB%20Single%20EXE-6366f1?style=flat-square" alt="Single EXE"/>
  <img src="https://img.shields.io/badge/License-MIT-f59e0b?style=flat-square" alt="MIT License"/>
</p>

<p align="center">
  <img src="docs/screenshots/01-overview.png" alt="GameOptimizer Pro - Situation Overview" width="100%"/>
</p>

---

## 为什么做它

市面上的很多「一键游戏优化脚本」往往把系统改成不可逆的非默认值（关闭 MPO、强制固定短时间片、禁用 SysMain、开启全局 0.5 ms 定时器……），实测多为负优化，且改完根本还原不回来。

**GameOptimizer Pro** 是一台把 **「测量 → 判断 → 施加 → 校验 → 随时还原」** 完整闭合的性能控制台：

- **写前原子账本（Write-Ahead Ledger）**：任何系统写入前，先读取并原子落盘原值（含「原本不存在」状态）至 `%LOCALAPPDATA%\GameOptimizer\ledger\ledger.json`；写盘失败则中止改动，写入后立刻回读校验，支持逐条还原、长按全部还原或归档确认状态。
- **自动识破第三方负优化残留**：启动主动扫描 6 类常见脚本负优化配置（错误量子加权、篡改 MMCSS、禁 MPO、全局定时器常开、禁 SysMain、开启 LargeSystemCache），支持总览页一键修复。
- **零外部依赖 · 纯代码 C# 5 / WPF**：无需安装 .NET SDK 或任何第三方运行库，Windows 10/11 原生 `csc.exe` 即可 1 秒内编出 `~1.24 MB` 单文件可执行程序。
- **零注入 · 零驱动 · 不碰反作弊**：纯用户态标准 Win32 API，不注入游戏进程、不修改游戏文件、不加载内核驱动。

---

## 界面预览

<table>
  <tr>
    <td width="50%">
      <strong>02 调优矩阵 · 随滚吸顶战术检视台</strong><br/>
      <img src="docs/screenshots/02-tuning.png" alt="Tuning Matrix & Sticky Tactical Inspector"/>
    </td>
    <td width="50%">
      <strong>03 调度与定时器 · 交互式硅晶核心拓扑</strong><br/>
      <img src="docs/screenshots/03-scheduler.png" alt="Scheduler & Silicon Topology"/>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <strong>05 图形栈 · 虚拟多屏蓝图画布与 PDH GPU 遥测</strong><br/>
      <img src="docs/screenshots/05-graphics.png" alt="Graphics Multi-Monitor Canvas & GPU Telemetry"/>
    </td>
    <td width="50%">
      <strong>01 态势总览 · 四维健康驾驶舱与 4 套光学主题</strong><br/>
      <img src="docs/screenshots/01-overview.png" alt="Situation Overview"/>
    </td>
  </tr>
</table>

---

## 核心功能模块（11 页全覆盖）

| # | 模块 | 核心能力 |
|---|---|---|
| `01` | **态势总览** | 四维系统健康评估（调度唤醒 / 算力余裕 / 内存余裕 / 调优覆盖）、`ArcGauge 3.0` 就绪表盘、5Hz 单调三次平滑抖动波形、硅晶核心热力与一键快控 |
| `02` | **调优矩阵** | 30 项系统调优（涵盖注册表、字段级 `RegKv` DirectX 全局开关、原生 `powrprof.dll` 电源子项与服务）、3 套战术预设、实时搜索与右侧随滚吸顶 **Tactical Inspector**（实测→目标 Diff / 原子路径 / 工程机理） |
| `03` | **调度与定时器** | 自动探测本机物理核/线程/L3 簇（CCD）/异构大小核，支持交互点选亲和矩阵（`CCD0` / `CCD1` / `仅物理核` / `全核`）、本进程 0.5 ms 定时租约与内核参数审计 |
| `04` | **内存与提交** | `MemRibbon 3.0` 物理页四级构成、提交与页面文件监控、内核分页/非分页池、Top 进程工作集与原生私有提交读数、Win11 EcoQoS 压制与待机列表清理 |
| `05` | **图形栈** | 原生 GDI 虚拟多屏蓝图画布（按真实坐标/分辨率/刷新率/色深等比绘制）、DXGI 多卡 LUID 识别（自动去重 MUX/Optimus 双 LUID 与虚拟卡）、1Hz PDH 逐引擎与显存占用 |
| `06` | **网络与吞吐** | 四节点实时路由拓扑（`PC → NIC/TUN → LAN → WAN`）、自动识别 Clash/Mihomo/WireGuard/Wintun 隧道、并行双路径延迟探测、60s 双通道吞吐示波与活跃 TCP 进程聚合 |
| `07` | **进程与优先级** | `NtQuerySystemInformation` 零句柄双缓冲进程表、前台目标自动锁定、单进程优先级 / 亲和核 / Win11 EcoQoS 精确控制与一键建档 |
| `08` | **战术档案** | 按游戏可执行文件固化 `CCD / 优先级 / 独显优先 / 0.5ms` 策略，动态守护在游戏启动瞬间自动挂载、退出后按账本复原；支持 Steam / Epic / WeGame / Xbox / 桌面多源扫描 |
| `09` | **基准测试** | 8.0s 唤醒超调 48 箱自适应直方图 + 内存带宽/指针追逐延迟 + 跨线程事件往返乒乓 + 同口径历史 A/B 对照与 `S/A/B/C` 评级 |
| `10` | **还原中心** | 写前安全金库统计、原值→新值法医时间线、逐条还原 / 长按全部还原 / 保留归档 / 快照导出 |
| `11` | **审计日志** | 只追加记录每次应用、还原、守护挂载与错误归因，支持关键字过滤与诊断报告导出 |

---

## 快捷下载与使用

### 方式一：直接下载成品（推荐普通玩家）

前往右侧 **[Releases](https://github.com/rex25415/GameOptimizer-Pro/releases)** 下载最新版：
- `GameOptimizer-Pro-v1.40.0-win-x64.exe`：单文件绿色版，下载后直接双击即可运行。
- `GameOptimizer-Pro-v1.40.0-portable.zip`：包含主程序与 `deploy.ps1` / `uninstall.ps1` 安装卸载脚本。
- `SHA256SUMS.txt`：可核对可执行文件哈希。

> **关于 Windows SmartScreen 提示**：本软件为个人开源项目，未购买商业代码签名证书。首次运行系统可能弹出 SmartScreen 提醒，你可以点击「更多信息 → 仍要运行」，或者采用下方方式二直接从源码 1 秒本地自编译。

### 方式二：零依赖从源码编译（Windows 10 / 11 自带环境即可）

无需安装 Visual Studio 或 .NET SDK，仅用 Windows 内置的 `.NET Framework 4.8 csc.exe`：

```powershell
git clone https://github.com/rex25415/GameOptimizer-Pro.git
cd GameOptimizer-Pro
.\build.ps1                 # 编译 -> out\GameOptimizer.exe（约 0.7 秒）
.\build.ps1 -Run            # 编译并直接启动
.\tools\deploy.ps1          # 安装至 %LOCALAPPDATA%\Programs\GameOptimizer 并创建桌面/开始菜单快捷方式与卸载入口
.\tools\uninstall.ps1       # 干净卸载（默认保留 ledger 账本，可加 -PurgeUserData 一并清除）
```

### 权限与硬件自适应说明

- 程序默认以**普通权限**启动，全部硬件探测与实时诊断立即可用；需要写入 `HKLM` / 电源方案 / 服务时，点击左下角状态行或顶栏「提权重启」按需提权即可。
- 所有硬件信息（CPU 型号与核/簇/大小核拓扑、NVIDIA/AMD/Intel 显卡与多显示器、台式机/笔记本供电、电源计划、网卡与游戏库）全部于运行时通过原生 Windows API 自动探测。

---

## 后端实测性能（Rev 140 · IRONCLAD KERNEL）

Rev 140 将关键后端热路径全面收敛至零异常原生 Win32 API（`advapi32.dll RegGetValueW`、`OpenProcess` 单句柄亲和/判活/私有内存、持久化高水位非托管缓冲与共享进程表）：

| 基准项 | 实测结果（`--backend-bench` / `--alloc-test`） | 说明 |
|---|---|---|
| `reg.read` / `reg.read.miss` | `P50 17.0 µs` / `15.5 µs` | 原生 `RegGetValueW`，缺失键零 CLR `IOException` |
| `affinity.read.native` / `phys.cached` | `P50 2.7 µs` / `0.0 µs` | 彻底替代 `Process.GetProcessById`（`~13.8 ms` 全表扫描） |
| `proc.alive.native` / `proc.private.native` | `P50 2.3 µs` / `2.4 µs` | 原生单句柄判活防 PID 复用 + `K32GetProcessMemoryInfo` |
| `net.tcp.pid` | `P50 1.82 ms` | 持久化高水位 TCP 缓冲 + TTL PID 缓存 + 并行双路径探测 |
| `proc.table.refresh` / `cached` | `10.1 ms (76 B)` / `0.1 µs` | 单次 `NtQuerySystemInformation` 双缓冲快照 |
| `bus.raise` | `4.9 ns/op` | 内部脏标记事件总线 |
| 自绘光学图元稳态分配 | `Waveform 41 B` / `MemRibbon 0 B` / `PerfSampler 0 B/tick` | 全仓零 `DropShadowEffect` 离屏阴影合成负担 |

### 内置 6 套自动化自检

```powershell
.\out\GameOptimizer.exe --selftest selftest.txt          # 113 项后端原子事务、RegKv、并发与 Win32 校验
.\out\GameOptimizer.exe --uitest uitest.txt              # 84 项前端布局、交互、换肤、吸顶与 11 页回归
.\out\GameOptimizer.exe --alloc-test                     # 11 项图元与采样器零分配预算探针
.\out\GameOptimizer.exe --stress-test                    # 8 项切页/换肤/面板泄漏与线程退出压测
.\out\GameOptimizer.exe --daemon-test                    # 守护引擎、会话落点与战报边界回归
.\out\GameOptimizer.exe --backend-bench bench.txt        # 19 项后端微基准对表
```

---

## 快捷键与数据位置

- **全局快捷键**：`Ctrl+K` 命令面板 · `Alt+← / Alt+→` 历史前进后退 · `Ctrl+1 ~ Ctrl+0` 直达前 10 页 · `Esc` 关闭浮层
- **本地数据目录**（所有数据仅存本机，无任何联网遥测上报）：
  - `%LOCALAPPDATA%\GameOptimizer\ledger\ledger.json`：写前原子账本（原值记录）
  - `%LOCALAPPDATA%\GameOptimizer\ledger\audit-YYYYMM.log`：追加式审计日志
  - `%LOCALAPPDATA%\GameOptimizer\profiles.json`：游戏战术档案

---

## 诚实边界声明

- 本工具不测量游戏内 FPS（真实帧时间需要 PresentMon / ETW 级采集）；所有读数均为真实系统侧指标（唤醒抖动、DPC/ISR、定时器精度、PDH GPU 引擎、显存与网络吞吐），读不到一律显示 `—`，绝不伪造数值。
- 风险 C 项需长按确认；需重启生效项明确标注；还原仅回放账本记录过的原值，不会误删用户后续自行调整的配置。
- 详细演进记录与技术报告见 [CHANGELOG.md](CHANGELOG.md) 与 [REV140.md](REV140.md)。

---

## License

本项目采用 [MIT License](LICENSE) 开源。
