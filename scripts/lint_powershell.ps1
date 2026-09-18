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

# The offline runner carries the backend's default secret rules inline; keep the copy identical.
$runner = Get-Content -Raw (Join-Path $root 'scripts/driftbuster-offline-runner.ps1')
$embedded = [regex]::Match($runner, "function Get-DbEmbeddedSecretRuleText \{.*?return @'\r?\n(?<rules>.*?)\r?\n'@", 'Singleline').Groups['rules'].Value
$resource = (Get-Content -Raw (Join-Path $root 'gui/DriftBuster.Backend/Resources/secret_rules.json')).TrimEnd()
if (($embedded -replace "\r\n", "\n") -ne ($resource -replace "\r\n", "\n")) {
    throw "scripts/driftbuster-offline-runner.ps1 embeds secret rules that differ from gui/DriftBuster.Backend/Resources/secret_rules.json."
}

Write-Information "PSScriptAnalyzer: no issues found in $($targets -join ', ')" -InformationAction Continue
