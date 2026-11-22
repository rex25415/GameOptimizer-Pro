<#
  GameOptimizer — 构建脚本
  用法：
    .\build.ps1                 编译 -> out\GameOptimizer.exe
    .\build.ps1 -Shot           编译并渲染全部页面截图到 shots\
    .\build.ps1 -Run            编译并启动

  Rev78 变更：编译先落到 out\_stage\，再用「重试 + 替换」交换到 out\。
  原因：csc 直接写 out\GameOptimizer.exe 时，只要上一轮进程还没退干净、
  或者杀软正在扫描刚生成的文件，就会拿到 CS0016「另一个程序正在使用此文件」。
  这个错误和源码对错无关，却会让人误以为改坏了——反复排查是最浪费时间的坑。
#>
param(
    [switch]$Shot,
    [switch]$Run,
    [string]$ShotDir = "",
    # 并行开发用：把产物编到别的目录，避免多个 agent 抢同一个 out\GameOptimizer.exe
    [string]$OutDir = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$out  = if ($OutDir) { $OutDir } else { Join-Path $root 'out' }
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf  = Join-Path $fw 'WPF'

if (-not (Test-Path $csc)) { throw 'csc.exe 不存在：' + $csc }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$stage = Join-Path $out '_stage'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$stageExe = Join-Path $stage 'GameOptimizer.exe'
Remove-Item $stageExe -Force -ErrorAction SilentlyContinue

$refPaths = @(
    (Join-Path $wpf 'PresentationFramework.dll'),
    (Join-Path $wpf 'PresentationCore.dll'),
    (Join-Path $wpf 'WindowsBase.dll'),
    (Join-Path $wpf 'System.Windows.Presentation.dll'),
    (Join-Path $fw  'System.Xaml.dll'),
    (Join-Path $fw  'System.Drawing.dll'),
    (Join-Path $fw  'System.Management.dll'),
    (Join-Path $fw  'System.Configuration.dll'),
    (Join-Path $fw  'System.Xml.dll'),
    (Join-Path $fw  'System.Xml.Linq.dll'),
    (Join-Path $fw  'System.Runtime.Serialization.dll'),
    (Join-Path $fw  'System.Web.Extensions.dll'),
    (Join-Path $fw  'System.Core.dll')
)

$sourceFiles = @(Get-ChildItem -Path $src -Recurse -Filter *.cs -File | Sort-Object FullName)
if ($sourceFiles.Count -eq 0) { throw '源代码为空' }

$manifest = Join-Path $src 'app.manifest'
$icon     = Join-Path $root 'assets\app.ico'

# 使用响应文件（UTF-8 BOM）+ ProcessStartInfo 调用 csc.exe：
# 彻底消除 PowerShell 5.1 与 PowerShell 7 (pwsh) 原生参数引号转义差异，
# 并同时支持含空格/中文路径与无上限源文件列表。
$rspLines = New-Object System.Collections.Generic.List[string]
$rspLines.Add('/nologo')
$rspLines.Add('/utf8output')
$rspLines.Add('/codepage:65001')
$rspLines.Add('/target:winexe')
$rspLines.Add('/platform:anycpu')
$rspLines.Add('/optimize+')
$rspLines.Add('/unsafe-')
$rspLines.Add('/out:"' + $stageExe + '"')
$rspLines.Add('/win32manifest:"' + $manifest + '"')
$rspLines.Add('/r:System.dll')
foreach ($r in $refPaths) {
    $rspLines.Add('/r:"' + $r + '"')
}
if (Test-Path $icon) {
    $rspLines.Add('/win32icon:"' + $icon + '"')
    $rspLines.Add('/resource:"' + $icon + '",GameOptimizer.ico')
}
foreach ($s in $sourceFiles) {
    $rspLines.Add('"' + $s.FullName + '"')
}

$rsp = Join-Path $stage 'build.rsp'
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllLines($rsp, $rspLines.ToArray(), $utf8Bom)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $csc
$psi.Arguments = '@"' + $rsp + '"'
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
$psi.CreateNoWindow = $true

$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
[void]$proc.Start()
$errTask = $proc.StandardError.ReadToEndAsync()
$stdOut  = $proc.StandardOutput.ReadToEnd()
$proc.WaitForExit()
$stdErr  = $errTask.GetAwaiter().GetResult()
$cscExit = $proc.ExitCode
$proc.Dispose()
$sw.Stop()
$log = $stdOut + $stdErr

if ($cscExit -ne 0) {
    Write-Host $log -ForegroundColor Red
    throw ('编译失败 exit=' + $cscExit)
}
if ($log.Trim()) { Write-Host $log -ForegroundColor Yellow }

# ---- 交换：带重试 --------------------------------------------------------
$exe = Join-Path $out 'GameOptimizer.exe'
$placed = $false
for ($i = 0; $i -lt 20; $i++) {
    try {
        Copy-Item $stageExe $exe -Force -ErrorAction Stop
        $placed = $true
        break
    } catch {
        Start-Sleep -Milliseconds 150
    }
}
if (-not $placed) {
    # 产物是好的，但 out\GameOptimizer.exe 还是旧的。以前这里 exit 0，
    # verify.ps1 随后拿**旧 exe** 跑完整套自检并顺利全绿 —— 一次"验收通过"验的是上一版。
    # 编译成功但没能替换，必须让调用方看到失败（exit 2：产物可用，部署未完成）。
    Write-Host ('[BUILD STAGED] 目标被占用，编译产物在 ' + $stageExe) -ForegroundColor Yellow
    Write-Host ('  当前 out\GameOptimizer.exe 仍是旧版本，锁释放后重跑即可（退出码 2）。') -ForegroundColor Yellow
    exit 2
}

$len = (Get-Item $exe).Length
Write-Host ('[BUILD OK] {0:N0} bytes  {1} ms' -f $len, $sw.ElapsedMilliseconds) -ForegroundColor Green

if ($Shot) {
    if (-not $ShotDir) { $ShotDir = Join-Path $root 'shots' }
    New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
    Get-ChildItem $ShotDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
    $p = Start-Process -FilePath $exe -ArgumentList @('--render', ('"' + $ShotDir + '"')) -PassThru -Wait
    if ($p.ExitCode -ne 0) {
        Write-Host ('[SHOT FAILED] exit=' + $p.ExitCode + ' -> ' + $ShotDir) -ForegroundColor Red
        exit $p.ExitCode
    }
    Write-Host ('[SHOT] exit=0 -> ' + $ShotDir) -ForegroundColor Cyan
}
if ($Run) {
    Start-Process -FilePath $exe | Out-Null
}
