<#
.SYNOPSIS
  验证 FileViewPage：真实点击进入/返回、透明开关、滚动到中段、图标管线、宏键退出，并采集性能日志。

.DESCRIPTION
  用 UIA 定位 + mouse_event 真实点击；滚动用 UIA ScrollPattern（ListView 支持）到 50%。
  所有判定都基于 demo 日志中的 [FV]/[NAV]/[DEMO] 标记，不靠代码自评。

.PARAMETER Mode
  topmost / desktoppane / workerw。desktoppane 是“嵌入桌面且可点击”的推荐路线；
  workerw 是壁纸层（位于 SHELLDLL_DefView 之下），点击预期落不到宿主，只用于透明像素对照。

.PARAMETER Dir
  文件视图页默认目录（--dir）。实测建议用 C:\Users\gold\perf-research\data\f10k / f100k。

.PARAMETER Expect
  期望枚举项数（0 表示不校验），用于确认 1 万/10 万规模。

.PARAMETER ImageDir
  可选：切到该目录并“重新枚举”，校验 Shell 缩略图确实补上（loaded>0）。

.PARAMETER ExitBy
  click（默认，点击退出按钮）/ hotkey（Ctrl+F4，验证宏键在文件页仍可用）。

.PARAMETER Repeats
  同一次会话内用“重新枚举”重复加载次数（默认 1）。用于同缓存、同进程状态的性能复测。
#>
param(
    [ValidateSet("topmost", "desktoppane", "workerw")]
    [string]$Mode = "topmost",

    [string]$Dir = "C:\Users\gold\perf-research\data\f10k",

    [int]$Expect = 0,

    [string]$ImageDir = "",

    [ValidateSet("click", "hotkey")]
    [string]$ExitBy = "click",

    [int]$Repeats = 1,

    [int]$ScrollPercent = 50,

    # 可选：滚动扫描序列（百分比），用于验证图标缓存 LRU 淘汰/回填
    [int[]]$Sweep = @(),

    # 可选：遮挡降级实测（自建无边框覆盖窗：全屏=全遮挡，700x500=部分遮挡边界）
    [switch]$OccludeTest,

    # 可选：关闭遮挡降级（--occ 0），用于性能/内存口径与之前轮次对比
    [switch]$NoOcc,

    [switch]$MemGc,

    [string]$LogPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$packageName = "WinUI2Demo"
$pkg = Get-AppxPackage -Name $packageName | Select-Object -First 1
if (-not $pkg) {
    throw "$packageName 未注册，请先运行 scripts/package-msix.ps1 -ProjectName WinUI2Demo -PackageName WinUI2Demo -InstallMode loose"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if (-not $LogPath) {
    $LogPath = Join-Path $artifacts "WinUI2Demo-FileView-$Mode-$stamp.log"
}
$resultFile = Join-Path $artifacts "WinUI2Demo-FileView-$Mode-$stamp.result.txt"

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
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    public struct PT { public int X; public int Y; }

    const uint LEFTDOWN = 0x0002;
    const uint LEFTUP = 0x0004;
    const uint KEYUP = 0x0002;
    const byte VK_CONTROL = 0x11;
    const byte VK_F4 = 0x73;

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

    public static void SendCtrlF4()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F4, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(VK_F4, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }
}
"@

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
            ShowWindow(h, 6);
            n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]"DemoLauncher").Type) {
    Add-Type -TypeDefinition $activationSource -Language CSharp
}
if (-not ([System.Management.Automation.PSTypeName]"RealMouse").Type) {
    Add-Type -TypeDefinition $mouseSource -Language CSharp
}
if (-not ([System.Management.Automation.PSTypeName]"WinCleanup").Type) {
    Add-Type -TypeDefinition $cleanupSource -Language CSharp
}

$coverSource = @"
using System;
using System.Runtime.InteropServices;
public static class CoverWin {
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] static extern IntPtr FindWindow(string cls, string title);
    public static IntPtr ByTitle(string title) { return FindWindow(null, title); }
    public static bool Maximize(IntPtr h) { ShowWindow(h, 3); SetForegroundWindow(h); return true; }
    public static bool Normal(IntPtr h) { ShowWindow(h, 9); SetForegroundWindow(h); return true; }
    // 部分遮挡边界：缩到 700x500 并移到左上，不再覆盖宿主 95% 面积
    public static bool Partial(IntPtr h) {
        ShowWindow(h, 9);
        SetWindowPos(h, IntPtr.Zero, 120, 120, 700, 500, 0x0004 | 0x0010);
        return true;
    }
}
"@
if (-not ([System.Management.Automation.PSTypeName]"CoverWin").Type) {
    Add-Type -TypeDefinition $coverSource -Language CSharp
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

function Wait-LogCount([string]$pattern, [int]$minCount, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $text = Read-Log
        if ($text -and ([regex]::Matches($text, $pattern)).Count -ge $minCount) { return $true }
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

function Find-ByAutomationId([System.Windows.Automation.AutomationElement]$scope, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-UsableElement([string]$name, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $el = Find-ByName $scope $name
        if ($el -and $el.Current.BoundingRectangle.Width -gt 1 -and $el.Current.BoundingRectangle.Height -gt 1) {
            return $el
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Ensure-DemoUnder([int]$x, [int]$y) {
    if ($Mode -ne "topmost") {
        for ($i = 0; $i -lt 3; $i++) {
            [void][WinCleanup]::MinimizeAllExcept([uint32]$proc.Id)
            Start-Sleep -Milliseconds 500
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

function Click-Toggle([System.Windows.Automation.AutomationElement]$el, [string]$why) {
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.Right - 18)
    $y = [int]($r.Bottom - 12)
    Click-At $x $y $why
}

function Get-RunMetrics([string]$text) {
    $mFirst = [regex]::Matches($text, "\[FV\] firstBatch count=(\d+) atMs=(\d+) uiDelayMs=(\d+) applyMs=(\d+) enumMs=(\d+)") | Select-Object -Last 1
    $mEnum = [regex]::Matches($text, "\[FV\] enum done count=(\d+) enumMs=(\d+) error=(\S+)") | Select-Object -Last 1
    $mFeed = [regex]::Matches($text, "\[FV\] feed done shown=(\d+) batches=(\d+) firstPaintMs=(\d+) feedDoneMs=(\d+) uiApplyMaxMs=(\d+) icons requested=(\d+) loaded=(\d+) failed=(\d+)") | Select-Object -Last 1
    $mHb = [regex]::Matches($text, "\[FV\] heartbeat feed maxGapMs=(\d+) samples=(\d+)") | Select-Object -Last 1
    $line = "runMetrics"
    if ($mFirst) { $line += " firstBatch=$($mFirst.Groups[1].Value)@$($mFirst.Groups[2].Value)ms uiDelay=$($mFirst.Groups[3].Value)ms" }
    if ($mEnum) { $line += " enumMs=$($mEnum.Groups[2].Value) count=$($mEnum.Groups[1].Value)" }
    if ($mFeed) { $line += " feedDoneMs=$($mFeed.Groups[4].Value) batches=$($mFeed.Groups[2].Value) uiApplyMax=$($mFeed.Groups[5].Value)ms" }
    if ($mHb) { $line += " hbGap=$($mHb.Groups[1].Value)ms/$($mHb.Groups[2].Value)" }
    return $line
}

$results = New-Object System.Collections.Generic.List[string]
function Add-Result([string]$line) {
    $results.Add($line)
    Write-Host $line
    Add-Content -Path $resultFile -Value $line -Encoding UTF8
}

Set-Content -Path $resultFile -Value "mode=$Mode dir=$Dir expect=$Expect log=$LogPath" -Encoding UTF8

$argString = "--mode $Mode --transparent 1 --fill work --dir `"$Dir`" --log `"$LogPath`""
if ($MemGc) { $argString += " --memgc 1" }
if ($NoOcc) { $argString += " --occ 0" }
$aumid = "$($pkg.PackageFamilyName)!App"
$processId = [DemoLauncher]::Activate($aumid, $argString)
$proc = Get-Process -Id ([int]$processId)
Add-Result "pid=$($proc.Id) aumid=$aumid"

if (-not (Wait-LogMark "[RECT] host screen=" 15000)) { throw "宿主窗口未就绪（日志超时）" }
Start-Sleep -Milliseconds 800

$hostHwnd = Get-HostHwnd
Add-Result "hostHwnd=0x$($hostHwnd.ToString('X'))"

if ($Mode -ne "topmost") {
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
$fgOk = [RealMouse]::EnsureForeground($hostHwnd, 6000)
Add-Result "foreground ok=$fgOk"

$fail = 0
$fileViewBtn = Find-ByName $scope "→ 文件视图"
$trans = Find-ByName $scope "透明"
$quitBtn = Find-ByName $scope "退出"

Add-Result "found: fileView=$([bool]$fileViewBtn) transparency=$([bool]$trans) quit=$([bool]$quitBtn)"

# 1) 真实点击进入文件视图
if ($fileViewBtn) {
    Click-Element $fileViewBtn
    $ok = Wait-LogMark "[NAV] Navigated to WinUI2Demo.FileViewPage" 12000
    if (-not $ok) {
        Add-Result "RETRY nav: 补点一次文件视图入口"
        Click-Element $fileViewBtn
        $ok = Wait-LogMark "[NAV] Navigated to WinUI2Demo.FileViewPage" 6000
    }
    if ($ok) { Add-Result "PASS enter: 真实点击进入 FileViewPage" }
    else { Add-Result "FAIL enter: 未收到 FileViewPage 导航"; $fail++ }
}
else { Add-Result "FAIL enter: UIA 未找到“→ 文件视图”"; $fail++ }

# 2) 等枚举/喂入完成
$feedDone = Wait-LogCount "\[FV\] feed done" 1 30000
if (-not $feedDone) {
    Add-Result "FAIL feed: 30s 内未完成枚举/喂入"
    $fail++
}
else {
    Add-Result "PASS feed: 枚举/喂入完成"
}

Start-Sleep -Milliseconds 500
$text = Read-Log

$mFirst = [regex]::Matches($text, "\[FV\] firstBatch count=(\d+) atMs=(\d+) uiDelayMs=(\d+) applyMs=(\d+) enumMs=(\d+)") |
    Select-Object -Last 1
if ($mFirst) {
    Add-Result ("firstBatch count={0} atMs={1} uiDelayMs={2} applyMs={3} enumMs={4}" -f
        $mFirst.Groups[1].Value, $mFirst.Groups[2].Value, $mFirst.Groups[3].Value, $mFirst.Groups[4].Value, $mFirst.Groups[5].Value)
}
else { Add-Result "FAIL firstBatch: 未找到首批日志"; $fail++ }

$mRealized = [regex]::Matches($text, "\[FV\] firstRealized name='([^']*)' atMs=(\d+)") | Select-Object -Last 1
if ($mRealized) {
    Add-Result ("firstRealized name={0} atMs={1}" -f $mRealized.Groups[1].Value, $mRealized.Groups[2].Value)
}
else { Add-Result "FAIL firstRealized: 没有容器实现日志"; $fail++ }

$mEnum = [regex]::Matches($text, "\[FV\] enum done count=(\d+) enumMs=(\d+) error=(\S+)") | Select-Object -Last 1
if ($mEnum) {
    $enumCount = [int]$mEnum.Groups[1].Value
    Add-Result ("enum done count={0} enumMs={1} error={2}" -f $enumCount, $mEnum.Groups[2].Value, $mEnum.Groups[3].Value)
    if ($mEnum.Groups[3].Value -ne "none") { Add-Result "FAIL enum: error=$($mEnum.Groups[3].Value)"; $fail++ }
    if ($Expect -gt 0 -and $enumCount -ne $Expect) {
        Add-Result "FAIL enum: 期望 $Expect 项，实际 $enumCount 项"
        $fail++
    }
}
else { Add-Result "FAIL enum: 未找到枚举完成日志"; $fail++ }

$mFeed = [regex]::Matches($text, "\[FV\] feed done shown=(\d+) batches=(\d+) firstPaintMs=(\d+) feedDoneMs=(\d+) uiApplyMaxMs=(\d+) icons requested=(\d+) loaded=(\d+) failed=(\d+)") |
    Select-Object -Last 1
$shownCount = 0
if ($mFeed) {
    $shownCount = [int]$mFeed.Groups[1].Value
    Add-Result ("feed done shown={0} batches={1} firstPaintMs={2} feedDoneMs={3} uiApplyMaxMs={4} iconsReq={5} iconsLoadedAtDone={6} iconsFailedAtDone={7}" -f
        $mFeed.Groups[1].Value, $mFeed.Groups[2].Value, $mFeed.Groups[3].Value,
        $mFeed.Groups[4].Value, $mFeed.Groups[5].Value,
        $mFeed.Groups[6].Value, $mFeed.Groups[7].Value, $mFeed.Groups[8].Value)
    if ($Expect -gt 0 -and $shownCount -ne $Expect) {
        Add-Result "FAIL feed: 期望显示 $Expect 项，实际 $shownCount 项"
        $fail++
    }
}
else { Add-Result "FAIL feed: 未找到 feed done 明细"; $fail++ }

$mHb = [regex]::Matches($text, "\[FV\] heartbeat feed maxGapMs=(\d+) samples=(\d+)") | Select-Object -Last 1
if ($mHb) {
    $gap = [int]$mHb.Groups[1].Value
    Add-Result "heartbeat feed maxGapMs=$gap samples=$($mHb.Groups[2].Value)"
    if ($gap -gt 500) { Add-Result "FAIL heartbeat: 喂入期间 UI 停顿 ${gap}ms > 500ms"; $fail++ }
    else { Add-Result "PASS heartbeat: 喂入期间 UI 最大停顿 ${gap}ms" }
}
else { Add-Result "FAIL heartbeat: 未找到心跳汇总"; $fail++ }

# 2b) 同一会话内重复加载（同缓存、同进程状态），用于性能口径复测
if ($Repeats -gt 1) {
    Add-Result ("run1: " + (Get-RunMetrics (Read-Log)))
    $feedCount = ([regex]::Matches((Read-Log), "\[FV\] feed done")).Count
    $reloadBtn = Find-ByName $scope "重新枚举"
    if (-not $reloadBtn) { Add-Result "FAIL repeat: UIA 未找到“重新枚举”"; $fail++ }
    else {
        for ($k = 2; $k -le $Repeats; $k++) {
            Click-Element $reloadBtn
            $okRun = Wait-LogCount "\[FV\] feed done" ($feedCount + 1) 30000
            if (-not $okRun) { Add-Result "FAIL repeat ${k}: 未完成再一次枚举/喂入"; $fail++; break }
            $feedCount++
            Start-Sleep -Milliseconds 500
            Add-Result ("run${k}: " + (Get-RunMetrics (Read-Log)))
        }
    }
}

# 3) 文件页内点击透明开关（真实点击证明 UI 可交互；再打开恢复透明）
if ($trans) {
    Click-Toggle $trans "transparency off"
    $ok1 = Wait-LogCount "\[DEMO\] transparency toggled" 1 4000
    Start-Sleep -Milliseconds 500
    Click-Toggle $trans "transparency on"
    $ok2 = Wait-LogCount "\[DEMO\] transparency toggled" 2 4000
    if ($ok1 -and $ok2) { Add-Result "PASS transparency: 文件页内透明关/开均生效" }
    else { Add-Result "FAIL transparency: toggled 日志次数不足"; $fail++ }
}
else { Add-Result "FAIL transparency: UIA 未找到“透明”"; $fail++ }

# 3b) 遮挡检测与分级降级（R8）：自建无边框窗口做可控全遮挡 / 部分遮挡边界
if ($OccludeTest) {
    $coverScript = @'
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object System.Windows.Forms.Form
$f.Text = "WinUI2DemoCover"
$f.FormBorderStyle = "None"
$f.StartPosition = "Manual"
$f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$f.TopMost = $false
[System.Windows.Forms.Application]::Run($f)
'@
    $coverFile = Join-Path $env:TEMP "winui2demo-coverwin.ps1"
    Set-Content -Path $coverFile -Value $coverScript -Encoding UTF8
    $note = Start-Process powershell -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", $coverFile) -PassThru
    $hw = [IntPtr]::Zero
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Milliseconds 100
        $p = Get-Process -Id $note.Id -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne 0) { $hw = [IntPtr]$p.MainWindowHandle; break }
        $byTitle = [CoverWin]::ByTitle("WinUI2DemoCover")
        if ($byTitle -ne [IntPtr]::Zero) { $hw = $byTitle; break }
    }

    if ($hw -eq [IntPtr]::Zero) {
        Add-Result "FAIL occlude: 覆盖窗口未出现"; $fail++
        Stop-Process -Id $note.Id -Force -ErrorAction SilentlyContinue
    }
    else {
        $okOcc = (Wait-LogCount "\[OCC\] occluded=1" 1 12000)
        if ($okOcc) { Add-Result "PASS occlude: 全屏覆盖窗口 → [OCC] occluded=1" }
        else { Add-Result "FAIL occlude: 全屏覆盖未触发遮挡判定"; $fail++ }

        Start-Sleep -Milliseconds 1200
        $tOcc = Read-Log
        $mOcc = [regex]::Matches($tOcc, "\[OCC\] occluded=1 reason=([^\s]+)") | Select-Object -Last 1
        $mT1 = [regex]::Matches($tOcc, "\[OCC\]\[FV\] tier1 icons released=(\d+) cache=\d+->0 managedDeltaKB=(-?\d+)") | Select-Object -Last 1
        $mT3 = [regex]::Matches($tOcc, "\[OCC\]\[FV\] tier3 batchLimit=(\d+) bufferPeak=(\d+)") | Select-Object -Last 1
        $mT4 = [regex]::Matches($tOcc, "\[MEM\] tag=occ\.t4 wsBefore=([\d.]+)MB wsAfterGc=([\d.]+)MB wsAfterTrim=([\d.]+)MB private=([\d.]+)MB") | Select-Object -Last 1
        if ($mOcc) { Add-Result "occlude reason=$($mOcc.Groups[1].Value)" }
        if ($mT1) { Add-Result ("tier1 icons released={0} managedDeltaKB={1}" -f $mT1.Groups[1].Value, $mT1.Groups[2].Value) }
        else { Add-Result "FAIL occlude: 缺 tier1 日志"; $fail++ }
        if ($mT3) { Add-Result ("tier3 batchLimit={0} bufferPeak={1}" -f $mT3.Groups[1].Value, $mT3.Groups[2].Value) }
        else { Add-Result "FAIL occlude: 缺 tier3 日志"; $fail++ }
        if ($mT4) { Add-Result ("tier4 ws {0}->{1}->{2}MB private={3}MB" -f $mT4.Groups[1].Value, $mT4.Groups[2].Value, $mT4.Groups[3].Value, $mT4.Groups[4].Value) }
        else { Add-Result "FAIL occlude: 缺 tier4 日志"; $fail++ }

        $clearBefore = ([regex]::Matches((Read-Log), "\[OCC\] occluded=0")).Count
        [CoverWin]::Partial($hw) | Out-Null
        $okClear = Wait-LogCount "\[OCC\] occluded=0" ($clearBefore + 1) 10000
        if ($okClear) { Add-Result "PASS occlude-boundary: 700x500 部分遮挡 → occluded=0" }
        else { Add-Result "FAIL occlude-boundary: 部分遮挡被误判为全遮挡"; $fail++ }

        $occ2Before = ([regex]::Matches((Read-Log), "\[OCC\] occluded=1")).Count
        [CoverWin]::Maximize($hw) | Out-Null
        $okOcc2 = Wait-LogCount "\[OCC\] occluded=1" ($occ2Before + 1) 10000
        if ($okOcc2) { Add-Result "PASS occlude-again: 二次全屏再次触发" }
        else { Add-Result "FAIL occlude-again: 二次全屏未触发"; $fail++ }

        Stop-Process -Id $note.Id -Force -ErrorAction SilentlyContinue
        $okRestore = Wait-LogCount "\[OCC\]\[FV\] restore" 1 8000
        Start-Sleep -Milliseconds 500
        $mR = [regex]::Matches((Read-Log), "\[OCC\]\[FV\] restore pending=(\d+) restoreMs=(\d+) verdict=([^\s]+)") | Select-Object -Last 1
        if ($okRestore -and $mR) {
            Add-Result ("restore pending={0} restoreMs={1} verdict={2}" -f $mR.Groups[1].Value, $mR.Groups[2].Value, $mR.Groups[3].Value)
            if ([int]$mR.Groups[2].Value -le 1000) { Add-Result "PASS restore: 恢复到可用 ≤1s" }
            else { Add-Result "FAIL restore: >1s（按‘不划算’记录）"; $fail++ }
        }
        else { Add-Result "FAIL restore: 未收到恢复计时"; $fail++ }
    }
}

# 4) 滚动到中段：UIA ScrollPattern 50%，看 firstVisible 与 realized 容器数
$list = Find-ByAutomationId $scope "FileList"
if ($list) {
    Add-Result "list found: name='$($list.Current.Name)' class='$($list.Current.ClassName)'"
    try {
        $sp = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $settleBefore = ([regex]::Matches((Read-Log), "\[FV\] view\(settle\)")).Count
        $sp.SetScrollPercent(-1, $ScrollPercent)
        Add-Result "scroll: SetScrollPercent(horizontal=-1, vertical=$ScrollPercent) ok"
        $okSettle = Wait-LogCount "\[FV\] view\(settle\)" ($settleBefore + 1) 8000
        Add-Result "scroll: settle log received=$okSettle"
        Start-Sleep -Milliseconds 300
        $t2 = Read-Log
        $mView = [regex]::Matches($t2, "\[FV\] view\(settle\) first=(-?\d+) last=(-?\d+) realized=(\d+) shown=(\d+)") | Select-Object -Last 1
        if ($mView) {
            $first = [int]$mView.Groups[1].Value
            $realized = [int]$mView.Groups[3].Value
            Add-Result ("view settle first={0} last={1} realized={2} shown={3}" -f
                $mView.Groups[1].Value, $mView.Groups[2].Value, $mView.Groups[3].Value, $mView.Groups[4].Value)
            if ($first -gt 100) { Add-Result "PASS scroll: 已滚动到中段（first=$first）" }
            else { Add-Result "FAIL scroll: first=$first，未到中段"; $fail++ }
            if ($realized -lt 200) { Add-Result "PASS virtualization: 容器 realized=$realized（远小于列表项数）" }
            else { Add-Result "FAIL virtualization: realized=$realized 过多"; $fail++ }
        }
        else { Add-Result "FAIL scroll: 未收到 view(settle) 日志"; $fail++ }
        $mHb2 = [regex]::Matches((Read-Log), "\[FV\] heartbeat scroll maxGapMs=(\d+) samples=(\d+)") | Select-Object -Last 1
        if ($mHb2) { Add-Result "heartbeat scroll maxGapMs=$($mHb2.Groups[1].Value) samples=$($mHb2.Groups[2].Value)" }
        else { Add-Result "FAIL scroll: 未收到滚动心跳汇总"; $fail++ }
    }
    catch {
        Add-Result "FAIL scroll: ScrollPattern 不可用或调用失败：$($_.Exception.Message)"
        $fail++
    }
}
else { Add-Result "FAIL scroll: UIA 未找到 FileList"; $fail++ }

# 4b) 可选：滚动扫描，观察图标缓存上限/淘汰/回填（每次停 1.5 秒等 settle 汇总）
if ($Sweep.Count -gt 0 -and $list) {
    $sp2 = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    foreach ($pct in $Sweep) {
        $before = ([regex]::Matches((Read-Log), "\[FV\] view\(settle\)")).Count
        $sp2.SetScrollPercent(-1, $pct)
        $ok = Wait-LogCount "\[FV\] view\(settle\)" ($before + 1) 8000
        Start-Sleep -Milliseconds 300
        $tS = Read-Log
        $mV = [regex]::Matches($tS, "\[FV\] view\(settle\) first=(-?\d+) last=(-?\d+) realized=(\d+) shown=(\d+) icons requested=(\d+) loaded=(\d+) failed=(\d+) evicted=(\d+) cache=(\d+) visibleMissing=(\d+)") | Select-Object -Last 1
        if ($mV) {
            Add-Result ("sweep pct={0} settle={1} first={2} cache={3} evicted={4} visibleMissing={5} loaded={6}" -f
                $pct, $ok, $mV.Groups[1].Value, $mV.Groups[9].Value, $mV.Groups[8].Value, $mV.Groups[10].Value, $mV.Groups[6].Value)
        }
        else { Add-Result "FAIL sweep: pct=$pct 未找到带缓存字段的 view(settle)"; $fail++ }
    }
}

# 5) 返回 PageA，再进入一次文件页（导航进/出后能力仍正常）
# PageA 上也有一个禁用的“← 返回”，进入文件页后必须重新查找当前页的返回按钮
$backBtn = Find-ByName $scope "← 返回"
if ($backBtn) {
    Click-Element $backBtn
    $okBack = Wait-LogMark "[NAV] Navigated to WinUI2Demo.PageA" 6000
    if ($okBack) { Add-Result "PASS back: 返回 PageA 成功" }
    else { Add-Result "FAIL back: 未返回 PageA"; $fail++ }
}
else { Add-Result "FAIL back: UIA 未找到“← 返回”"; $fail++ }

if ($fileViewBtn) {
    # 返回后 PageA 若被重新创建，旧 UIA 元素会失效（BoundingRectangle 为 0/NaN），必须等新元素可点
    Start-Sleep -Milliseconds 800
    $fileViewBtn2 = Wait-UsableElement "→ 文件视图" 6000
    if ($fileViewBtn2) {
        Click-Element $fileViewBtn2
        $okReenter = Wait-LogCount "\[NAV\] Navigated to WinUI2Demo.FileViewPage" 2 8000
        $okRefeed = Wait-LogCount "\[FV\] feed done" 2 30000
        if ($okReenter -and $okRefeed) { Add-Result "PASS reenter: 第二次进入文件页并完成枚举" }
        else { Add-Result "FAIL reenter: 重新进入失败（nav=$okReenter feed=$okRefeed）"; $fail++ }
    }
    else { Add-Result "FAIL reenter: PageA 的“→ 文件视图”按钮未恢复可点"; $fail++ }
}

# 6) 可选：切到图片目录，验证真缩略图补上
if ($ImageDir -ne "") {
    $pathBox = Find-ByName $scope "目录路径"
    $reload = Find-ByName $scope "重新枚举"
    if ($pathBox -and $reload) {
        $vp = $pathBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($ImageDir)
        Start-Sleep -Milliseconds 300
        Click-Element $reload
        $settleBefore = ([regex]::Matches((Read-Log), "\[FV\] icons settle")).Count
        $okSettle = Wait-LogCount "\[FV\] icons settle" ($settleBefore + 1) 12000
        if ($okSettle) {
            $t3 = Read-Log
            $mIcon = [regex]::Matches($t3, "\[FV\] icons settle requested=(\d+) loaded=(\d+) failed=(\d+) realized=(\d+)") | Select-Object -Last 1
            if ($mIcon) {
                Add-Result ("icons settle requested={0} loaded={1} failed={2} realized={3}" -f
                    $mIcon.Groups[1].Value, $mIcon.Groups[2].Value, $mIcon.Groups[3].Value, $mIcon.Groups[4].Value)
                if ([int]$mIcon.Groups[2].Value -gt 0) { Add-Result "PASS icons: 真缩略图/图标已加载（loaded>0）" }
                else { Add-Result "FAIL icons: loaded=0，Shell 缩略图未生效"; $fail++ }
            }
            else { Add-Result "FAIL icons: 未找到 icons settle 明细"; $fail++ }
        }
        else { Add-Result "FAIL icons: 切换图片目录后未收到 icons settle"; $fail++ }
    }
    else { Add-Result "FAIL icons: UIA 未找到路径框/重新枚举按钮"; $fail++ }
}

# 7) 退出：点击按钮或宏键
if ($ExitBy -eq "hotkey") {
    [RealMouse]::SendCtrlF4()
    $okExit = Wait-LogMark "[DEMO] close requested" 5000
    if ($okExit) { Add-Result "PASS quit: Ctrl+F4 宏键在文件页仍生效" }
    else { Add-Result "FAIL quit: 未收到 close requested（宏键失效）"; $fail++ }
}
elseif ($quitBtn) {
    Click-Element $quitBtn
    $okExit = Wait-LogMark "[DEMO] close requested" 5000
    if ($okExit) { Add-Result "PASS quit: 点击退出成功" }
    else { Add-Result "FAIL quit: 未收到 close requested"; $fail++ }
}
else { Add-Result "FAIL quit: UIA 未找到“退出”"; $fail++ }

if ($okExit) {
    $exited = $proc.WaitForExit(8000)
    Add-Result "process exited=$exited"
    if (-not $exited) { Add-Result "FAIL quit: 进程未退出"; $fail++ }
}

if ($Mode -ne "topmost" -and $shell) { $shell.UndoMinimizeAll() }

if ($proc -and -not $proc.HasExited) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Add-Result "cleanup: killed leftover pid=$($proc.Id)"
}

Add-Result "RESULT mode=$Mode failures=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
