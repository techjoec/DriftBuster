#!/usr/bin/env pwsh
param(
    [string[]]$Path = @("cli", "scripts")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$targets = $Path | ForEach-Object { Resolve-Path (Join-Path $root $_) }

if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    throw "PSScriptAnalyzer is required. Install via 'Install-Module PSScriptAnalyzer'."
}

$results = $targets | ForEach-Object { Invoke-ScriptAnalyzer -Path $_ -Recurse -Severity @('Error','Warning') }

if ($results) {
    $results | Format-Table
    throw "PSScriptAnalyzer reported $($results.Count) issue(s)."
}

Write-Information "PSScriptAnalyzer: no issues found in $($targets -join ', ')" -InformationAction Continue
