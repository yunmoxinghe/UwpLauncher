Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
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
'@

$pkg = Get-AppxPackage -Name WinUI2Demo | Select-Object -First 1
$aumid = "$($pkg.PackageFamilyName)!App"
$log = Join-Path $PSScriptRoot ("aot-scroll-{0}.log" -f (Get-Date -Format "HHmmss"))
$argString = "--mode topmost --transparent 1 --fill work --dir `"C:\Users\gold\perf-research\data\f100k`" --log `"$log`""
$processId = [DemoLauncher]::Activate($aumid, $argString)
Start-Sleep -Milliseconds 8000
$proc = Get-Process -Id ([int]$processId)
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 200
    if (Test-Path $log) {
        $txt = Get-Content $log -Raw
        if ($txt -match "\[CreateWindow\] host hwnd=0x([0-9A-Fa-f]+)") {
            $hwnd = [IntPtr]([Convert]::ToInt64($Matches[1], 16))
        }
    }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "FAIL: host hwnd not found"; exit 1 }
Write-Output "pid=$($proc.Id) hwnd=0x$($hwnd.ToString('X'))"

$scope = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "→ 文件视图")
$btn = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $btn) { Write-Output "FAIL: button not found"; exit 1 }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 5

$listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "文件列表")
$list = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
if (-not $list) { Write-Output "FAIL: list not found"; exit 1 }

function Get-VisibleNames($el) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $items = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
    $names = @()
    foreach ($i in $items) { $names += $i.Current.Name }
    return ,$names
}

$sp = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
$before = Get-VisibleNames $list
Write-Output ("BEFORE pct={0} n={1} first='{2}' last='{3}'" -f $sp.Current.VerticalScrollPercent, $before.Count, $before[0], $before[-1])
$sp.SetScrollPercent(-1, 100)
Start-Sleep -Milliseconds 2000
$after = Get-VisibleNames $list
Write-Output ("AFTER  pct={0} n={1} first='{2}' last='{3}'" -f $sp.Current.VerticalScrollPercent, $after.Count, $after[0], $after[-1])
Write-Output ("LOG=" + $log)
$proc | Stop-Process -Force
