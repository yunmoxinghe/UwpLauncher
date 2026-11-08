<#
.SYNOPSIS
  以已注册的 MSIX 包身份启动 WinUI2Demo 交互演示，可选自动冒烟测试与截图。

.DESCRIPTION
  默认以“置顶浮层 + 透明开”启动，留给用户手动操作；
  -Selftest 时走与按钮/开关相同的桥接路径自动跑完并退出（透明关/开、Page B、返回、
  嵌入桌面、回到置顶、关闭），用于打包后的冒烟验证。

.PARAMETER Mode
  启动时的初始宿主模式：topmost（置顶浮层）/ workerw（嵌入桌面）。

.PARAMETER Selftest
  自动冒烟测试，约 6 秒后自动退出。

.PARAMETER Screenshot
  运行期间抓取整屏截图，输出到 artifacts/。

.PARAMETER HoldMs
  非 Selftest 模式下，为了截图而等待的时长（毫秒）。
#>
param(
    [ValidateSet("topmost", "workerw")]
    [string]$Mode = "topmost",

    [ValidateSet("on", "off")]
    [string]$Transparent = "on",

    [switch]$Selftest,

    [switch]$Screenshot,

    [int]$HoldMs = 4000
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$packageName = "WinUI2Demo"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$pkg = Get-AppxPackage -Name $packageName | Select-Object -First 1
if (-not $pkg) {
    throw "$packageName 未注册。请先运行 scripts/package-msix.ps1 -ProjectName WinUI2Demo -PackageName WinUI2Demo"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$name = "$packageName-$Mode-$(if ($Selftest) { 'selftest' } else { 'interactive' })-$stamp"
$log = Join-Path $artifacts "$name.log"
$png = Join-Path $artifacts "$name.png"

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

public static class DemoLauncher
{
    public static uint Activate(string aumid, string arguments)
    {
        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        var hr = mgr.ActivateApplication(aumid, arguments, 0, out pid);
        if (hr < 0) throw new COMException("ActivateApplication failed", hr);
        return pid;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]"DemoLauncher").Type) {
    Add-Type -TypeDefinition $activationSource -Language CSharp
}

if ($Screenshot -or $Selftest) {
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
}

$argString = "--mode $Mode --transparent $(if ($Transparent -eq 'on') { '1' } else { '0' }) --log $log"
if ($Selftest) { $argString += " --selftest" }

$aumid = "$($pkg.PackageFamilyName)!App"
$processId = [DemoLauncher]::Activate($aumid, $argString)
$p = Get-Process -Id ([int]$processId)

if ($Selftest) {
    # 以日志标记同步截图，避免激活/启动耗时让固定 sleep 错过阶段
    function Wait-LogMark([string]$pattern, [int]$timeoutMs) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
            if (Test-Path $log) {
                $text = Get-Content $log -Raw -ErrorAction SilentlyContinue
                if ($text -and $text.Contains($pattern)) { return $true }
            }
            Start-Sleep -Milliseconds 100
        }
        return $false
    }

    $navB = Wait-LogMark "[SELFTEST] navigate PageB" 12000
    Start-Sleep -Milliseconds 250
    if ($Screenshot) { Capture-Screen (Join-Path $artifacts "$name-b.png") }

    $workerw = Wait-LogMark "[SELFTEST] mode -> workerw" 8000
    Start-Sleep -Milliseconds 500
    if ($Screenshot) {
        # 进入壁纸层后会普通窗口遮挡，最小化其他窗口再截图才能看到它
        $shell = New-Object -ComObject Shell.Application
        $shell.MinimizeAll()
        Start-Sleep -Milliseconds 900
        Capture-Screen (Join-Path $artifacts "$name-workerw.png")
        $shell.UndoMinimizeAll()
    }

    $p.WaitForExit(15000) | Out-Null
    "log-marks: navigateB=$navB workerw=$workerw"
}
else {
    Start-Sleep -Milliseconds $HoldMs
    if ($Screenshot) { Capture-Screen $png }
}

"pid=$($p.Id) exited=$($p.HasExited)"
"aumid=$aumid"
"log=$log"
if (Test-Path $log) {
    "---- log tail ----"
    Get-Content $log -Tail 40
}
