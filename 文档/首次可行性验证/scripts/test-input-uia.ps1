<#
.SYNOPSIS
  用 UIA 定位元素 + 真实鼠标点击，验证 WinUI2Demo 在各宿主模式下“点得动”。

.DESCRIPTION
  不使用 InvokePattern 等程序化调用，而是：
  1) 通过 UIA 按名称找到按钮/ToggleSwitch，读出 BoundingRectangle；
  2) 把光标移到该矩形内，用 mouse_event 发真实的左键按下/抬起；
  3) 以 demo 日志中的真实事件（导航、透明切换、退出）判定是否生效。

  非 topmost 模式会先最小化所有普通窗口，保证桌面区域的点击不会被其他应用窗口接管。

.PARAMETER Mode
  启动模式：topmost / desktoppane / workerw。

.PARAMETER LogPath
  可选，指定日志文件；默认写到 artifacts 下带时间戳的文件。
#>
param(
    [ValidateSet("topmost", "desktoppane", "workerw")]
    [string]$Mode = "desktoppane",

    [string]$LogPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$packageName = "WinUI2Demo"
$pkg = Get-AppxPackage -Name $packageName | Select-Object -First 1
if (-not $pkg) {
    throw "$packageName 未注册，请先运行 scripts/package-msix.ps1 -ProjectName WinUI2Demo -PackageName WinUI2Demo"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if (-not $LogPath) {
    $LogPath = Join-Path $artifacts "WinUI2Demo-$Mode-uia-$stamp.log"
}
$resultFile = Join-Path $artifacts "WinUI2Demo-$Mode-uia-$stamp.result.txt"

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

$mouseSource = @"
using System;
using System.Runtime.InteropServices;

public static class RealMouse
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    /// <summary>把宿主置前，等它真的成为前台窗口（避开刚安装/冷启动时的前台锁），最多等 6 秒。</summary>
    public static bool EnsureForeground(IntPtr h, int timeoutMs)
    {
        SetForegroundWindow(h);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (GetForegroundWindow() == h) return true;
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PT { public int X; public int Y; }

    const uint LEFTDOWN = 0x0002;
    const uint LEFTUP = 0x0004;

    public static string Probe(int x, int y)
    {
        var h = WindowFromPoint(new PT { X = x, Y = y });
        var sb = new System.Text.StringBuilder(64);
        GetClassNameW(h, sb, sb.Capacity);
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return string.Format("0x{0:X} class={1} pid={2}", h.ToInt64(), sb, pid);
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]"DemoLauncher").Type) {
    Add-Type -TypeDefinition $activationSource -Language CSharp
}
if (-not ([System.Management.Automation.PSTypeName]"RealMouse").Type) {
    Add-Type -TypeDefinition $mouseSource -Language CSharp
}

$cleanupSource = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WinCleanup
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr h, IntPtr l);

    /// <summary>最小化所有可见普通顶层窗口，保留桌面/任务栏与 excludePid。</summary>
    public static int MinimizeAllExcept(uint excludePid)
    {
        int n = 0;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(128);
            GetClassNameW(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                cls == "Progman" || cls == "WorkerW" || cls == "SHELLDLL_DefView" || cls == "SysListView32")
                return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == excludePid) return true;
            RECT r;
            if (!GetWindowRect(h, out r) || r.Right <= r.Left || r.Bottom <= r.Top) return true;
            ShowWindow(h, 6 /*SW_MINIMIZE*/);
            n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }
}
"@
if (-not ([System.Management.Automation.PSTypeName]"WinCleanup").Type) {
    Add-Type -TypeDefinition $cleanupSource -Language CSharp
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Read-Log {
    if (-not (Test-Path $LogPath)) { return "" }
    return (Get-Content $LogPath -Raw -ErrorAction SilentlyContinue)
}

function Wait-LogMark([string]$pattern, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $text = Read-Log
        if ($text -and $text.Contains($pattern)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Get-HostHwnd {
    $text = Read-Log
    if ($text -match "\[CreateWindow\] host hwnd=0x([0-9A-Fa-f]+)") {
        return [IntPtr]([Convert]::ToInt64($Matches[1], 16))
    }
    return [IntPtr]::Zero
}

function Find-ByName([System.Windows.Automation.AutomationElement]$scope, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Ensure-DemoUnder([int]$x, [int]$y) {
    if ($Mode -ne "topmost") {
        # 桌面层窗口低于普通应用；逐个点击前都清一次屏，避免 Chrome 等窗口回弹后吃掉点击
        for ($i = 0; $i -lt 4; $i++) {
            [void][WinCleanup]::MinimizeAllExcept([uint32]$proc.Id)
            Start-Sleep -Milliseconds 600
            $p = [RealMouse]::Probe($x, $y)
            if ($p -match "pid=$($proc.Id)\b") { return $p }
        }
        return $p
    }
    return [RealMouse]::Probe($x, $y)
}

function Click-Element([System.Windows.Automation.AutomationElement]$el, [int]$dx = 0, [int]$dy = 0) {
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.Left + $r.Width / 2) + $dx
    $y = [int]($r.Top + $r.Height / 2) + $dy
    $under = Ensure-DemoUnder $x $y
    "  click '$($el.Current.Name)' rect=($([int]$r.Left),$([int]$r.Top))-$([int]$r.Right),$([int]$r.Bottom) at ($x,$y) under=[$under]"
    [RealMouse]::Click($x, $y)
}

function Click-At([int]$x, [int]$y, [string]$why) {
    $under = Ensure-DemoUnder $x $y
    "  click $why at ($x,$y) under=[$under]"
    [RealMouse]::Click($x, $y)
}

$results = New-Object System.Collections.Generic.List[string]
function Add-Result([string]$line) {
    $results.Add($line)
    Write-Host $line
    Add-Content -Path $resultFile -Value $line -Encoding UTF8
}

Set-Content -Path $resultFile -Value "mode=$Mode log=$LogPath" -Encoding UTF8

$argString = "--mode $Mode --transparent 1 --fill work --log $LogPath"
$aumid = "$($pkg.PackageFamilyName)!App"
$processId = [DemoLauncher]::Activate($aumid, $argString)
$proc = Get-Process -Id ([int]$processId)
Add-Result "pid=$($proc.Id) aumid=$aumid"

# 等宿主窗口建好
if (-not (Wait-LogMark "[RECT] host screen=" 15000)) { throw "宿主窗口未就绪（日志超时）" }
Start-Sleep -Milliseconds 800

$hostHwnd = Get-HostHwnd
Add-Result "hostHwnd=0x$($hostHwnd.ToString('X'))"

if ($Mode -ne "topmost") {
    # 窗口在桌面层、低于所有普通应用；先把其他窗口最小化，点击才落在它身上
    $shell = New-Object -ComObject Shell.Application
    $shell.MinimizeAll()
    Start-Sleep -Milliseconds 1200
}

$scope = [System.Windows.Automation.AutomationElement]::FromHandle($hostHwnd)
if (-not $scope) {
    Add-Result "FAIL: AutomationElement.FromHandle 返回空"
    exit 1
}
Add-Result "uia scope: name='$($scope.Current.Name)' class='$($scope.Current.ClassName)'"

# 刚重装包后的首次启动可能还在前台切换/首启准备，先确保宿主成为前台窗口再点击
$fgOk = [RealMouse]::EnsureForeground($hostHwnd, 6000)
Add-Result "foreground: host=$([RealMouse]::GetForegroundWindow().ToString('X')) expected=$($hostHwnd.ToString('X')) ok=$fgOk"

$goB = Find-ByName $scope "→ 去 Page B"
$trans = Find-ByName $scope "透明"
$fill = Find-ByName $scope "铺满桌面"
$quit = Find-ByName $scope "退出"

Add-Result "found: goB=$([bool]$goB) transparency=$([bool]$trans) fill=$([bool]$fill) quit=$([bool]$quit)"

$fail = 0

# 1) 点击“去 Page B”，看真实导航日志
# 刚安装/首启时首轮点击可能被前台切换延迟（实测曾延迟约 11 秒才被 XAML 处理），
# 所以首步等待放宽到 12 秒；失败时再补一次真实点击并等 6 秒。
if ($goB) {
    Click-Element $goB
    $ok = Wait-LogMark "[NAV] Navigated to WinUI2Demo.PageB" 12000
    if (-not $ok) {
        Add-Result "RETRY nav: 首次点击未在 12s 内收到导航，补点一次"
        Click-Element $goB
        $ok = Wait-LogMark "[NAV] Navigated to WinUI2Demo.PageB" 6000
    }
    if ($ok) { Add-Result "PASS nav: 嵌入后点击按钮导航成功" }
    else { Add-Result "FAIL nav: 未收到 Navigated to PageB"; $fail++ }
}
else { Add-Result "FAIL nav: UIA 未找到“→ 去 Page B”"; $fail++ }

# 2) 点击“透明”ToggleSwitch（UIA 矩形含 header，点击右侧开关轨道）
if ($trans) {
    $r = $trans.Current.BoundingRectangle
    $x = [int]($r.Right - 18)
    $y = [int]($r.Bottom - 12)
    Click-At $x $y "transparency switch"
    $ok = Wait-LogMark "[DEMO] transparency toggled" 4000
    if ($ok) { Add-Result "PASS transparency: 点击开关切换透明成功" }
    else { Add-Result "FAIL transparency: 未收到 toggled 日志"; $fail++ }
}
else { Add-Result "FAIL transparency: UIA 未找到“透明”"; $fail++ }

# 2b) 点击“铺满桌面”ToggleSwitch：验证布局切换在嵌入态下也可交互（并会重排到 rcMonitor）
if ($fill) {
    $r = $fill.Current.BoundingRectangle
    $x = [int]($r.Right - 18)
    $y = [int]($r.Bottom - 12)
    Click-At $x $y "fill switch"
    $ok = Wait-LogMark "[DEMO] fill toggled => desktop" 4000
    if ($ok) { Add-Result "PASS fill: 点击“铺满桌面”切换布局成功" }
    else { Add-Result "FAIL fill: 未收到 fill toggled 日志"; $fail++ }
}
else { Add-Result "FAIL fill: UIA 未找到“铺满桌面”"; $fail++ }

# 3) 点击“退出”
if ($quit) {
    Click-Element $quit
    $ok = Wait-LogMark "[DEMO] close requested" 4000
    if ($ok) {
        Add-Result "PASS quit: 点击退出成功"
        $exited = $proc.WaitForExit(6000)
        Add-Result "process exited=$exited"
        if (-not $exited) { Add-Result "FAIL quit: 进程未退出"; $fail++ }
    }
    else { Add-Result "FAIL quit: 未收到 close requested"; $fail++ }
}
else { Add-Result "FAIL quit: UIA 未找到“退出”"; $fail++ }

if ($Mode -ne "topmost") { $shell.UndoMinimizeAll() }

# 若退出步骤失败（例如点击被前台切换吃掉），别把实例留给下一轮测试
if ($proc -and -not $proc.HasExited) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Add-Result "cleanup: killed leftover pid=$($proc.Id)"
}

Add-Result "RESULT mode=$Mode failures=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
