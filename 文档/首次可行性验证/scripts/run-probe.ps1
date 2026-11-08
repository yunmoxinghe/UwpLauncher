<#
.SYNOPSIS
  运行 IslandProbe 并抓取屏幕截图，用于验证 UWP XAML Island 透明背景。

.PARAMETER Probe
  探针工程：IslandProbe（UWP XAML 内容）/ WinUI2Probe（WinUI 2 控件作为岛内容对照）

.PARAMETER Mode
  宿主模式：topmost（DesktopFlyouts 原方案）/ plain / workerw（SetParent 到桌面 WorkerW）

.PARAMETER Transparent
  是否开启 IsBackgroundTransparent：on / off

.PARAMETER HoldMs
  探针窗口总存活时间（毫秒）

.PARAMETER CaptureAtMs
  探针内部像素采样时间点（毫秒）
#>
param(
    [ValidateSet("IslandProbe", "WinUI2Probe")]
    [string]$Probe = "IslandProbe",

    [ValidateSet("topmost", "plain", "workerw")]
    [string]$Mode = "topmost",

    [ValidateSet("on", "off")]
    [string]$Transparent = "on",

    [int]$HoldMs = 5000,
    [int]$CaptureAtMs = 2500
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\$Probe\bin\x64\Debug\net10.0-windows10.0.26100.0\$Probe.exe"
$artifacts = Join-Path $root "artifacts"

if (-not (Test-Path $exe)) {
    throw "未找到 $exe。请先构建 $Probe（见 VERIFICATION.md 第 5 节）。"
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$flag = if ($Transparent -eq "on") { "1" } else { "0" }
$name = "$Probe-$Mode-$Transparent-$stamp"
$log = Join-Path $artifacts "$name.log"
$png = Join-Path $artifacts "$name.png"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$shell = $null
$minimized = $false
if ($Mode -eq "workerw") {
    # 桌面层被普通窗口遮挡时无法观察，先最小化所有窗口
    $shell = New-Object -ComObject Shell.Application
    $shell.MinimizeAll()
    $minimized = $true
    Start-Sleep -Milliseconds 700
}

try {
    $p = Start-Process -FilePath $exe -ArgumentList @(
        "--mode", $Mode,
        "--transparent", $flag,
        "--log", $log,
        "--hold", "$HoldMs",
        "--capture", "$CaptureAtMs"
    ) -PassThru

    Start-Sleep -Milliseconds ($CaptureAtMs + 900)

    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
    $bmp.Save($png)
    $g.Dispose()
    $bmp.Dispose()

    $p.WaitForExit($HoldMs + 10000) | Out-Null
    "done: $name (exited=$($p.HasExited))"
    "log: $log"
    "png: $png"
}
finally {
    if ($minimized -and $shell -ne $null) {
        $shell.UndoMinimizeAll()
    }
}
