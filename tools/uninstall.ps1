<#
  uninstall.ps1 — GameOptimizer Pro 干净卸载脚本

  默认保留 %LOCALAPPDATA%\GameOptimizer 下的写前账本（ledger）与档案配置，
  以便日后仍可找回写前原值；如确认所有改动已还原且需彻底清理，可加 -PurgeUserData。

  用法：
    .\uninstall.ps1
    .\uninstall.ps1 -PurgeUserData
#>
param(
    [switch]$PurgeUserData,
    [switch]$Silent
)
$ErrorActionPreference = 'Stop'

function Say($msg, $color = 'Gray') {
    if (-not $Silent) { Write-Host $msg -ForegroundColor $color }
}

Say ''
Say '=== GameOptimizer Pro 卸载向导 ===' 'Cyan'

# 1. 停止运行中的进程（先温和关闭，再兜底结束）
$procs = @(Get-Process GameOptimizer -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    Say '  [1/6] 正在结束运行中的 GameOptimizer 进程…' 'Yellow'
    foreach ($p in $procs) {
        try { $null = $p.CloseMainWindow() } catch { }
    }
    $deadline = (Get-Date).AddSeconds(4)
    while ((Get-Date) -lt $deadline -and @(Get-Process GameOptimizer -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 250
    }
    $rem = @(Get-Process GameOptimizer -ErrorAction SilentlyContinue)
    if ($rem.Count -gt 0) {
        $rem | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
} else {
    Say '  [1/6] 未检测到运行中的进程' 'DarkGray'
}

# 2. 清理开机自启项与历史遗留计划任务
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path $runKey) {
    $val = Get-ItemProperty -Path $runKey -Name 'GameOptimizer' -ErrorAction SilentlyContinue
    if ($null -ne $val) {
        Remove-ItemProperty -Path $runKey -Name 'GameOptimizer' -Force -ErrorAction SilentlyContinue
        Say '  [2/6] 已移除开机自启注册表项 (Run\GameOptimizer)' 'Green'
    } else {
        Say '  [2/6] 无开机自启注册表项' 'DarkGray'
    }
}
try {
    $null = & schtasks.exe /Query /TN 'GameOptimizerPro' 2>$null
    if ($LASTEXITCODE -eq 0) {
        $null = & schtasks.exe /Delete /TN 'GameOptimizerPro' /F 2>$null
        Say '  [2/6] 已清理遗留计划任务 (GameOptimizerPro)' 'Green'
    }
} catch { }

# 3. 清理桌面与开始菜单快捷方式
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'GameOptimizer Pro.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\GameOptimizer Pro.lnk')
)
foreach ($lnk in $shortcuts) {
    if (Test-Path $lnk) {
        Remove-Item $lnk -Force -ErrorAction SilentlyContinue
        Say ('  [3/6] 已移除快捷方式：' + $lnk) 'Green'
    }
}

# 4. 清理「安装的应用」卸载注册表项
$uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GameOptimizerPro'
if (Test-Path $uninstKey) {
    Remove-Item $uninstKey -Recurse -Force -ErrorAction SilentlyContinue
    Say '  [4/6] 已注销系统卸载注册表项' 'Green'
}

# 5. 移除程序安装目录
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\GameOptimizer'
if (Test-Path $installDir) {
    $removed = $false
    for ($i = 0; $i -lt 8; $i++) {
        try {
            Remove-Item $installDir -Recurse -Force -ErrorAction Stop
            $removed = $true
            break
        } catch {
            Start-Sleep -Milliseconds 300
        }
    }
    if ($removed) {
        Say ('  [5/6] 已删除程序目录：' + $installDir) 'Green'
    } else {
        # 当前若正是从 installDir 内部运行 uninstall.ps1，自身占用的脚本所在目录延后仅清 exe
        Get-ChildItem $installDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'uninstall.ps1' } | Remove-Item -Force -ErrorAction SilentlyContinue
        Say ('  [5/6] 程序文件已清理；目录句柄占用请退出终端后手动删除：' + $installDir) 'Yellow'
    }
} else {
    Say '  [5/6] 程序安装目录不存在' 'DarkGray'
}

# 6. 用户数据目录（ledger 写前账本 / profiles）
$dataDir = Join-Path $env:LOCALAPPDATA 'GameOptimizer'
if (Test-Path $dataDir) {
    if ($PurgeUserData) {
        Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
        Say ('  [6/6] 已彻底清除用户数据与账本目录：' + $dataDir) 'Yellow'
    } else {
        Say ('  [6/6] 已保留账本与档案配置（如需连同账本一起清除请加 -PurgeUserData）：' + $dataDir) 'Cyan'
    }
}

Say ''
Say '  [OK] 卸载完成。' 'Green'
