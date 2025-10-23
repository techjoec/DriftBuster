param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..' '..' 'artifacts' 'powershell' 'tests' 'pester-results.xml')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedOutput = (Resolve-Path -Path (Split-Path -Parent $OutputPath) -ErrorAction SilentlyContinue)
if (-not $resolvedOutput) {
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force
}

$scriptPath = Join-Path $PSScriptRoot 'DriftBuster.PowerShell.Tests.ps1'
$config = New-PesterConfiguration
$config.Run.Path = $scriptPath
$config.Run.Exit = $true
$config.TestResult.Enabled = $true
$config.TestResult.OutputFormat = 'NUnitXml'
$config.TestResult.OutputPath = $OutputPath

Invoke-Pester -Configuration $config
