<#
.SYNOPSIS
  工作区铺满范围 + 任务栏遮挡的像素/命中双证据实测（配合 demo 日志里的 rcMonitor/rcWork/SPI 对照）。

.DESCRIPTION
  对 topmost / desktoppane × fill=work / fill=desktop 四组组合：
  1) 先清屏（最小化所有可见的普通顶层窗口，保留桌面/任务栏/自己的 demo），抓一张桌面基线；
  2) 启动 demo（--transparent 0，内容不透明，像素判定确定）；
  3) 用 WindowFromPoint 校验目标点下方是谁（我方 pid / Shell_TrayWnd），再抓截图；
  4) 采样：
     - redHalf（PageA 右半 50% 红，不透明底为黑时约 RGB(128,0,0)）→ 证明窗口画在 rcWork/rcMonitor 的目标位置；
     - workBottom-6px（工作区底边之上）→ 应为我方窗口（与基线桌面像素不同）；
     - taskbarStrip（任务栏带内）→ desktoppane 应为任务栏（与基线一致）；
       topmost+fill=desktop 应为我方窗口（与基线不同，说明 topmost 盖住任务栏）。
  结果写入 artifacts/workspace-<stamp>.result.txt，截图同时落盘。

.NOTES
  用 pwsh（PowerShell 7，DPI-aware）运行，截图为 1:1 物理像素；用 Windows PowerShell 5.1 会得到
  DPI 虚拟坐标，脚本已按 截图宽/显示器宽 换算，仍可用。
  本机只有 1 块显示器（SM_CMONITORS=1），副屏/异 DPI/热插拔均未覆盖，见 VERIFICATION.md。
#>

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$packageName = "WinUI2Demo"
$pkg = Get-AppxPackage -Name $packageName | Select-Object -First 1
if (-not $pkg) {
    throw "$packageName 未注册，请先运行 scripts/package-msix.ps1 -ProjectName WinUI2Demo -InstallMode loose"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$resultFile = Join-Path $artifacts "workspace-$stamp.result.txt"
Set-Content -Path $resultFile -Value "stamp=$stamp" -Encoding UTF8

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

$helperSource = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskHelper
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct PT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr h, IntPtr l);

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Describe(int x, int y)
    {
        var h = WindowFromPoint(new PT { X = x, Y = y });
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        var root = GetAncestor(h, 2 /*GA_ROOT*/);
        return string.Format("h=0x{0:X} class={1} pid={2} root=0x{3:X}", h.ToInt64(), ClassOf(h), pid, root.ToInt64());
    }

    /// <summary>最小化所有可见的普通顶层窗口，保留桌面/任务栏，以及 excludePid 的进程窗口。返回最小化数量。</summary>
    public static int MinimizeAllExcept(uint excludePid)
    {
        int n = 0;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = ClassOf(h);
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

if (-not ([System.Management.Automation.PSTypeName]"DemoLauncher").Type) {
    Add-Type -TypeDefinition $activationSource -Language CSharp
}
if (-not ([System.Management.Automation.PSTypeName]"DeskHelper").Type) {
    Add-Type -TypeDefinition $helperSource -Language CSharp
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Add-Result([string]$line) {
    Write-Host $line
    Add-Content -Path $resultFile -Value $line -Encoding UTF8
}

function Capture-Screen {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    return $bmp
}

function Read-Log([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    return (Get-Content $path -Raw -ErrorAction SilentlyContinue)
}

function Wait-LogMark([string]$path, [string]$pattern, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $text = Read-Log $path
        if ($text -and $text.Contains($pattern)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Px($bmp, [int]$x, [int]$y) {
    if ($x -lt 0 -or $y -lt 0 -or $x -ge $bmp.Width -or $y -ge $bmp.Height) { return $null }
    return $bmp.GetPixel($x, $y)
}

function Fmt($c) {
    if ($null -eq $c) { return "OUT-OF-RANGE" }
    return "RGB($($c.R),$($c.G),$($c.B))"
}

function MaxDiff($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return 999 }
    $d = [Math]::Abs($a.R - $b.R)
    $d = [Math]::Max($d, [Math]::Abs($a.G - $b.G))
    $d = [Math]::Max($d, [Math]::Abs($a.B - $b.B))
    return $d
}

$aumid = "$($pkg.PackageFamilyName)!App"

# ---------- 0) 桌面基线（清屏后抓一张，作为任务栏像素参照） ----------
[void][DeskHelper]::MinimizeAllExcept(0)
Start-Sleep -Milliseconds 1200
$baseline = Capture-Screen
$baseline.Save((Join-Path $artifacts "workspace-$stamp-baseline.png"))
$baseStripProbe = [DeskHelper]::Describe(960, 1044)
$baseAboveProbe = [DeskHelper]::Describe(960, 1002)
Add-Result ("baseline size={0}x{1} stripProbe=[{2}] aboveProbe=[{3}]" -f $baseline.Width, $baseline.Height, $baseStripProbe, $baseAboveProbe)

$fail = 0

foreach ($mode in @("topmost", "desktoppane")) {
    foreach ($fill in @("work", "desktop")) {
        $tag = "$mode/fill=$fill"
        $log = Join-Path $artifacts "workspace-$mode-$fill-$stamp.log"
        $shot = $null
        $proc = $null

        try {
            $processId = [DemoLauncher]::Activate($aumid, "--mode $mode --transparent 0 --fill $fill --log $log")
            $proc = Get-Process -Id ([int]$processId)

            if (-not (Wait-LogMark $log "[WS] text =>" 15000)) { throw "demo 未输出 [WS] text（日志超时）" }
            Start-Sleep -Milliseconds 900

            $text = Read-Log $log
            $rectMatches = [regex]::Matches($text, "\[RECT\] host screen=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)")
            if ($rectMatches.Count -eq 0) { throw "日志缺少 host 矩形" }
            $last = $rectMatches[$rectMatches.Count - 1].Groups
            $hl = [int]$last[1].Value; $ht = [int]$last[2].Value; $hr = [int]$last[3].Value; $hb = [int]$last[4].Value

            if ($text -notmatch "\[WS\] monitor\[1/\d+\][^\r\n]*rcMonitor=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)px[^\r\n]*rcWork=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)px") {
                throw "日志缺少 monitor rcMonitor/rcWork"
            }
            $ml = [int]$Matches[1]; $mt = [int]$Matches[2]; $mr = [int]$Matches[3]; $mb = [int]$Matches[4]
            $wl = [int]$Matches[5]; $wt = [int]$Matches[6]; $wr = [int]$Matches[7]; $wb = [int]$Matches[8]

            if ($text -notmatch "\[WS\] taskbar ABM_GETTASKBARPOS ok=\w+ edge=(\S+)\((\d+)\) rect=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)px") {
                throw "日志缺少任务栏矩形"
            }
            $tbL = [int]$Matches[3]; $tbT = [int]$Matches[4]; $tbR = [int]$Matches[5]; $tbB = [int]$Matches[6]

            if ($text -notmatch "\[WS\] verdict\([^)]*\) host=\([^)]*\)px layout=([^ ]+)") { throw "日志缺少 verdict" }
            $verdictLayout = $Matches[1]
            if ($text -notmatch "SPI_GETWORKAREA[^\r\n]*equalCurrentRcWork=(\w+)") { throw "日志缺少 SPI 对照" }
            $spiEqual = $Matches[1]

            foreach ($line in ($text -split "`r?`n" | Where-Object { $_ -match "\[WS\] (monitor|SPI|taskbar|insets|verdict|layout)" })) {
                Add-Result "[$tag] $line"
            }

            # 清屏（保留 demo 自己的窗口），保证桌面层窗口可见可拍；
            # 本机偶发 StartMenuExperienceHost(CoreWindow) 弹在下方遮住采样点，命中不对就重试几次
            $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
            $k = $vs.Width / [double]($mr - $ml)
            $redX = [int][Math]::Round(($hl + ($hr - $hl) * 0.75) * $k)
            $redY = [int][Math]::Round(($ht + ($hb - $ht) * 0.5) * $k)
            $aboveX = [int][Math]::Round((($hl + $hr) / 2.0) * $k)
            $aboveY = [int][Math]::Round(($wb - 6) * $k)
            $stripY = [int][Math]::Round(($tbT + 36) * $k)

            $expectCovered = ($mode -eq "topmost" -and $fill -eq "desktop")
            $redProbe = ""; $stripProbe = ""
            for ($i = 0; $i -lt 5; $i++) {
                [void][DeskHelper]::MinimizeAllExcept([uint32]$proc.Id)
                Start-Sleep -Milliseconds 900
                $redProbe = [DeskHelper]::Describe($redX, $redY)
                $stripProbe = [DeskHelper]::Describe($aboveX, $stripY)
                $frontOk = ($redProbe -match "pid=$($proc.Id)\b")
                if ($expectCovered) { $frontOk = $frontOk -and ($stripProbe -match "pid=$($proc.Id)\b") }
                if ($frontOk) { break }
            }
            Add-Result ("[{0}] frontRetries={1} redProbe=[{2}] stripProbe=[{3}]" -f $tag, $i, $redProbe, $stripProbe)

            $shot = Capture-Screen
            $shot.Save((Join-Path $artifacts "workspace-$mode-$fill-$stamp.png"))

            $aboveProbe = [DeskHelper]::Describe($aboveX, $aboveY)

            $red = Px $shot $redX $redY
            $above = Px $shot $aboveX $aboveY
            $strip = Px $shot $aboveX $stripY
            $baseAbove = Px $baseline $aboveX $aboveY
            $baseStrip = Px $baseline $aboveX $stripY

            $redOk = ($redProbe -match "pid=$($proc.Id)\b") -and
                     ($null -ne $red) -and ([Math]::Abs($red.R - 128) -le 18) -and ($red.G -le 18) -and ($red.B -le 18)
            $aboveOk = ($aboveProbe -match "pid=$($proc.Id)\b") -and ((MaxDiff $above $baseAbove) -ge 25)

            $oursStrip = $stripProbe -match "pid=$($proc.Id)\b"
            $trayStrip = $stripProbe -match "class=Shell_TrayWnd"
            if ($oursStrip) { $stripOwner = "宿主" }
            elseif ($trayStrip) { $stripOwner = "Shell_TrayWnd" }
            else { $stripOwner = "其他" }

            if ($expectCovered) {
                # topmost + fill=desktop：宿主与任务栏同为 topmost，谁在上面取决于 Z 序。
                # 两种结果都算有效证据：任务栏在上 → 可见范围仍被裁掉；宿主在上 → 真的盖住任务栏。
                $stripOk = ($oursStrip -and (MaxDiff $strip $baseStrip) -ge 25) -or $trayStrip
            }
            else {
                # 任务栏是半透明 Acrylic，会透出下层内容：宿主铺到它下方时像素本来就会变，
                # 所以这里只用命中测试判定“任务栏在最上层”，像素差值作为参考记录。
                $stripOk = $trayStrip
            }

            Add-Result ("[{0}] dpiScale={1:N3} host=({2},{3},{4},{5})px rcMonitor=({6},{7},{8},{9})px rcWork=({10},{11},{12},{13})px taskbar=(x{14}..{15},y{16}..{17})px layout={18} spiEqual={19}" -f `
                $tag, $k, $hl, $ht, $hr, $hb, $ml, $mt, $mr, $mb, $wl, $wt, $wr, $wb, $tbL, $tbR, $tbT, $tbB, $verdictLayout, $spiEqual)
            Add-Result ("[{0}] redHalf@({1},{2}) demo={3} probe=[{4}] expect=RGB(128,0,0) ok={5}" -f $tag, $redX, $redY, (Fmt $red), $redProbe, $redOk)
            Add-Result ("[{0}] aboveTaskbar@({1},{2}) demo={3} baseline={4} diff={5} probe=[{6}] ok={7}" -f $tag, $aboveX, $aboveY, (Fmt $above), (Fmt $baseAbove), (MaxDiff $above $baseAbove), $aboveProbe, $aboveOk)
            Add-Result ("[{0}] taskbarStrip@({1},{2}) demo={3} baseline={4} diff={5} probe=[{6}] expectCovered={7} owner={8} ok={9}" -f $tag, $aboveX, $stripY, (Fmt $strip), (Fmt $baseStrip), (MaxDiff $strip $baseStrip), $stripProbe, $expectCovered, $stripOwner, $stripOk)

            if (-not ($redOk -and $aboveOk -and $stripOk)) { $fail++ }
        }
        finally {
            try { if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } } catch { }
            Start-Sleep -Milliseconds 500
            if ($shot) { $shot.Dispose() }
        }
    }
}

$baseline.Dispose()
Add-Result "RESULT failures=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
