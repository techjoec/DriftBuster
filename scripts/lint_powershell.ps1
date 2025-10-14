#!/usr/bin/env pwsh
param(
    [string]$Path = "cli/DriftBuster.PowerShell"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$target = Resolve-Path (Join-Path $root $Path)

if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    throw "PSScriptAnalyzer is required. Install via 'Install-Module PSScriptAnalyzer'."
}

$results = Invoke-ScriptAnalyzer -Path $target -Recurse -Severity @('Error','Warning')

if ($results) {
    $results | Format-Table
    throw "PSScriptAnalyzer reported $($results.Count) issue(s)."
}

Write-Host "PSScriptAnalyzer: no issues found in $target" -ForegroundColor Green
