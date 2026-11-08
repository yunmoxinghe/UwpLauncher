<#
.SYNOPSIS
    安装 WinUI2Demo：优先「提权 + 机器级证书信任 + 签名安装」，失败自动退回松散注册。

.DESCRIPTION
    1) 校验 .cer 指纹与包内 AppxSignature.p7x 的签名者指纹一致（不一致就不导入，避免装错信任）
    2) 未提权时用 Start-Process -Verb RunAs 自提升，参数原样传给子进程
    3) 提权后把证书导入 LocalMachine\TrustedPeople 与 LocalMachine\Root
       （自签名证书的根就是它自己，只进一处仍会报 0x800B010A/0x800B0109），再 Add-AppxPackage 签名包
    4) 用户拒绝提权 / 证书导入失败 / 签名安装失败 → 自动退回开发者模式松散注册
    5) 每步打印实际 HRESULT 与结论；安装前清理同名包的旧注册

.PARAMETER NoElevate
    不触发自提升，直接走非提权分支（脚本自检用）

.PARAMETER Elevated
    内部参数：标记当前进程已是提权后的子进程，防止重复自提升

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$Msix = "",
    [string]$Cer = "",
    [string]$LayoutZip = "",
    [string]$InstallDir = "",
    [switch]$SkipUninstall,
    [switch]$NoElevate,
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if (-not $Msix) { $Msix = Join-Path $root "WinUI2Demo_1.0.0.0_x64.msix" }
if (-not $Cer) { $Cer = Join-Path $root "WinUI2DemoDev.cer" }
if (-not $LayoutZip) { $LayoutZip = Join-Path $root "WinUI2Demo-layout.zip" }
# 松散注册要求注册目录长期存在（删掉目录包就坏了），所以放 LocalAppData 而不是临时目录
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA "WinUI2Demo\layout" }

$script:pkgName = "WinUI2Demo"
$script:isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:summary = @()

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Fail([string]$Text) { Write-Host "FAIL  $Text" -ForegroundColor Red }
function Write-Note([string]$Text) { Write-Host "      $Text" -ForegroundColor DarkGray }

function Get-HResult([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    $ex = $ErrorRecord.Exception
    while ($ex.InnerException) { $ex = $ex.InnerException }
    if ($ex.HResult -ne 0) { return ("0x{0:X8}" -f $ex.HResult) }
    return "-"
}

function Get-ErrorLine([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    return ($ErrorRecord.Exception.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
}

function Get-InstalledPackage {
    return Get-AppxPackage -Name $script:pkgName -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Test-Installed([string]$Where) {
    $pkg = Get-InstalledPackage
    if (-not $pkg) { throw "$Where 后仍查不到已安装的 $script:pkgName" }
    Write-Host "      已安装：$($pkg.PackageFullName)"
    Write-Host "      安装位置：$($pkg.InstallLocation)"
    return $true
}

function Remove-InstalledPackages {
    $seen = @{}
    $removed = 0
    foreach ($pkg in @(Get-AppxPackage -Name $script:pkgName -ErrorAction SilentlyContinue)) {
        if ($seen.ContainsKey($pkg.PackageFullName)) { continue }
        $seen[$pkg.PackageFullName] = $true
        Write-Host "      移除（当前用户）：$($pkg.PackageFullName)"
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
        $removed++
    }
    if ($script:isAdmin) {
        foreach ($pkg in @(Get-AppxPackage -Name $script:pkgName -AllUsers -ErrorAction SilentlyContinue)) {
            if ($seen.ContainsKey($pkg.PackageFullName)) { continue }
            $seen[$pkg.PackageFullName] = $true
            Write-Host "      移除（所有用户）：$($pkg.PackageFullName)"
            Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop
            $removed++
        }
    }
    if ($removed -eq 0) { Write-Host "      没有需要清理的旧注册" }
    else { Write-Ok "已清理 $removed 个旧注册" }
}

function Get-PackageSignerThumbprint {
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
        Add-Type -AssemblyName System.Security | Out-Null
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Msix)
        try {
            $entry = $zip.GetEntry("AppxSignature.p7x")
            if (-not $entry) { return $null }
            $ms = New-Object System.IO.MemoryStream
            $entry.Open().CopyTo($ms)
            [byte[]]$raw = $ms.ToArray()
            # AppxSignature.p7x = "PKCX" 头 + 偏移 4 起的 PKCS#7 SignedData
            $off = 0
            if ($raw.Length -gt 8 -and $raw[0] -ne 0x30) {
                for ($i = 0; $i -lt [Math]::Min(64, $raw.Length - 1); $i++) {
                    if ($raw[$i] -eq 0x30 -and $raw[$i + 1] -eq 0x82) { $off = $i; break }
                }
            }
            [byte[]]$blob = $raw[$off..($raw.Length - 1)]
            $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
            $cms.Decode($blob)
            if ($cms.SignerInfos.Count -eq 0) { return $null }
            return $cms.SignerInfos[0].Certificate.Thumbprint
        }
        finally { $zip.Dispose() }
    }
    catch { return $null }
}

function Test-CertInStore([string]$StorePath, [string]$Thumbprint) {
    if (-not $Thumbprint) { return $false }
    return Test-Path (Join-Path $StorePath $Thumbprint)
}

function Get-MachineTrustState([string]$Thumbprint) {
    $tp = Test-CertInStore "Cert:\LocalMachine\TrustedPeople" $Thumbprint
    $rt = Test-CertInStore "Cert:\LocalMachine\Root" $Thumbprint
    return @{ TrustedPeople = $tp; Root = $rt; Complete = ($tp -and $rt) }
}

function Write-Summary {
    Write-Step "结果汇总"
    foreach ($line in $script:summary) { Write-Host "      $line" }
}

function Complete([string]$Path) {
    $script:summary += "采用路径：$Path"
    Write-Summary
    $pkg = Get-InstalledPackage
    if ($pkg) {
        Write-Host "      PackageFullName：$($pkg.PackageFullName)"
        Write-Host "      AUMID：$($pkg.PackageFamilyName)!App"
    }
    exit 0
}

# ---------- 1) 环境与包校验 ----------
Write-Step "1/5 环境与包校验"
if (-not (Test-Path $Msix)) { Write-Fail "找不到安装包：$Msix"; exit 1 }
Write-Host "      包：$Msix（$((Get-Item $Msix).Length) 字节）"

$unlock = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue
$devMode = ($unlock.AllowDevelopmentWithoutDevLicense -eq 1)
Write-Host "      开发者模式：$devMode；当前会话管理员：$($script:isAdmin)"

$cerThumb = $null
if (Test-Path $Cer) {
    $cerObj = Get-PfxCertificate $Cer
    $cerThumb = $cerObj.Thumbprint
    Write-Host "      证书：$($cerObj.Subject)  指纹=$cerThumb  有效期至=$($cerObj.NotAfter.ToString('yyyy-MM-dd'))"
}
else { Write-Host "      未找到证书文件：$Cer" }

$signerThumb = Get-PackageSignerThumbprint
Write-Host "      包签名者指纹：$signerThumb"

$certUsable = $false
if (-not $cerThumb) {
    $script:summary += "证书校验：SKIPPED（缺少 .cer）"
}
elseif (-not $signerThumb) {
    $script:summary += "证书校验：SKIPPED（包内读不到 AppxSignature.p7x，包可能未签名）"
}
elseif ($cerThumb -eq $signerThumb) {
    $certUsable = $true
    Write-Ok "证书与包签名者指纹一致（$cerThumb）"
    $script:summary += "证书校验：PASS（指纹与包内签名一致）"
}
else {
    Write-Fail "证书与包签名者不一致：cer=$cerThumb / 包内=$signerThumb"
    Write-Note "不会导入这张证书，签名安装路径跳过，改走松散注册"
    $script:summary += "证书校验：FAIL（cer 与包内签名者不一致，已跳过签名路径）"
}

# ---------- 自提升 ----------
if (-not $script:isAdmin -and -not $NoElevate -and -not $Elevated) {
    Write-Step "请求管理员权限（导入机器级信任存储必需）"
    $hostExe = (Get-Process -Id $PID).Path
    $argStr = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Msix "{1}" -Cer "{2}" -LayoutZip "{3}" -InstallDir "{4}" -Elevated' -f `
        $PSCommandPath, $Msix, $Cer, $LayoutZip, $InstallDir
    if ($SkipUninstall) { $argStr += ' -SkipUninstall' }
    Write-Note "若在 UAC 弹窗点“否”，脚本会继续走非提权路径（松散注册）"
    try {
        $child = Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $argStr -Wait -PassThru
        Write-Ok "提权子进程已结束，退出码 $($child.ExitCode)"
        $script:summary += "提权：PASS（子进程退出码 $($child.ExitCode)）"
        Write-Summary
        exit $child.ExitCode
    }
    catch {
        $code = Get-HResult $_
        Write-Fail "提权被拒绝或失败：$code $(Get-ErrorLine $_)"
        Write-Note "继续走非提权路径：跳过证书导入，直接尝试松散注册"
        $script:summary += "提权：FAIL $code（已退回非提权路径）"
    }
}
elseif ($script:isAdmin) {
    Write-Host "`n      已在管理员上下文，无需自提升"
}

# ---------- 2) 清理同名旧注册 ----------
Write-Step "2/5 清理同名旧注册"
if ($SkipUninstall) {
    Write-Note "已指定 -SkipUninstall，跳过清理"
    $script:summary += "旧注册清理：SKIPPED（-SkipUninstall）"
}
else {
    try {
        Remove-InstalledPackages
        $left = @(Get-AppxPackage -Name $script:pkgName -ErrorAction SilentlyContinue)
        if ($left.Count -gt 0) { throw "清理后仍有 $($left.Count) 个同名注册" }
    }
    catch {
        $code = Get-HResult $_
        Write-Fail "$code $(Get-ErrorLine $_)"
        $script:summary += "旧注册清理：FAIL $code"
        Write-Note "旧注册未清理干净时新装可能报 0x80073CF9/0x80073D06"
    }
}

# ---------- 3) 证书导入 + 签名安装 ----------
$signedInstalled = $false
$skipReason = $null

if (-not $certUsable) {
    $skipReason = "证书不可用（缺失或与包签名者不一致），不导入错误证书"
}
elseif (-not $script:isAdmin) {
    $trust = Get-MachineTrustState $cerThumb
    if ($trust.Complete) {
        Write-Step "3/5 签名安装（证书已在机器级信任存储，无需导入）"
        try {
            Add-AppxPackage -Path $Msix -ForceApplicationShutdown -ErrorAction Stop
            Test-Installed "Add-AppxPackage" | Out-Null
            $signedInstalled = $true
            Write-Ok "签名安装成功"
            $script:summary += "签名安装：PASS（证书已在 LocalMachine 信任存储）"
        }
        catch {
            $code = Get-HResult $_
            Write-Fail "$code $(Get-ErrorLine $_)"
            $script:summary += "签名安装：FAIL $code"
            $skipReason = "签名安装失败"
        }
    }
    else {
        $skipReason = "当前非管理员，证书未进机器级信任存储（TrustedPeople=$($trust.TrustedPeople) Root=$($trust.Root)），导入需要管理员"
    }
}
else {
    Write-Step "3/5 导入证书到机器级信任存储 + 签名安装"
    try {
        Import-Certificate -FilePath $Cer -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
        Import-Certificate -FilePath $Cer -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
        $trust = Get-MachineTrustState $cerThumb
        if (-not $trust.TrustedPeople) { throw "LocalMachine\TrustedPeople 导入后校验失败" }
        if (-not $trust.Root) { throw "LocalMachine\Root 导入后校验失败" }
        Write-Ok "证书已进入 LocalMachine\TrustedPeople 与 LocalMachine\Root（$cerThumb）"
        $script:summary += "证书导入：PASS（TrustedPeople + Root）"
    }
    catch {
        $code = Get-HResult $_
        Write-Fail "$code $(Get-ErrorLine $_)"
        $script:summary += "证书导入：FAIL $code"
        $skipReason = "证书导入失败"
    }

    if (-not $skipReason) {
        try {
            Add-AppxPackage -Path $Msix -ForceApplicationShutdown -ErrorAction Stop
            Test-Installed "Add-AppxPackage" | Out-Null
            $signedInstalled = $true
            Write-Ok "签名安装成功"
            $script:summary += "签名安装：PASS"
        }
        catch {
            $code = Get-HResult $_
            Write-Fail "$code $(Get-ErrorLine $_)"
            Write-Note "0x800B010A=发布者证书链不受信任；0x800B0109=根证书不受信任。两者都指向证书信任，不是包损坏"
            $script:summary += "签名安装：FAIL $code"
            $skipReason = "签名安装失败"
        }
    }
}

if ($signedInstalled) { Complete "签名安装（提权 + 机器级证书信任）" }

Write-Step "3/5 签名安装结果"
if ($skipReason) { Write-Note "跳过/失败原因：$skipReason" }
Write-Note "继续尝试松散注册"

# ---------- 4) 松散注册（兜底） ----------
Write-Step "4/5 松散注册（开发者模式）"
if (-not $devMode) {
    Write-Note "开发者模式未开启，本步预计失败：设置 → 系统 → 开发者选项 → 开发人员模式"
}
try {
    if (-not (Test-Path $LayoutZip)) { throw "找不到 $LayoutZip" }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Expand-Archive -Path $LayoutZip -DestinationPath $InstallDir -Force
    $manifest = Join-Path $InstallDir "AppxManifest.xml"
    if (-not (Test-Path $manifest)) { throw "解压后没有 AppxManifest.xml" }
    Write-Note "注册目录：$InstallDir（注册后必须保留，不能删）"
    Add-AppxPackage -Register $manifest -ForceApplicationShutdown -ErrorAction Stop
    Test-Installed "Add-AppxPackage -Register" | Out-Null
    Write-Ok "松散注册成功（不需要证书，依赖开发者模式）"
    $script:summary += "松散注册：PASS"
    Complete "松散注册（开发者模式，目录 $InstallDir）"
}
catch {
    $code = Get-HResult $_
    Write-Fail "$code $(Get-ErrorLine $_)"
    $script:summary += "松散注册：FAIL $code"
}

# ---------- 5) 失败汇总 ----------
Write-Step "5/5 安装失败"
Write-Summary
Write-Host "`n      处理建议：" -ForegroundColor Yellow
Write-Host "      1) 用管理员身份重跑本脚本（会自动导入机器级信任证书后签名安装）" -ForegroundColor Yellow
Write-Host "      2) 或开启开发者模式后重跑（走松散注册，不需要证书）" -ForegroundColor Yellow
Write-Host "      3) 仍有问题请附上上面的 HRESULT" -ForegroundColor Yellow
exit 1
