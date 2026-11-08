<#
.SYNOPSIS
  把 WinUI2Probe 打包成 MSIX（声明 Microsoft.UI.Xaml.2.8 框架包依赖），并安装/注册到本机。

.DESCRIPTION
  1. 构建 WinUI2Probe；
  2. 组装 MSIX 布局（exe + 依赖 + AppxManifest.xml + logo）；
  3. MakeAppx 打包，SignTool 用自签名证书签名；
  4. 依次尝试“签名安装”与“开发模式松散注册”（-Register，无需证书信任）。

.PARAMETER InstallMode
  auto（默认）：先试签名 MSIX 安装，失败则回退到松散注册
  signed：只允许签名 MSIX 安装，失败即报错
  loose：只做松散注册

.PARAMETER SkipBuild
  跳过 MSBuild，复用已有 bin 输出。
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$Platform = "x64",

    [switch]$SkipBuild,

    [ValidateSet("auto", "signed", "loose")]
    [string]$InstallMode = "auto",

    # 工程名（src\<ProjectName>\<ProjectName>.csproj）与包标识，默认沿用旧探针
    [string]$ProjectName = "WinUI2Probe",

    [string]$PackageName = "",

    # 证书主题必须与 Package.appxmanifest 的 Publisher 完全一致
    [string]$CertSubject = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

if (-not $PackageName) { $PackageName = $ProjectName }
if (-not $CertSubject) { $CertSubject = "CN=${PackageName}Dev" }

$src = Join-Path $root "src\$ProjectName"
$proj = Join-Path $src "$ProjectName.csproj"
$tfm = "net10.0-windows10.0.26100.0"
$out = Join-Path $src "bin\$Platform\$Configuration\$tfm"

$work = Join-Path $root "artifacts\msix\$ProjectName"
$layout = Join-Path $work "layout"
$msix = Join-Path $work "${PackageName}_1.0.0.0_x64.msix"
$pkgLog = Join-Path $work "package.log"
$statusFile = Join-Path $root "artifacts\$ProjectName-install-status.txt"
$certName = "${PackageName}Dev"

New-Item -ItemType Directory -Force -Path $work | Out-Null
Set-Content -Path $pkgLog -Value "" -Encoding UTF8

function Write-PkgLog([string]$message) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $message
    Write-Host $line
    Add-Content -Path $pkgLog -Value $line -Encoding UTF8
}

function Find-MSBuild {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($found) { return $found }
    }
    $fallback = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
    if (Test-Path $fallback) { return $fallback }
    throw "未找到 MSBuild"
}

function Find-SdkTool([string]$name) {
    $bin = "C:\Program Files (x86)\Windows Kits\10\bin"
    $tool = Get-ChildItem $bin -Recurse -Filter $name -File |
        Where-Object { $_.FullName -match "\\x64\\$([regex]::Escape($name))$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) { throw "未找到 $name（Windows SDK）" }
    return $tool.FullName
}

function New-Logo([string]$path, [int]$size, [string]$text) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(0x0F, 0x6C, 0xBD))
    $font = New-Object System.Drawing.Font("Segoe UI", ([Math]::Max(8.0, $size / 4.0)), [System.Drawing.FontStyle]::Bold)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $g.DrawString($text, $font, [System.Drawing.Brushes]::White, $rect, $fmt)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
}

Write-PkgLog "work=$work"

# 1) 构建
if (-not $SkipBuild) {
    $msbuild = Find-MSBuild
    Write-PkgLog "MSBuild=$msbuild"
    & $msbuild $proj /restore /p:Platform=$Platform /p:Configuration=$Configuration /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "构建失败，MSBuild exit=$LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $out "$ProjectName.exe"))) {
    throw "未找到构建输出 $out\$ProjectName.exe"
}

# 2) 组装布局
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force -Path $layout | Out-Null
Copy-Item (Join-Path $out "*") $layout -Recurse -Force
Copy-Item (Join-Path $src "Package.appxmanifest") (Join-Path $layout "AppxManifest.xml") -Force

$assets = Join-Path $layout "Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null
New-Logo (Join-Path $assets "StoreLogo.png") 50 "W2"
New-Logo (Join-Path $assets "Square150x150Logo.png") 150 "W2"
New-Logo (Join-Path $assets "Square44x44Logo.png") 44 "W2"

$layoutFiles = (Get-ChildItem $layout -Recurse -File).Count
Write-PkgLog "layout files=$layoutFiles"

# 3) 打包 + 签名
$makeappx = Find-SdkTool "makeappx.exe"
$signtool = Find-SdkTool "signtool.exe"

if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /o /d $layout /p $msix
if ($LASTEXITCODE -ne 0) { throw "makeappx 失败，exit=$LASTEXITCODE" }
Write-PkgLog "packed $(Split-Path -Leaf $msix) size=$((Get-Item $msix).Length)"

$subject = $CertSubject
# 复用同一张开发证书：每次构建都新建会导致"上次信任的证书对不上新包"（0x800B0109）。
# 有效期不足 30 天才重建。
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) {
    Write-PkgLog "no reusable cert for $subject, creating new"
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -FriendlyName "$PackageName MSIX dev" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
}
else {
    Write-PkgLog "reuse cert thumbprint=$($cert.Thumbprint) notAfter=$($cert.NotAfter.ToString('yyyy-MM-dd'))"
}
$pfx = Join-Path $work "$certName.pfx"
$cer = Join-Path $work "$certName.cer"
$pwPlain = $certName
$pw = ConvertTo-SecureString $pwPlain -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pw -Force | Out-Null
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
& $signtool sign /fd SHA256 /f $pfx /p $pwPlain $msix
if ($LASTEXITCODE -ne 0) { throw "signtool 失败，exit=$LASTEXITCODE" }
Write-PkgLog "signed with $subject thumbprint=$($cert.Thumbprint)"

# 4) 安装 / 注册
$pkgName = $PackageName
Get-AppxPackage -Name $pkgName | ForEach-Object {
    Write-PkgLog "移除已安装的 $($_.PackageFullName)"
    Remove-AppxPackage -Package $_.PackageFullName
}

$installModeUsed = $null
$signedError = $null

if ($InstallMode -ne "loose") {
    try {
        Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
        Add-AppxPackage -Path $msix -ForceApplicationShutdown
        $installModeUsed = "signed-msix"
    }
    catch {
        $signedError = $_.Exception.Message
        Write-PkgLog "签名 MSIX 安装失败：$signedError"
        if ($InstallMode -eq "signed") { throw }
    }
}

if (-not $installModeUsed) {
    Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml") -ForceApplicationShutdown
    $installModeUsed = "loose-register"
}

$pkg = Get-AppxPackage -Name $pkgName | Select-Object -First 1
if (-not $pkg) { throw "安装后仍未找到包 $pkgName" }

function Get-RepoRelative([string]$path) {
    return ($path.Substring($root.Length + 1) -replace '\\', '/')
}

$summary = @(
    "mode=$installModeUsed"
    "signedInstallError=$signedError"
    "packageFullName=$($pkg.PackageFullName)"
    "packageFamilyName=$($pkg.PackageFamilyName)"
    "aumid=$($pkg.PackageFamilyName)!App"
    "msix=$(Get-RepoRelative $msix)"
    "packageLog=$(Get-RepoRelative $pkgLog)"
) -join "`r`n"
Set-Content -Path $statusFile -Value $summary -Encoding UTF8
Write-PkgLog $summary

"install mode: $installModeUsed"
"aumid: $($pkg.PackageFamilyName)!App"
