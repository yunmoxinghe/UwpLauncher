<#
.SYNOPSIS
    一键安装 WinUI2Demo（按 1→2→3 顺序自动尝试，任一成功即停）。

.DESCRIPTION
    1) Add-AppxPackage -AllowUnsigned（需要开发者模式；该参数只对"完全未签名"的包生效）
    2) 松散注册：解压 layout.zip 到固定目录 → Add-AppxPackage -Register AppxManifest.xml（需要开发者模式）
    3) 导入 dist 里的 .cer 到当前用户 TrustedPeople，再普通签名安装（非管理员下仍会 0x800B0109）

    每一步都输出 PASS/FAIL 与失败原因（含 HRESULT）。
    本机实测（非管理员、Windows 11、开发者模式开）：① FAIL 0x800B0109；② PASS；③ FAIL 0x800B0109。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$Msix = "",
    [string]$Cer = "",
    [string]$LayoutZip = "",
    [string]$InstallDir = "",
    [switch]$SkipUninstall
)

$ErrorActionPreference = "Stop"
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if (-not $Msix) { $Msix = Join-Path $root "WinUI2Demo_1.0.0.0_x64.msix" }
if (-not $Cer) { $Cer = Join-Path $root "WinUI2DemoDev.cer" }
if (-not $LayoutZip) { $LayoutZip = Join-Path $root "WinUI2Demo-layout.zip" }
# 松散注册要求注册目录长期存在（删掉目录包就坏了），所以放到 LocalAppData 而不是临时目录
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA "WinUI2Demo\layout" }

$pkgName = "WinUI2Demo"
$script:failedSteps = @()

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Fail([string]$Text) { Write-Host "FAIL  $Text" -ForegroundColor Red }
function Write-Note([string]$Text) { Write-Host "        $Text" -ForegroundColor DarkGray }

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
    return Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Test-Installed([string]$Where) {
    $pkg = Get-InstalledPackage
    if (-not $pkg) { throw "$Where 后仍查不到已安装的 $pkgName" }
    Write-Host "已安装：$($pkg.PackageFullName)"
    Write-Host "安装位置：$($pkg.InstallLocation)"
    Write-Host "AUMID：$($pkg.PackageFamilyName)!App"
    return $true
}

function Remove-Installed {
    foreach ($pkg in @(Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue)) {
        Write-Host "移除已安装版本：$($pkg.PackageFullName)"
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
    }
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
            # AppxSignature.p7x = "PKCX" 头 + 偏移 4 的 PKCS#7 SignedData
            $off = 4
            if ($raw.Length -gt 8 -and $raw[0] -ne 0x30) {
                for ($i = 0; $i -lt [Math]::Min(64, $raw.Length - 1); $i++) {
                    if ($raw[$i] -eq 0x30 -and $raw[$i + 1] -eq 0x82) { $off = $i; break }
                }
            }
            else { $off = 0 }
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

# ---------- 前置检查 ----------
Write-Step "环境与包检查"
if (-not (Test-Path $Msix)) { Write-Fail "找不到安装包：$Msix"; exit 1 }
Write-Host "包：$Msix"
Write-Host "    大小 $((Get-Item $Msix).Length) 字节"

$unlock = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue
$devMode = ($unlock.AllowDevelopmentWithoutDevLicense -eq 1)
$sideload = ($unlock.AllowAllTrustedApps -eq 1)
Write-Host "开发者模式：$devMode；允许受信任应用旁加载：$sideload"
if (-not $devMode) { Write-Note "未开启开发者模式：第 2 步会失败。设置 → 系统 → 开发者选项 → 开发人员模式" }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "当前会话管理员：$isAdmin"

$signerThumb = Get-PackageSignerThumbprint
$cerThumb = $null
if (Test-Path $Cer) {
    $cerObj = Get-PfxCertificate $Cer
    $cerThumb = $cerObj.Thumbprint
    Write-Host "证书：$($cerObj.Subject)  指纹=$cerThumb  有效期至=$($cerObj.NotAfter.ToString('yyyy-MM-dd'))"
}
else { Write-Host "未找到证书文件：$Cer" }
Write-Host "包签名者指纹：$signerThumb"
if ($signerThumb -and $cerThumb -and $signerThumb -ne $cerThumb) {
    Write-Host "警告：证书与包签名者不一致（签名的常见失败原因 0x800B0109）" -ForegroundColor Yellow
}

if (-not $SkipUninstall) { Remove-Installed }

# ---------- 1) AllowUnsigned ----------
Write-Step "1/3 Add-AppxPackage -AllowUnsigned"
Write-Note '-AllowUnsigned 只放行"完全未签名"的包；自签名包仍要过证书信任链'
try {
    Add-AppxPackage -Path $Msix -AllowUnsigned -ForceApplicationShutdown -ErrorAction Stop
    Test-Installed "Add-AppxPackage -AllowUnsigned" | Out-Null
    Write-Ok "未签名允许模式安装成功"
    exit 0
}
catch {
    $code = Get-HResult $_
    Write-Fail "$code  $(Get-ErrorLine $_)"
    $script:failedSteps += "1=$code"
}

# ---------- 2) 松散注册 ----------
Write-Step "2/3 松散注册（Add-AppxPackage -Register AppxManifest.xml）"
Write-Note "目标目录：$InstallDir（注册后必须保留，不能删）"
try {
    if (-not (Test-Path $LayoutZip)) { throw "找不到 $LayoutZip" }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Expand-Archive -Path $LayoutZip -DestinationPath $InstallDir -Force
    $manifest = Join-Path $InstallDir "AppxManifest.xml"
    if (-not (Test-Path $manifest)) { throw "解压后没有 AppxManifest.xml" }
    Add-AppxPackage -Register $manifest -ForceApplicationShutdown -ErrorAction Stop
    Test-Installed "Add-AppxPackage -Register" | Out-Null
    Write-Ok "松散注册成功（依赖开发者模式，不需要证书）"
    exit 0
}
catch {
    $code = Get-HResult $_
    Write-Fail "$code  $(Get-ErrorLine $_)"
    $script:failedSteps += "2=$code"
}

# ---------- 3) 导入证书后签名安装 ----------
Write-Step "3/3 导入证书到 CurrentUser\TrustedPeople + 签名安装"
Write-Note "AppXSvc 用机器级证书存储校验签名；非管理员写不进 LocalMachine，本步通常仍报 0x800B0109"
try {
    if (-not (Test-Path $Cer)) { throw "找不到证书：$Cer" }
    $already = Get-ChildItem Cert:\CurrentUser\TrustedPeople | Where-Object { $_.Thumbprint -eq $cerThumb }
    if ($already) { Write-Host "证书已在 TrustedPeople：$cerThumb" }
    else {
        Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
        Write-Host "已导入 CurrentUser\TrustedPeople：$cerThumb"
    }
    Add-AppxPackage -Path $Msix -ForceApplicationShutdown -ErrorAction Stop
    Test-Installed "Add-AppxPackage（已信任证书）" | Out-Null
    Write-Ok "签名安装成功"
    exit 0
}
catch {
    $code = Get-HResult $_
    Write-Fail "$code  $(Get-ErrorLine $_)"
    $script:failedSteps += "3=$code"
}

Write-Host "`n三条路径都失败：$($script:failedSteps -join '; ')" -ForegroundColor Red
Write-Host "排查顺序：1) 开发者模式是否开启（第 2 步依赖它）；2) 管理员会话导入 LocalMachine\TrustedPeople 后再试第 3 步。" -ForegroundColor Yellow
exit 1
