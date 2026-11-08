param(
    [ValidateSet("topmost", "desktoppane", "workerw")]
    [string]$Mode = "desktoppane"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$pkg = Get-AppxPackage -Name "WinUI2Demo" | Select-Object -First 1

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Capture([string]$path) {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
    $bmp.Save($path); $g.Dispose(); $bmp.Dispose()
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
public static class TideLauncher {
    public static uint Activate(string aumid, string arguments) {
        var m = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid; var hr = m.ActivateApplication(aumid, arguments, 0, out pid);
        if (hr < 0) throw new COMException("fail", hr);
        return pid;
    }
}
public static class TideKeys {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void Down(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); }
    public static void Up(byte vk) { keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Mod(byte mod, byte vk) { Down(mod); System.Threading.Thread.Sleep(40); Down(vk); System.Threading.Thread.Sleep(40); Up(vk); Up(mod); }
    public static void ShowDesktop() { Down(0x5B); System.Threading.Thread.Sleep(50); Down(0x44); System.Threading.Thread.Sleep(50); Up(0x44); Up(0x5B); }
}
"@
if (-not ([System.Management.Automation.PSTypeName]"TideLauncher").Type) {
    Add-Type -TypeDefinition $src -Language CSharp
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$log = Join-Path $artifacts "toggle-$Mode-$stamp.log"
$pid2 = [TideLauncher]::Activate("$($pkg.PackageFamilyName)!App", "--mode $Mode --transparent 1 --log $log")
$proc = Get-Process -Id ([int]$pid2)

Start-Sleep -Milliseconds 1800
[TideKeys]::ShowDesktop()
Start-Sleep -Milliseconds 2600

$pngA = Join-Path $artifacts "toggle-$Mode-$stamp-on.png"
Capture $pngA
$bmp = [System.Drawing.Bitmap]::FromFile($pngA)
foreach ($p in @(@(200,300),@(900,300))) { $c=$bmp.GetPixel($p[0],$p[1]); "ON  ($($p[0]),$($p[1])) = $($c.R),$($c.G),$($c.B)" }
$bmp.Dispose()

# Ctrl+F2 = 透明开关（全局热键，不依赖焦点/点击）
[TideKeys]::Mod(0x11, 0x71)
Start-Sleep -Milliseconds 1200
$pngB = Join-Path $artifacts "toggle-$Mode-$stamp-off.png"
Capture $pngB
$bmp2 = [System.Drawing.Bitmap]::FromFile($pngB)
foreach ($p in @(@(200,300),@(900,300))) { $c=$bmp2.GetPixel($p[0],$p[1]); "OFF ($($p[0]),$($p[1])) = $($c.R),$($c.G),$($c.B)" }
$bmp2.Dispose()

# 再切回 ON，确认可逆
[TideKeys]::Mod(0x11, 0x71)
Start-Sleep -Milliseconds 1200
$pngC = Join-Path $artifacts "toggle-$Mode-$stamp-on2.png"
Capture $pngC
$bmp3 = [System.Drawing.Bitmap]::FromFile($pngC)
foreach ($p in @(@(200,300),@(900,300))) { $c=$bmp3.GetPixel($p[0],$p[1]); "ON2 ($($p[0]),$($p[1])) = $($c.R),$($c.G),$($c.B)" }
$bmp3.Dispose()

if (-not $proc.HasExited) { $proc.Kill() }
"log=$log"
"pngs=$pngA,$pngB,$pngC"
