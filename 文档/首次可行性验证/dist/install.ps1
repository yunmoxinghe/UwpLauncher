<#
.SYNOPSIS
    安装 WinUI2Demo：仅支持签名 MSIX（自提升 + 机器级证书信任）。

.DESCRIPTION
    1) 校验 .cer 指纹与包内 AppxSignature.p7x 的签名者指纹一致（不一致拒绝导入）
    2) 未提权时用 Start-Process -Verb RunAs 自提升；子进程窗口隐藏，结果经临时文件回传
    3) 清理同名旧注册（当前用户，提权时含 -AllUsers）
    4) 把证书导入 LocalMachine\TrustedPeople 与 LocalMachine\Root 并校验
    5) Add-AppxPackage 签名安装，校验安装位置在 WindowsApps 下且 SignatureKind 不是 None

    任一步失败都会明确报错、打印 HRESULT 与人工修复命令，并返回非 0 退出码。
    脚本不再提供松散注册（layout）降级路径。

.PARAMETER NoElevate
    脚本自检用：不触发自提升，用于验证「非管理员时报错并给出修复步骤」的行为

.PARAMETER Elevated
    内部参数：标记当前进程是提权后的子进程，防止重复自提升

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$Msix = "",
    [string]$Cer = "",
    [switch]$SkipUninstall,
    [switch]$NoElevate,
    [switch]$Elevated,
    # 内部参数：提权子进程把结果汇总写到该文件，父进程读回打印
    [string]$ResultPath = ""
)

$ErrorActionPreference = "Stop"
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if (-not $Msix) { $Msix = Join-Path $root "WinUI2Demo_1.0.0.0_x64.msix" }
if (-not $Cer) { $Cer = Join-Path $root "WinUI2DemoDev.cer" }

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

function Get-AllRegistrations {
    $list = @(Get-AppxPackage -Name $script:pkgName -ErrorAction SilentlyContinue)
    if ($script:isAdmin) {
        $list += @(Get-AppxPackage -Name $script:pkgName -AllUsers -ErrorAction SilentlyContinue)
    }
    return @($list | Sort-Object PackageFullName -Unique)
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

function Save-Result([int]$Code) {
    if (-not $ResultPath) { return }
    $lines = @("ExitCode=$Code")
    foreach ($line in $script:summary) { $lines += $line }
    $pkg = Get-InstalledPackage
    if ($pkg) {
        $lines += "PackageFullName=$($pkg.PackageFullName)"
        $lines += "InstallLocation=$($pkg.InstallLocation)"
        $lines += "SignatureKind=$($pkg.SignatureKind)"
    }
    if ($Code -ne 0) {
        $lines += "人工修复步骤（管理员 PowerShell）："
        $lines += "Import-Certificate -FilePath `"$Cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
        $lines += "Import-Certificate -FilePath `"$Cer`" -CertStoreLocation Cert:\LocalMachine\Root"
        $lines += "Add-AppxPackage -Path `"$Msix`" -ForceApplicationShutdown"
        $lines += "0x800B010A/0x800B0109=证书链不受信任；0x80073CF3=缺 Microsoft.UI.Xaml.2.8；0x80073CF9/0x80073D06=同名包未清理"
    }
    try { Set-Content -Path $ResultPath -Value $lines -Encoding UTF8 } catch { }
}

function Write-ManualSteps {
    Write-Host "`n      人工修复步骤（以管理员身份打开 PowerShell 后执行）：" -ForegroundColor Yellow
    Write-Host "      1) 导入证书到两处机器级存储（自签名证书的根就是它自己，两处都要）：" -ForegroundColor Yellow
    Write-Host "         Import-Certificate -FilePath `"$Cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople" -ForegroundColor Yellow
    Write-Host "         Import-Certificate -FilePath `"$Cer`" -CertStoreLocation Cert:\LocalMachine\Root" -ForegroundColor Yellow
    Write-Host "      2) 确认两处都存在同一指纹：$(if ($script:cerThumb) { $script:cerThumb } else { '<见第 1 步输出的证书指纹>' })" -ForegroundColor Yellow
    Write-Host "      3) 签名安装：" -ForegroundColor Yellow
    Write-Host "         Add-AppxPackage -Path `"$Msix`" -ForceApplicationShutdown" -ForegroundColor Yellow
    Write-Host "      错误码对照：" -ForegroundColor Yellow
    Write-Host "        0x800B010A / 0x800B0109 → 证书链不受信任，检查第 1 步两处是否都导入成功" -ForegroundColor Yellow
    Write-Host "        0x80073CF3 → 缺少 Microsoft.UI.Xaml.2.8 框架包，先在商店/系统中补装" -ForegroundColor Yellow
    Write-Host "        0x80073D06 / 0x80073CF9 → 同名包未清理干净，先 Get-AppxPackage $($script:pkgName) | Remove-AppxPackage" -ForegroundColor Yellow
    Write-Host "      4) 不要使用 layout.zip 松散注册；本项目只支持签名 MSIX 安装" -ForegroundColor Yellow
}

function Fail-Install([string]$Reason, [string]$Code = "-") {
    if ($Code -and $Code -ne "-") { Write-Fail "$Code $Reason" }
    else { Write-Fail $Reason }
    $script:summary += "安装失败：$Code $Reason"
    Write-Summary
    Save-Result 1
    Write-ManualSteps
    exit 1
}

function Assert-SignedInstall {
    $pkg = Get-InstalledPackage
    if (-not $pkg) { return "安装后仍查不到 $script:pkgName" }
    Write-Host "      PackageFullName：$($pkg.PackageFullName)"
    Write-Host "      InstallLocation：$($pkg.InstallLocation)"
    Write-Host "      SignatureKind：$($pkg.SignatureKind)"
    if ($pkg.SignatureKind -eq "None") {
        return "检测到松散注册（SignatureKind=None），不是签名安装"
    }
    if ($pkg.InstallLocation -notlike "*\WindowsApps\*") {
        return "安装位置不在 WindowsApps 下：$($pkg.InstallLocation)"
    }
    return $null
}

function Complete {
    $script:summary += "采用路径：签名 MSIX 安装"
    Write-Summary
    $pkg = Get-InstalledPackage
    if ($pkg) {
        Write-Host "      PackageFullName：$($pkg.PackageFullName)"
        Write-Host "      SignatureKind：$($pkg.SignatureKind)"
        Write-Host "      InstallLocation：$($pkg.InstallLocation)"
        Write-Host "      AUMID：$($pkg.PackageFamilyName)!App"
    }
    Save-Result 0
    exit 0
}

# ---------- 1) 环境与包校验 ----------
Write-Step "1/5 环境与包校验"
if (-not (Test-Path $Msix)) { Write-Fail "找不到安装包：$Msix"; exit 1 }
Write-Host "      包：$Msix（$((Get-Item $Msix).Length) 字节）"
Write-Host "      当前会话管理员：$($script:isAdmin)"

# 包清单声明了 Microsoft.UI.Xaml.2.8 框架包依赖，缺它时 Add-AppxPackage 会报 0x80073CF3
$framework = Get-AppxPackage -Name "Microsoft.UI.Xaml.2.8" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($framework) { Write-Host "      WinUI 2.8 框架包：$($framework.PackageFullName)" }
else { Write-Host "      WinUI 2.8 框架包：未安装（安装会报 0x80073CF3，需先补装）" }

$script:cerThumb = $null
if (-not (Test-Path $Cer)) { Fail-Install "找不到证书文件：$Cer" }
$cerObj = Get-PfxCertificate $Cer
$script:cerThumb = $cerObj.Thumbprint
Write-Host "      证书：$($cerObj.Subject)  指纹=$($script:cerThumb)  有效期至=$($cerObj.NotAfter.ToString('yyyy-MM-dd'))"

$signerThumb = Get-PackageSignerThumbprint
Write-Host "      包签名者指纹：$signerThumb"
if (-not $signerThumb) { Fail-Install "包内读不到 AppxSignature.p7x（包未签名或已损坏）" }
if ($signerThumb -ne $script:cerThumb) {
    Fail-Install "证书与包签名者不一致（cer=$($script:cerThumb) / 包内=$signerThumb），拒绝导入错误证书"
}
Write-Ok "证书与包签名者指纹一致（$($script:cerThumb)）"
$script:summary += "证书校验：PASS（指纹与包内签名一致）"

# ---------- 2) 自提升 ----------
Write-Step "2/5 管理员权限"
if (-not $script:isAdmin) {
    if ($NoElevate -or $Elevated) {
        Fail-Install "当前不是管理员，且已指定 -NoElevate；签名安装需要管理员权限"
    }
    $hostExe = (Get-Process -Id $PID).Path
    $resultPath = Join-Path $env:TEMP ("WinUI2Demo-install-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $argStr = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Msix "{1}" -Cer "{2}" -Elevated -ResultPath "{3}"' -f `
        $PSCommandPath, $Msix, $Cer, $resultPath
    if ($SkipUninstall) { $argStr += ' -SkipUninstall' }
    Write-Note "将请求管理员权限（写入机器级证书信任存储必需）"
    try {
        # 子进程窗口隐藏，结果通过 -ResultPath 回传，避免控制台一闪而过看不到 HRESULT
        $child = Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $argStr -Wait -PassThru -WindowStyle Hidden
        Write-Ok "提权子进程已结束，退出码 $($child.ExitCode)"
        if (Test-Path $resultPath) {
            Write-Step "提权进程执行结果"
            Get-Content $resultPath -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
            Remove-Item $resultPath -Force -ErrorAction SilentlyContinue
        }
        else {
            Write-Note "提权子进程未生成结果文件（可能中途被关闭或未启动成功）"
        }
        exit $child.ExitCode
    }
    catch {
        $code = Get-HResult $_
        Fail-Install "提权被拒绝或失败：$code $(Get-ErrorLine $_)" $code
    }
}
else {
    Write-Ok "已在管理员上下文"
}

# ---------- 3) 清理同名旧注册 ----------
Write-Step "3/5 清理同名旧注册"
if ($SkipUninstall) {
    Write-Note "已指定 -SkipUninstall，跳过清理"
    $script:summary += "旧注册清理：SKIPPED（-SkipUninstall）"
}
else {
    $seen = @{}
    $removed = 0
    foreach ($pkg in @(Get-AppxPackage -Name $script:pkgName -ErrorAction SilentlyContinue)) {
        if ($seen.ContainsKey($pkg.PackageFullName)) { continue }
        $seen[$pkg.PackageFullName] = $true
        Write-Host "      移除（当前用户）：$($pkg.PackageFullName)（$($pkg.InstallLocation)）"
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
        $removed++
    }
    if ($script:isAdmin) {
        foreach ($pkg in @(Get-AppxPackage -Name $script:pkgName -AllUsers -ErrorAction SilentlyContinue)) {
            if ($seen.ContainsKey($pkg.PackageFullName)) { continue }
            $seen[$pkg.PackageFullName] = $true
            Write-Host "      移除（所有用户）：$($pkg.PackageFullName)（$($pkg.InstallLocation)）"
            Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop
            $removed++
        }
    }
    if ($removed -eq 0) { Write-Host "      没有需要清理的旧注册" }
    elseif (@(Get-AllRegistrations).Count -gt 0) {
        Fail-Install "清理后仍有 $((@(Get-AllRegistrations)).Count) 个同名注册"
    }
    else { Write-Ok "已清理 $removed 个旧注册" }
    $script:summary += "旧注册清理：PASS（移除 $removed 个）"
}

# ---------- 4) 证书双存储导入 ----------
Write-Step "4/5 证书导入机器级信任存储"
$trust = Get-MachineTrustState $script:cerThumb
if ($trust.Complete) {
    Write-Ok "证书已在 LocalMachine\TrustedPeople 与 LocalMachine\Root"
    $script:summary += "证书导入：SKIPPED（两处已存在）"
}
else {
    try {
        Import-Certificate -FilePath $Cer -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
        Import-Certificate -FilePath $Cer -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
        $trust = Get-MachineTrustState $script:cerThumb
        if (-not $trust.TrustedPeople) { throw "LocalMachine\TrustedPeople 导入后校验失败" }
        if (-not $trust.Root) { throw "LocalMachine\Root 导入后校验失败" }
        Write-Ok "证书已进入 LocalMachine\TrustedPeople 与 LocalMachine\Root（$($script:cerThumb)）"
        $script:summary += "证书导入：PASS（TrustedPeople + Root）"
    }
    catch {
        Fail-Install "证书导入失败：$(Get-ErrorLine $_)" (Get-HResult $_)
    }
}

# ---------- 5) 签名安装 ----------
Write-Step "5/5 签名安装（Add-AppxPackage）"
try {
    Add-AppxPackage -Path $Msix -ForceApplicationShutdown -ErrorAction Stop
}
catch {
    Fail-Install "Add-AppxPackage 失败：$(Get-ErrorLine $_)" (Get-HResult $_)
}
$verifyError = Assert-SignedInstall
if ($verifyError) { Fail-Install $verifyError }
Write-Ok "签名安装成功"
$script:summary += "签名安装：PASS（$((Get-InstalledPackage).SignatureKind)，$((Get-InstalledPackage).InstallLocation)）"

$leftover = @(Get-AllRegistrations | Where-Object { $_.InstallLocation -notlike "*\WindowsApps\*" })
if ($leftover.Count -gt 0) {
    Fail-Install "仍有 $($leftover.Count) 个非 WindowsApps 路径的同名注册：$($leftover.InstallLocation -join '; ')"
}
$script:summary += "残留松散注册：无"

Complete
