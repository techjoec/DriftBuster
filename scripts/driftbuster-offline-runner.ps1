<#
.SYNOPSIS
  Portable offline collector for DriftBuster profiles.

.DESCRIPTION
  Consumes a DriftBuster offline runner config (JSON) and produces a
  collection package containing collected files, a manifest, and run
  logs.  The script intentionally avoids external dependencies so it can
  be shipped alongside the GUI in fully air-gapped environments.

.USAGE
  PS> .\driftbuster-offline-runner.ps1 -ConfigPath .\config.json
  PS> .\driftbuster-offline-runner.ps1 -ConfigPath .\config.json -OutputDirectory C:\Collections

  The PowerShell tests live in Python (see driftbuster/offline_runner.py)
  so we can validate behaviour without a Windows runner.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [Parameter()]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

function Get-DbTimestamp {
    (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
}

function Get-DbIsoTimestamp {
    (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

function Resolve-DbPath {
    param([string]$PathText)
    $expanded = [System.Environment]::ExpandEnvironmentVariables($PathText)
    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $expanded
    }
}

function Get-DbSafeName {
    param([string]$Text)
    if (-not $Text) { return 'data' }
    $chars = @()
    foreach ($c in $Text.ToCharArray()) {
        if (($c -match '[A-Za-z0-9]') -or $c -in @('-', '_')) {
            $chars += $c
        }
        else {
            $chars += '-'
        }
    }
    $safe = -join $chars
    if ([string]::IsNullOrWhiteSpace($safe)) { return 'data' }
    return $safe
}

function Test-DbHasWildcard {
    param([string]$Text)
    foreach ($token in @('*', '?', '[')) {
        if ($Text -and $Text.Contains($token)) { return $true }
    }
    return $false
}

function Get-DbGlobBase {
    param([string]$Pattern)
    $expanded = [System.Environment]::ExpandEnvironmentVariables($Pattern)
    $normalised = $expanded -replace '/', '\\'
    $segments = $normalised -split '\\'
    $builder = New-Object System.Collections.Generic.List[string]
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment)) {
            if ($builder.Count -eq 0 -and $normalised.StartsWith('\\')) {
                $builder.Add('')
            }
            continue
        }
        if ($segment -match '[*?[]') { break }
        $builder.Add($segment)
    }

    if ($builder.Count -eq 0) { return (Get-Location).ProviderPath }

    $first = $builder[0]
    if ($first -match '^[A-Za-z]:$') {
        $remaining = if ($builder.Count -gt 1) { ($builder | Select-Object -Skip 1) -join '\\' } else { '' }
        $path = if ($remaining) { "$first\\$remaining" } else { "$first\\" }
    }
    elseif ($normalised.StartsWith('\\')) {
        $path = '\\' + (($builder | Where-Object { $_ }) -join '\\')
    }
    else {
        $path = ($builder -join '\\')
    }

    try {
        return [System.IO.Path]::GetFullPath($path)
    }
    catch {
        return $path
    }
}

function Get-DbMatches {
    param([string]$SourcePath)
    $expanded = [System.Environment]::ExpandEnvironmentVariables($SourcePath)
    try {
        $candidate = [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        $candidate = $expanded
    }

    if (Test-Path -LiteralPath $candidate) {
        return @((Resolve-Path -LiteralPath $candidate).ProviderPath)
    }

    if (-not (Test-DbHasWildcard $expanded)) {
        throw "Path does not exist: $SourcePath"
    }

    $baseDir = Get-DbGlobBase $expanded
    if (-not (Test-Path -LiteralPath $baseDir)) {
        return @()
    }

    $pattern = New-Object System.Management.Automation.WildcardPattern($expanded, 'IgnoreCase')
    $matches = New-Object System.Collections.Generic.List[string]
    Get-ChildItem -Path $baseDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $candidatePath = $_.FullName
        if ($pattern.IsMatch($candidatePath)) {
            $matches.Add($candidatePath)
        }
    }
    return $matches
}

function Get-DbRelativePath {
    param([string]$Base, [string]$Target)
    $basePath = (Resolve-Path -LiteralPath $Base).ProviderPath
    $targetPath = (Resolve-Path -LiteralPath $Target).ProviderPath

    if ($targetPath.StartsWith($basePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $targetPath.Substring($basePath.Length).TrimStart('\\', '/')
        if (-not $relative) { return '.' }
        return $relative.Replace('\\', '/')
    }

    return ([System.IO.Path]::GetFileName($targetPath))
}

function Test-DbExclude {
    param([string]$RelativePath, [string[]]$Patterns)
    if (-not $Patterns -or $Patterns.Count -eq 0) { return $false }
    foreach ($pattern in $Patterns) {
        if ([string]::IsNullOrEmpty($pattern)) { continue }
        if ([System.Management.Automation.WildcardPattern]::new($pattern, 'IgnoreCase').IsMatch($RelativePath) -or
            [System.Management.Automation.WildcardPattern]::new($pattern, 'IgnoreCase').IsMatch(([System.IO.Path]::GetFileName($RelativePath)))) {
            return $true
        }
    }
    return $false
}

$configFile = Get-Item -LiteralPath $ConfigPath -ErrorAction Stop
$configContent = Get-Content -Path $configFile.FullName -Raw -Encoding UTF8
$config = $configContent | ConvertFrom-Json -Depth 12

if (-not $config.profile) {
    throw 'Configuration file missing "profile" block.'
}

$runnerSettings = if ($config.runner) { $config.runner } elseif ($config.settings) { $config.settings } else { $null }
$compress = if ($runnerSettings -and $runnerSettings.compress -ne $null) { [bool]$runnerSettings.compress } else { $true }
$includeLogs = if ($runnerSettings -and $runnerSettings.include_logs -ne $null) { [bool]$runnerSettings.include_logs } else { $true }
$includeManifest = if ($runnerSettings -and $runnerSettings.include_manifest -ne $null) { [bool]$runnerSettings.include_manifest } else { $true }
$includeConfig = if ($runnerSettings -and $runnerSettings.include_config -ne $null) { [bool]$runnerSettings.include_config } else { $true }
$manifestName = if ($runnerSettings -and $runnerSettings.manifest_name) { [string]$runnerSettings.manifest_name } else { 'manifest.json' }
$logName = if ($runnerSettings -and $runnerSettings.log_name) { [string]$runnerSettings.log_name } else { 'runner.log' }
$dataDirName = if ($runnerSettings -and $runnerSettings.data_directory_name) { [string]$runnerSettings.data_directory_name } else { 'data' }
$logsDirName = if ($runnerSettings -and $runnerSettings.logs_directory_name) { [string]$runnerSettings.logs_directory_name } else { 'logs' }
$maxTotalBytes = $null
if ($runnerSettings -and $runnerSettings.max_total_bytes) {
    $maxTotalBytes = [int64]$runnerSettings.max_total_bytes
    if ($maxTotalBytes -le 0) { throw 'runner.max_total_bytes must be positive when supplied.' }
}

$timestamp = Get-DbTimestamp
$outputRoot = if ($OutputDirectory) { Resolve-DbPath $OutputDirectory }
elseif ($runnerSettings -and $runnerSettings.output_directory) { Resolve-DbPath $runnerSettings.output_directory }
else { (Get-Location).ProviderPath }

if (-not (Test-Path -LiteralPath $outputRoot)) {
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
}

$profileName = if ($config.profile.name) { [string]$config.profile.name } else { 'offline' }
$stagingName = "{0}-{1}" -f (Get-DbSafeName $profileName), $timestamp
$stagingDir = Join-Path -Path $outputRoot -ChildPath $stagingName
$dataRoot = Join-Path -Path $stagingDir -ChildPath $dataDirName
$logsRoot = Join-Path -Path $stagingDir -ChildPath $logsDirName

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null

$logEntries = New-Object System.Collections.Generic.List[string]
function Write-DbLog {
    param([string]$Message)
    $logEntries.Add("[{0}] {1}" -f (Get-DbIsoTimestamp), $Message)
}

$collectedFiles = New-Object System.Collections.Generic.List[pscustomobject]
$sourceSummaries = New-Object System.Collections.Generic.List[pscustomobject]
$totalBytes = [int64]0

Write-DbLog 'offline collection started'

$sources = @()
if ($config.profile.sources) {
    if ($config.profile.sources -is [System.Array]) {
        $sources = @($config.profile.sources)
    }
    else {
        $sources = @($config.profile.sources)
    }
}
else {
    throw 'Profile must define at least one source.'
}

for ($index = 0; $index -lt $sources.Count; $index++) {
    $entry = $sources[$index]
    if ($entry -is [string]) {
        $entry = [pscustomobject]@{ path = [string]$entry }
    }

    $sourcePath = [string]$entry.path
    if (-not $sourcePath) {
        throw "Source entry $index is missing a path."
    }

    $alias = if ($entry.alias) { Get-DbSafeName([string]$entry.alias) }
    else {
        $resolved = Resolve-DbPath $sourcePath
        $name = [System.IO.Path]::GetFileName($resolved)
        if (-not $name) { $name = [System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($resolved)) }
        if (-not $name) { $name = "source_{0:d2}" -f $index }
        Get-DbSafeName $name
    }

    $destinationRoot = Join-Path -Path $dataRoot -ChildPath $alias
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

    $optional = $false
    if ($entry.PSObject.Properties.Match('optional').Count -gt 0) {
        $optional = [bool]$entry.optional
    }

    $excludePatterns = @()
    if ($entry.PSObject.Properties.Match('exclude').Count -gt 0) {
        if ($entry.exclude -is [string]) {
            $excludePatterns = @([string]$entry.exclude)
        }
        else {
            $excludePatterns = @($entry.exclude | ForEach-Object { [string]$_ })
        }
    }

    try {
        $matches = @(Get-DbMatches $sourcePath)
    }
    catch {
        if ($optional) {
            Write-DbLog "optional source skipped: $sourcePath"
            $sourceSummaries.Add([pscustomobject]@{
                    path     = $sourcePath
                    alias    = $alias
                    optional = $true
                    matched  = @()
                    skipped  = $true
                    reason   = 'missing'
                    exclude  = $excludePatterns
                })
            continue
        }
        Write-DbLog "required source missing: $sourcePath"
        throw
    }

    if ($matches.Count -eq 0 -and $optional) {
        Write-DbLog "optional source skipped: $sourcePath"
        $sourceSummaries.Add([pscustomobject]@{
                path     = $sourcePath
                alias    = $alias
                optional = $true
                matched  = @()
                skipped  = $true
                reason   = 'missing'
                exclude  = $excludePatterns
            })
        continue
    }
    elseif ($matches.Count -eq 0) {
        Write-DbLog "required source missing: $sourcePath"
        throw "Path does not exist: $sourcePath"
    }

    $collected = New-Object System.Collections.Generic.List[string]
    foreach ($match in ($matches | Sort-Object)) {
        if (Test-Path -LiteralPath $match -PathType Container) {
            $filesInDirectory = Get-ChildItem -Path $match -File -Recurse | Sort-Object FullName
            foreach ($file in $filesInDirectory) {
                $relative = Get-DbRelativePath $match $file.FullName
                if (Test-DbExclude $relative $excludePatterns) {
                    Write-DbLog "excluded $($file.FullName) by pattern"
                    continue
                }
                $size = $file.Length
                if ($maxTotalBytes -and ($totalBytes + $size) -gt $maxTotalBytes) {
                    throw 'Collection exceeds configured max_total_bytes limit.'
                }
                $destination = Join-Path -Path $destinationRoot -ChildPath ($relative -replace '/', '\\')
                $destParent = Split-Path -Path $destination -Parent
                if (-not (Test-Path -LiteralPath $destParent)) { New-Item -ItemType Directory -Path $destParent -Force | Out-Null }
                Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
                $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash.ToLowerInvariant()
                $relativeToData = Get-DbRelativePath $dataRoot $destination
                $collectedFiles.Add([pscustomobject]@{
                        alias         = $alias
                        source        = $sourcePath
                        destination   = $destination
                        relative_path = $relativeToData
                        size          = $size
                        sha256        = $hash
                    })
                $collected.Add($relative)
                $totalBytes += $size
            }
        }
        elseif (Test-Path -LiteralPath $match -PathType Leaf) {
            $relative = [System.IO.Path]::GetFileName($match)
            if (Test-DbExclude $relative $excludePatterns) {
                Write-DbLog "excluded $match by pattern"
                continue
            }
            $size = (Get-Item -LiteralPath $match).Length
            if ($maxTotalBytes -and ($totalBytes + $size) -gt $maxTotalBytes) {
                throw 'Collection exceeds configured max_total_bytes limit.'
            }
            $destination = Join-Path -Path $destinationRoot -ChildPath $relative
            Copy-Item -LiteralPath $match -Destination $destination -Force
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash.ToLowerInvariant()
            $relativeToData = Get-DbRelativePath $dataRoot $destination
            $collectedFiles.Add([pscustomobject]@{
                    alias         = $alias
                    source        = $sourcePath
                    destination   = $destination
                    relative_path = $relativeToData
                    size          = $size
                    sha256        = $hash
                })
            $collected.Add($relative)
            $totalBytes += $size
        }
    }

    $sourceSummaries.Add([pscustomobject]@{
            path     = $sourcePath
            alias    = $alias
            optional = $optional
            matched  = $collected
            skipped  = $false
            exclude  = $excludePatterns
        })
    Write-DbLog "collected $($collected.Count) items from $sourcePath"
}

Write-DbLog 'offline collection finished'

$logPath = $null
if ($includeLogs) {
    $logPath = Join-Path -Path $logsRoot -ChildPath $logName
    if ($logEntries.Count -gt 0) {
        $logEntries | Set-Content -Path $logPath -Encoding UTF8
    }
    else {
        New-Item -ItemType File -Path $logPath -Force | Out-Null
    }
}

$manifestPath = $null
if ($includeManifest) {
    $manifest = [ordered]@{
        schema       = 'https://driftbuster.dev/offline-runner/manifest/v1'
        generated_at = Get-DbIsoTimestamp
        timestamp    = $timestamp
        host         = [ordered]@{
            computer_name = $env:COMPUTERNAME
            user          = $env:USERNAME
            platform      = [System.Environment]::OSVersion.VersionString
        }
        profile      = [ordered]@{
            name        = [string]$config.profile.name
            description = $config.profile.description
            baseline    = $config.profile.baseline
            tags        = @($config.profile.tags)
            options     = if ($config.profile.options) { $config.profile.options } else { @{} }
        }
        runner       = [ordered]@{
            version = if ($config.version) { [string]$config.version } else { '1' }
            schema  = if ($config.schema) { [string]$config.schema } else { 'https://driftbuster.dev/offline-runner/config/v1' }
        }
        sources      = $sourceSummaries
        files        = $collectedFiles | ForEach-Object {
            [ordered]@{
                alias         = $_.alias
                source        = $_.source
                relative_path = $_.relative_path
                size          = $_.size
                sha256        = $_.sha256
            }
        }
        metadata     = if ($config.metadata) { $config.metadata } else { @{} }
        package      = [ordered]@{
            staging_directory = $stagingDir
            data_directory    = $dataRoot
            logs_directory    = $logsRoot
            compressed        = $compress
        }
    }

    if (Test-Path -LiteralPath $configFile.FullName) {
        $configHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $configFile.FullName).Hash.ToLowerInvariant()
        $manifest.config = [ordered]@{
            path   = $configFile.FullName
            sha256 = $configHash
        }
    }

    $manifestPath = Join-Path -Path $stagingDir -ChildPath $manifestName
    ($manifest | ConvertTo-Json -Depth 8) | Set-Content -Path $manifestPath -Encoding UTF8
}

if ($includeConfig) {
    Copy-Item -LiteralPath $configFile.FullName -Destination (Join-Path -Path $stagingDir -ChildPath $configFile.Name) -Force
}

$packagePath = $null
if ($compress) {
    $packageName = if ($runnerSettings -and $runnerSettings.package_name) { [string]$runnerSettings.package_name } else { "{0}-{1}.zip" -f (Get-DbSafeName $profileName), $timestamp }
    if (-not $packageName.ToLowerInvariant().EndsWith('.zip')) {
        $packageName = "$packageName.zip"
    }
    $packagePath = Join-Path -Path $outputRoot -ChildPath $packageName
    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    Compress-Archive -Path (Join-Path -Path $stagingDir -ChildPath '*') -DestinationPath $packagePath -Force
    Write-DbLog "package created at $packagePath"
}
else {
    Write-DbLog "staging directory ready at $stagingDir"
}

if ($logPath -and -not (Test-Path -LiteralPath $logPath)) {
    $logEntries | Set-Content -Path $logPath -Encoding UTF8
}

Write-Output ([pscustomobject]@{
        StagingDirectory = $stagingDir
        PackagePath      = $packagePath
        ManifestPath     = $manifestPath
        LogPath          = $logPath
        FilesCollected   = $collectedFiles.Count
    })
