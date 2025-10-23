#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win10-x64",
    [string]$OutputDirectory = "artifacts/gui-packaging/msix",
    [string]$IntermediateDirectory = "artifacts/gui-packaging/msix/staging",
    [string]$Publisher = "CN=DriftBuster Developers",
    [string]$PackageName = "com.driftbuster.gui",
    [string]$DisplayName = "DriftBuster",
    [string]$Description = "DriftBuster headless GUI shell.",
    [string]$Version = "1.0.0.0",
    [string]$AssetsDirectory = "gui/DriftBuster.Gui/Assets/Msix",
    [switch]$SkipPublish,
    [switch]$SkipSigning,
    [string]$CertificatePath,
    [string]$CertificatePassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw "MSIX packaging requires Windows. Run this script from a Windows host."
}

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$projectPath = Join-Path $root 'gui/DriftBuster.Gui/DriftBuster.Gui.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "GUI project not found at $projectPath"
}

$runtimeNormalized = $Runtime.Trim()
if (-not $runtimeNormalized) {
    throw "Runtime must be provided (e.g. win10-x64, win10-arm64)."
}

$publishDirectory = Join-Path $root "artifacts/gui-packaging/publish-msix/$Configuration/$runtimeNormalized"

if (-not $SkipPublish) {
    Write-Host "Publishing DriftBuster GUI for $runtimeNormalized..." -ForegroundColor Cyan
    $publishArgs = @(
        'publish',
        $projectPath,
        '-c', $Configuration,
        '-r', $runtimeNormalized,
        '--self-contained', 'true',
        '/p:PublishSingleFile=false',
        '/p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $publishDirectory
    )

    & dotnet @publishArgs | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for runtime $runtimeNormalized"
    }
} else {
    if (-not (Test-Path -LiteralPath $publishDirectory)) {
        throw "Publish directory $publishDirectory does not exist. Remove -SkipPublish to build artifacts."
    }
}

if (-not (Test-Path -LiteralPath $publishDirectory)) {
    throw "Publish output missing at $publishDirectory"
}

$intermediateRoot = Join-Path $root $IntermediateDirectory
if (Test-Path -LiteralPath $intermediateRoot) {
    Remove-Item -LiteralPath $intermediateRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $intermediateRoot -Force | Out-Null

$appRoot = Join-Path $intermediateRoot 'App'
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null

Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $appRoot -Recurse -Force

$assetsSource = Join-Path $root $AssetsDirectory
if (-not (Test-Path -LiteralPath $assetsSource)) {
    throw "Expected MSIX assets under $assetsSource. Provide Square150x150Logo.png, Square44x44Logo.png, StoreLogo.png, and Wide310x150Logo.png."
}

$assetsDestination = Join-Path $intermediateRoot 'Assets'
Copy-Item -Path (Join-Path $assetsSource '*') -Destination $assetsDestination -Recurse -Force

$expectedAssets = @(
    'Square150x150Logo.png',
    'Square44x44Logo.png',
    'StoreLogo.png',
    'Wide310x150Logo.png'
)

foreach ($asset in $expectedAssets) {
    $assetPath = Join-Path $assetsDestination $asset
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "Missing required MSIX asset: $assetPath"
    }
}

$architecture = switch -Regex ($runtimeNormalized) {
    'arm64' { 'arm64'; break }
    'x86' { 'x86'; break }
    Default { 'x64' }
}

$manifestPath = Join-Path $intermediateRoot 'AppxManifest.xml'
$exeName = 'DriftBuster.Gui.exe'
$manifestContent = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="$PackageName"
            Publisher="$Publisher"
            Version="$Version"
            ProcessorArchitecture="$architecture" />
  <Properties>
    <DisplayName>$DisplayName</DisplayName>
    <PublisherDisplayName>$Publisher</PublisherDisplayName>
    <Description>$Description</Description>
    <Logo>Assets\\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="DriftBuster"
                 Executable="App\\$exeName"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="$DisplayName"
                          Square150x150Logo="Assets\\Square150x150Logo.png"
                          Square44x44Logo="Assets\\Square44x44Logo.png"
                          Description="$Description"
                          BackgroundColor="transparent">
        <uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>
"@

Set-Content -LiteralPath $manifestPath -Value $manifestContent -Encoding UTF8

$kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeappx = Get-ChildItem -Path $kitRoot -Filter 'makeappx.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $makeappx) {
    throw "makeappx.exe not found. Install the Windows 10 SDK (App Packaging tools)."
}

$outputRoot = Join-Path $root $OutputDirectory
if (-not (Test-Path -LiteralPath $outputRoot)) {
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
}

$packageFileName = "$PackageName-$Version-$architecture.msix"
$packagePath = Join-Path $outputRoot $packageFileName
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

Write-Host "Packing MSIX via $($makeappx.FullName)..." -ForegroundColor Cyan
$makeappxArgs = @('pack', '/d', $intermediateRoot, '/p', $packagePath, '/o')
& $makeappx.FullName @makeappxArgs | Write-Host
if ($LASTEXITCODE -ne 0) {
    throw "makeappx.exe failed to create MSIX package."
}

if (-not $SkipSigning) {
    if (-not $CertificatePath) {
        throw "CertificatePath is required unless -SkipSigning is specified."
    }

    $signtool = Get-ChildItem -Path $kitRoot -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $signtool) {
        throw "signtool.exe not found. Install the Windows 10 SDK (Signing tools)."
    }

    $signArgs = @('sign', '/fd', 'SHA256', '/f', $CertificatePath, '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
    if ($CertificatePassword) {
        $signArgs += @('/p', $CertificatePassword)
    }
    $signArgs += $packagePath

    Write-Host "Signing MSIX via $($signtool.FullName)..." -ForegroundColor Cyan
    & $signtool.FullName @signArgs | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed to sign the MSIX package."
    }
} else {
    Write-Warning "Skipping signing. The MSIX will require sideloading exemptions."
}

Write-Host "MSIX package created at $packagePath" -ForegroundColor Green
Write-Host "Intermediate staging preserved at $intermediateRoot" -ForegroundColor Yellow
