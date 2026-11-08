param(
    [ValidateSet("topmost", "workerw", "desktoppane")]
    [string]$Mode = "desktoppane",
    [ValidateSet("on", "off")]
    [string]$Transparent = "on",
    [int]$SettleMs = 2500
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$packageName = "WinUI2Demo"

$pkg = Get-AppxPackage -Name $packageName | Select-Object -First 1
if (-not $pkg) { throw "$packageName 未注册" }

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

$src = @"
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationActivationManager {
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string a, [MarshalAs(UnmanagedType.LPWStr)] string b, int c, out uint pid);
    int ActivateShellApp([MarshalAs(UnmanagedType.LPWStr)] string a, [MarshalAs(UnmanagedType.LPWStr)] string b, out uint pid);
    int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b, [MarshalAs(UnmanagedType.LPWStr)] string c, out uint pid);
    int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b, out uint pid);
}
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] public class ApplicationActivationManager { }
public static class DemoLauncher2 {
    public static uint Activate(string aumid, string arguments) {
        var m = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        var hr = m.ActivateApplication(aumid, arguments, 0, out pid);
        if (hr < 0) throw new COMException("ActivateApplication failed", hr);
        return pid;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]"DemoLauncher2").Type) {
    Add-Type -TypeDefinition $src -Language CSharp
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$name = "WinUI2Demo-$Mode-transparent$Transparent-$stamp"
$log = Join-Path $artifacts "$name.log"
$png = Join-Path $artifacts "$name.png"

$argString = "--mode $Mode --transparent $(if ($Transparent -eq 'on') { '1' } else { '0' }) --log $log"
$pid2 = [DemoLauncher2]::Activate("$($pkg.PackageFamilyName)!App", $argString)
$proc = Get-Process -Id ([int]$pid2)

Start-Sleep -Milliseconds 1500
# 用 Win+D 把桌面提到最前：desktoppane 下窗口是 Progman 的子窗，会随桌面一起显示；
# 同时避免 MinimizeAll 被 IDE 等窗口恢复打断
$keys = @"
using System;
using System.Runtime.InteropServices;
public static class Keys {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void ShowDesktop() {
        keybd_event((byte)0x5B, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event((byte)0x44, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event((byte)0x44, 0, 2, UIntPtr.Zero);
        keybd_event((byte)0x5B, 0, 2, UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static uint PidAt(int x, int y) {
        uint pid = 0;
        GetWindowThreadProcessId(WindowFromPoint(new PT { X = x, Y = y }), out pid);
        return pid;
    }
}
"@
if (-not ([System.Management.Automation.PSTypeName]"Keys").Type) {
    Add-Type -TypeDefinition $keys -Language CSharp
}
[Keys]::ShowDesktop()
# Win+D 之后再把普通窗口最小化：部分 IDE/浏览器会抢回前台，单靠 Win+D 不可靠。
# 注意 WorkerW 路线下 Win+D 会让 SHELLDLL_DefView 重绘壁纸背景、把子窗整体盖住，
# 所以壁纸层的可见性只能靠“最小化其它窗口”保证（见 VERIFICATION 的特殊行为记录）。
$shell = New-Object -ComObject Shell.Application
$shell.MinimizeAll()
Start-Sleep -Milliseconds 1600

# 校验（topmost/desktoppane）：采样点命中的 PID 必须是 demo 自己；否则反复最小化其它窗口
if ($Mode -ne "workerw") {
    $myPid = [uint32]$proc.Id
    $ok = $false
    for ($i = 0; $i -lt 6; $i++) {
        if ([Keys]::PidAt(200, 300) -eq $myPid) { $ok = $true; break }
        $shell.MinimizeAll()
        Start-Sleep -Milliseconds 900
    }
    "foreground-check mode=$Mode hitPid=$([Keys]::PidAt(200,300)) myPid=$myPid ok=$ok"
}
Start-Sleep -Milliseconds $SettleMs

Capture-Screen $png

$bmp = [System.Drawing.Bitmap]::FromFile($png)
foreach ($p in @(@(200, 300), @(1400, 300), @(700, 20))) {
    $c = $bmp.GetPixel($p[0], $p[1])
    "mode=$Mode transparent=$Transparent point=($($p[0]),$($p[1])) rgb=$($c.R),$($c.G),$($c.B)"
}
$bmp.Dispose()

if (-not $proc.HasExited) { $proc.Kill() }
Start-Sleep -Milliseconds 800

# 桌面仍在最前，抓一张“纯桌面”基线用于对比透明是否真的透出桌面
$basePng = Join-Path $artifacts "desktop-visible-$stamp.png"
Capture-Screen $basePng
$bmp2 = [System.Drawing.Bitmap]::FromFile($basePng)
foreach ($p in @(@(200, 300), @(1400, 300), @(700, 20))) {
    $c = $bmp2.GetPixel($p[0], $p[1])
    "baseline point=($($p[0]),$($p[1])) rgb=$($c.R),$($c.G),$($c.B)"
}
$bmp2.Dispose()

$shell.UndoMinimizeAll()

"png=$png"
"baseline=$basePng"
"log=$log"
