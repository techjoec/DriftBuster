#!/usr/bin/env pwsh
param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts/powershell/releases",
    [switch]$SkipAnalyzer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$versionsPath = Join-Path $root 'versions.json'

if (-not (Test-Path -LiteralPath $versionsPath)) {
    throw "versions.json not found at $versionsPath"
}

$versions = Get-Content -LiteralPath $versionsPath -Raw | ConvertFrom-Json

$moduleVersion = $versions.powershell
if (-not $moduleVersion) {
    throw "powershell version entry missing from versions.json"
}

$backendVersion = $versions.core
if (-not $backendVersion) {
    throw "core version entry missing from versions.json"
}

$moduleRoot = Join-Path $root 'cli/DriftBuster.PowerShell'
$manifestPath = Join-Path $moduleRoot 'DriftBuster.psd1'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Module manifest not found at $manifestPath"
}

$manifestData = Import-PowerShellDataFile -Path $manifestPath
$manifest = Test-ModuleManifest -Path $manifestPath

if ($manifestData.ModuleVersion.ToString() -ne $moduleVersion) {
    throw "ModuleVersion in manifest ($($manifestData.ModuleVersion)) does not match versions.json entry ($moduleVersion)."
}

$manifestBackendVersion = $manifestData.PrivateData.BackendVersion
if (-not $manifestBackendVersion) {
    throw "Module manifest missing PrivateData.BackendVersion"
}

if ($manifestBackendVersion -ne $backendVersion) {
    throw "Module manifest BackendVersion '$manifestBackendVersion' does not match core version '$backendVersion'."
}

if (-not $SkipAnalyzer) {
    if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
        throw "PSScriptAnalyzer is required. Install via 'Install-Module PSScriptAnalyzer'."
    }

    $analysisResults = Invoke-ScriptAnalyzer -Path $moduleRoot -Recurse -Severity @('Error','Warning')
    $analysisArray = @($analysisResults)
    if ($analysisArray.Count -gt 0) {
        $analysisArray | Format-Table
        throw "PSScriptAnalyzer reported $($analysisArray.Count) issue(s)."
    }
} else {
    Write-Warning "Skipping PSScriptAnalyzer validation by request."
}

$backendBin = Join-Path $root "gui/DriftBuster.Backend/bin/$Configuration/net8.0/DriftBuster.Backend.dll"
if (-not (Test-Path -LiteralPath $backendBin)) {
    throw "Backend assembly not found at $backendBin. Run 'dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c $Configuration' first."
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

try {
    Copy-Item -Path $moduleRoot -Destination $stagingRoot -Recurse -Force
    Copy-Item -Path $backendBin -Destination (Join-Path $stagingRoot 'DriftBuster.PowerShell') -Force

    if (-not (Test-Path -LiteralPath $OutputDirectory)) {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    }

    $outputPath = Resolve-Path $OutputDirectory
    $archiveName = "DriftBuster.PowerShell-$moduleVersion.zip"
    $archivePath = Join-Path $outputPath $archiveName

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    $sourcePath = Join-Path $stagingRoot 'DriftBuster.PowerShell'
    Compress-Archive -Path $sourcePath -DestinationPath $archivePath -Force

    $hash = Get-FileHash -Path $archivePath -Algorithm SHA256
    $checksumPath = Join-Path $outputPath "$archiveName.sha256"
    Set-Content -LiteralPath $checksumPath -Value "$($hash.Hash)  $archiveName"

    Write-Host "Packaged PowerShell module to $archivePath" -ForegroundColor Green
    Write-Host "SHA256 checksum saved to $checksumPath" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
