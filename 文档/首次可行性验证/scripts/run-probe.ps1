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
    [int]$CaptureAtMs = 2500,

    # 岛内容：winui2（NavigationView + InfoBar）/ grid（对照诊断，仅原判据布局）/ nav（Frame + Page 导航）
    [ValidateSet("winui2", "grid", "nav")]
    [string]$Content = "winui2",

    # 以已注册的 MSIX 包身份启动（需要先运行 package-msix.ps1 -Install）
    [switch]$Packaged,

    # 仅 nav 内容：把 PageA 的 NavigationCacheMode 设为 Required（对照默认 Disabled）
    [switch]$NavCache
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\$Probe\bin\x64\Debug\net10.0-windows10.0.26100.0\$Probe.exe"
$artifacts = Join-Path $root "artifacts"

if (-not $Packaged -and -not (Test-Path $exe)) {
    throw "未找到 $exe。请先构建 $Probe（见 VERIFICATION.md 第 6 节）。"
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$flag = if ($Transparent -eq "on") { "1" } else { "0" }
$name = "$Probe-$Mode-$Transparent-$stamp"
$log = Join-Path $artifacts "$name.log"
$png = Join-Path $artifacts "$name.png"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Capture-Screen([string]$path) {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
    $bmp.Save($path)
    $g.Dispose()
    $bmp.Dispose()
}

$shell = $null
$minimized = $false
if ($Mode -eq "workerw") {
    # 桌面层被普通窗口遮挡时无法观察，先最小化所有窗口
    $shell = New-Object -ComObject Shell.Application
    $shell.MinimizeAll()
    $minimized = $true
    Start-Sleep -Milliseconds 700
    # 无窗口遮挡的桌面基准图：用于校验“透出的壁纸色”确实来自壁纸
    Capture-Screen (Join-Path $artifacts "$name-desktop.png")
}

function Start-PackagedProbe {
    $pkg = Get-AppxPackage -Name "WinUI2Probe" | Select-Object -First 1
    if (-not $pkg) {
        throw "WinUI2Probe 未注册。请先运行 scripts/package-msix.ps1"
    }

    $activationSource = @"
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationActivationManager
{
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    int ActivateShellApp([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, out uint processId);
    int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
    int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
public class ApplicationActivationManager { }

public static class PackagedProbeLauncher
{
    public static uint Activate(string aumid, string arguments)
    {
        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        // AO_NONE = 0
        var hr = mgr.ActivateApplication(aumid, arguments, 0, out pid);
        if (hr < 0) throw new COMException("ActivateApplication failed", hr);
        return pid;
    }
}
"@

    if (-not ([System.Management.Automation.PSTypeName]"PackagedProbeLauncher").Type) {
        Add-Type -TypeDefinition $activationSource -Language CSharp
    }

    $aumid = "$($pkg.PackageFamilyName)!App"
    $arguments = "--mode $Mode --content $Content --transparent $flag --log $log --hold $HoldMs --capture $CaptureAtMs$(if ($NavCache) { ' --navcache 1' })"
    $processId = [PackagedProbeLauncher]::Activate($aumid, $arguments)
    return Get-Process -Id ([int]$processId)
}

try {
    if ($Packaged) {
        $p = Start-PackagedProbe
    }
    else {
        $argList = @(
            "--mode", $Mode,
            "--content", $Content,
            "--transparent", $flag,
            "--log", $log,
            "--hold", "$HoldMs",
            "--capture", "$CaptureAtMs"
        )
        if ($NavCache) { $argList += @("--navcache", "1") }
        $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
    }

    Start-Sleep -Milliseconds ($CaptureAtMs + 900)

    Capture-Screen $png

    $p.WaitForExit($HoldMs + 10000) | Out-Null
    "done: $name (exited=$($p.HasExited))"
    "log: $log"
    "png: $png"
    if ($Mode -eq "workerw") { "desktop: $(Join-Path $artifacts "$name-desktop.png")" }
}
finally {
    if ($minimized -and $shell -ne $null) {
        $shell.UndoMinimizeAll()
    }
}
