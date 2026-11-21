<#
  deploy.ps1 — GameOptimizer 部署器
  原则：快捷方式按"目标 exe"识别，不按文件名。
        CreateShortcut 对不存在的路径会新建，曾经因此凭空多出一个桌面图标。
  用法：
    .\tools\deploy.ps1              # 同步 out\GameOptimizer.exe + 文档，并收敛快捷方式
    .\tools\deploy.ps1 -SkipDocs
#>
param([switch]$SkipDocs, [int]$WaitForExitSec = 0)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dst  = Join-Path $env:LOCALAPPDATA 'Programs\GameOptimizer'
$exe  = Join-Path $dst 'GameOptimizer.exe'
New-Item -ItemType Directory -Force -Path $dst | Out-Null

# ---- 1. 二进制与资产 ----
# 正在运行的 exe 是被锁住的：若无法替换主程序，立即中止而不覆盖 .ico/.md，避免图文版本不一致。
# 兼容源码仓库（out\GameOptimizer.exe）与绿色发布包（同级或上级 GameOptimizer*.exe）
$srcExe = Join-Path $root 'out\GameOptimizer.exe'
if (-not (Test-Path $srcExe)) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'GameOptimizer.exe'),
        (Join-Path $root 'GameOptimizer.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $srcExe = $c; break }
    }
    if (-not (Test-Path $srcExe)) {
        $glob = Get-ChildItem $root -Filter 'GameOptimizer*.exe' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($glob) { $srcExe = $glob.FullName }
    }
}
if (-not (Test-Path $srcExe)) {
    Write-Host ('[DEPLOY FAILED] 找不到构建产物：' + $srcExe + '（请先运行 .\build.ps1）') -ForegroundColor Red
    exit 1
}
$running = @(Get-Process GameOptimizer -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and $WaitForExitSec -gt 0) {
    $pids = ($running | ForEach-Object { $_.Id }) -join ','
    Write-Host ('  等待运行中的 GameOptimizer 实例退出（最多 ' + $WaitForExitSec + 's，PID=' + $pids + '）…')
    $running | Wait-Process -Timeout $WaitForExitSec -ErrorAction SilentlyContinue
    $running = @(Get-Process GameOptimizer -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $left = ($running | ForEach-Object { $_.Id }) -join ','
        Write-Host ('  !! 等待超时（' + $WaitForExitSec + 's），进程仍在运行 (PID=' + $left + ')') -ForegroundColor Yellow
    }
} elseif ($running.Count -gt 0) {
    $pids = ($running | ForEach-Object { $_.Id }) -join ','
    Write-Host ('  检测到运行中的 GameOptimizer (PID=' + $pids + ')，请先退出或加 -WaitForExitSec 30') -ForegroundColor Yellow
}
# 清理前一次热替换遗留的已解锁旧映像
Get-ChildItem $dst -Filter 'GameOptimizer.exe.prev*' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

$hotSwapped = $false
try {
    Copy-Item $srcExe $exe -Force -ErrorAction Stop
} catch {
    # Windows 可执行文件运行时带 FILE_SHARE_DELETE：无法原地覆写，但可走标准原子重命名热替换
    $prevExe = Join-Path $dst ('GameOptimizer.exe.prev.' + [DateTime]::Now.ToString('yyyyMMddHHmmss'))
    try {
        Move-Item -Path $exe -Destination $prevExe -Force -ErrorAction Stop
        Copy-Item $srcExe $exe -Force -ErrorAction Stop
        $hotSwapped = $true
        Write-Host ('  热替换成功：旧映像已暂移至 ' + (Split-Path $prevExe -Leaf) + '，新版二进制已就位。') -ForegroundColor Cyan
        Write-Host '  提示：托盘里仍在运行的旧实例退出后重启即生效（下次部署会自动清理 .prev 文件）。' -ForegroundColor Yellow
    } catch {
        Write-Host ('[DEPLOY FAILED] 二进制未替换（进程占用且无法热替换）：' + $exe) -ForegroundColor Red
        Write-Host '  已立即中止部署：未覆盖 .ico / .md 与快捷方式，防止安装目录图文版本不一致。' -ForegroundColor Yellow
        Write-Host '  请关闭运行中的 GameOptimizer 后重跑，或附加 -WaitForExitSec 30。' -ForegroundColor Yellow
        Write-Host ('  详情：' + $_.Exception.Message) -ForegroundColor DarkGray
        exit 1
    }
}
$srcIco = Join-Path $root 'assets\app.ico'
if (-not (Test-Path $srcIco)) { $srcIco = Join-Path $PSScriptRoot 'GameOptimizer.ico' }
if (Test-Path $srcIco) { Copy-Item $srcIco (Join-Path $dst 'GameOptimizer.ico') -Force }
if (-not $SkipDocs) {
    # 文档按通配复制：旧实现是一张写死的清单，每加一个 REVxx.md 都会忘，
    # 于是安装目录里的说明永远停在上一代（用户看到的版本说明和程序对不上）。
    Get-ChildItem $root -Filter *.md -File -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName $dst -Force }
}

$sh = New-Object -ComObject WScript.Shell
$iconLoc = $exe + ',0'

function Set-Link([string]$path) {
    $s = $sh.CreateShortcut($path)
    $s.TargetPath = $exe
    $s.IconLocation = $iconLoc
    $s.WorkingDirectory = $dst
    $s.Save()
}

function Get-Target([string]$path) {
    try { return ($sh.CreateShortcut($path)).TargetPath } catch { return '' }
}

# ---- 2. 桌面：先找现有的，再决定是否新建 ----
$desk = [Environment]::GetFolderPath('Desktop')
$mine = @(Get-ChildItem $desk -Filter *.lnk -ErrorAction SilentlyContinue |
          Where-Object { (Get-Target $_.FullName) -eq $exe })

if ($mine.Count -gt 1) {
    # 多于一个：保留名字最贴合产品的那份，其余删除
    $keep = $mine | Where-Object { $_.Name -like '*Pro*' } | Select-Object -First 1
    if (-not $keep) { $keep = $mine | Sort-Object LastWriteTime | Select-Object -First 1 }
    foreach ($l in $mine) { if ($l.FullName -ne $keep.FullName) { Remove-Item $l.FullName -Force; Write-Host ('  dedupe  -> removed ' + $l.Name) } }
    $mine = @($keep)
}

if ($mine.Count -eq 1) {
    Set-Link $mine[0].FullName
    Write-Host ('  桌面快捷方式：复用 ' + $mine[0].Name)
} else {
    $new = Join-Path $desk 'GameOptimizer Pro.lnk'
    Set-Link $new
    Write-Host ('  桌面快捷方式：新建 ' + (Split-Path $new -Leaf))
}

# ---- 3. 开始菜单：复用/去重，若尚不存在则新建（让 Win 搜索立即可见）----
$smRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
if (Test-Path $smRoot) {
    $smMine = @(Get-ChildItem $smRoot -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue |
                Where-Object { (Get-Target $_.FullName) -eq $exe })
    if ($smMine.Count -ge 1) {
        foreach ($item in $smMine) {
            Set-Link $item.FullName
            Write-Host ('  开始菜单：复用并对齐 ' + $item.Name)
        }
    } else {
        $smNew = Join-Path $smRoot 'GameOptimizer Pro.lnk'
        Set-Link $smNew
        Write-Host ('  开始菜单：新建 ' + (Split-Path $smNew -Leaf))
    }
}

# ---- 3.5 清理旧外部卸载脚本、刷新注册表元数据（直接指向 exe --uninstall）----
$dstDir = Split-Path -Parent $exe
$oldUninst = Join-Path $dstDir 'Uninstall.ps1'
if (Test-Path $oldUninst) { Remove-Item $oldUninst -Force -ErrorAction SilentlyContinue }

try {
    $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $dispVer = if ([string]::IsNullOrWhiteSpace($fvi.FileVersion)) { '142.0.2026.1121' } else { $fvi.FileVersion }
    $kb = [int][Math]::Ceiling(((Get-ChildItem $dstDir -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum) / 1KB)
    $uKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GameOptimizerPro'
    New-Item -Path $uKey -Force | Out-Null
    Set-ItemProperty -Path $uKey -Name 'DisplayName' -Value 'GameOptimizer Pro'
    Set-ItemProperty -Path $uKey -Name 'DisplayVersion' -Value $dispVer
    Set-ItemProperty -Path $uKey -Name 'Publisher' -Value 'GameOptimizer Studio'
    Set-ItemProperty -Path $uKey -Name 'InstallLocation' -Value $dstDir
    Set-ItemProperty -Path $uKey -Name 'DisplayIcon' -Value $exe
    Set-ItemProperty -Path $uKey -Name 'UninstallString' -Value ('"' + $exe + '" --uninstall')
    Set-ItemProperty -Path $uKey -Name 'QuietUninstallString' -Value ('"' + $exe + '" --uninstall --silent')
    Set-ItemProperty -Path $uKey -Name 'EstimatedSize' -Value $kb -Type DWord
    Set-ItemProperty -Path $uKey -Name 'NoModify' -Value 1 -Type DWord
    Set-ItemProperty -Path $uKey -Name 'NoRepair' -Value 1 -Type DWord
    Write-Host ('  卸载入口：已注册内置 --uninstall (' + $dispVer + ')') -ForegroundColor DarkGray
} catch {
    Write-Host ('  warn    -> uninstall reg: ' + $_.Exception.Message) -ForegroundColor Yellow
}

try {
    $runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runItem = Get-ItemProperty -Path $runPath -Name 'GameOptimizer' -ErrorAction SilentlyContinue
    if ($null -ne $runItem -and $runItem.GameOptimizer -notmatch '--tray') {
        Set-ItemProperty -Path $runPath -Name 'GameOptimizer' -Value ('"' + $exe + '" --tray')
        Write-Host '  自启参数：已补齐 --tray 托盘静默启动' -ForegroundColor DarkGray
    }
} catch { }

try {
    $tx = (& schtasks.exe /Query /TN 'GameOptimizerPro' /XML 2>$null) | Out-String
    if ($LASTEXITCODE -eq 0 -and $tx -match 'GameOptimizer\.ps1') {
        $null = & schtasks.exe /Delete /TN 'GameOptimizerPro' /F 2>$null
        Write-Host '  计划任务：已清理指向失效 .ps1 的遗留任务 GameOptimizerPro' -ForegroundColor DarkGray
    }
} catch { }

Get-ChildItem $dstDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'GameOptimizer.exe.bak-rev*' -or $_.Name -like 'GameOptimizer.exe.old-*' } |
    ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

# ---- 4. 外壳图标缓存 ----
try { ie4uinit.exe -show 2>$null } catch { }

$ver = (Get-Item $exe).Length
Write-Host ('[DEPLOY OK] ' + $exe + '  (' + $ver + ' bytes)') -ForegroundColor Green