$ErrorActionPreference = 'Stop'

$script:ModuleManifest = $null
$script:BackendVersion = $null
$script:BackendAssemblyPath = $null
$script:SerializerOptions = $null

function New-DriftBusterBackendMissingError {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]
        $SearchedPaths,

        [Parameter()]
        [System.Exception]
        $InnerException
    )

    $uniquePaths = $SearchedPaths | Where-Object { $_ } | Sort-Object -Unique
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('Unable to load DriftBuster.Backend.dll for the PowerShell module.') | Out-Null

    if ($uniquePaths) {
        $lines.Add('') | Out-Null
        $lines.Add('Searched locations:') | Out-Null
        foreach ($path in $uniquePaths) {
            $lines.Add("  - $path") | Out-Null
        }
    }

    $lines.Add('') | Out-Null
    $lines.Add('Recover by publishing the backend assembly:') | Out-Null
    $lines.Add('  dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Debug -o gui/DriftBuster.Backend/bin/Debug/published') | Out-Null
    $lines.Add('Then re-import the module or copy the resulting DriftBuster.Backend.dll next to DriftBuster.psm1.') | Out-Null

    $message = [string]::Join([Environment]::NewLine, $lines)

    if ($InnerException) {
        $exception = [System.IO.FileNotFoundException]::new($message, $InnerException)
    }
    else {
        $exception = [System.IO.FileNotFoundException]::new($message)
    }

    return [System.Management.Automation.ErrorRecord]::new(
        $exception,
        'DriftBusterBackendMissing',
        [System.Management.Automation.ErrorCategory]::ObjectNotFound,
        $null
    )
}

function Get-DriftBusterModuleManifest {
    if ($script:ModuleManifest) {
        return $script:ModuleManifest
    }

    $manifestPath = Join-Path $PSScriptRoot 'DriftBuster.psd1'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Module manifest not found at $manifestPath"
    }

    $script:ModuleManifest = Import-PowerShellDataFile -Path $manifestPath
    return $script:ModuleManifest
}

function Get-DriftBusterBackendVersion {
    if ($script:BackendVersion) {
        return $script:BackendVersion
    }

    $manifest = Get-DriftBusterModuleManifest
    $version = $manifest.PrivateData.BackendVersion
    if (-not $version) {
        throw "Module manifest missing PrivateData.BackendVersion"
    }

    $script:BackendVersion = [string]$version
    return $script:BackendVersion
}

function Get-DriftBusterBackendCacheDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $AssemblyPath,

        [Parameter()]
        [string]
        $Version
    )

    $resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
    $contextName = "DriftbusterPathsProbe_{0}" -f ([Guid]::NewGuid().ToString('N'))
    $context = [System.Runtime.Loader.AssemblyLoadContext]::new($contextName, $true)

    try {
        $assembly = $context.LoadFromAssemblyPath($resolvedAssembly)
        $pathsType = $assembly.GetType('DriftBuster.Backend.DriftbusterPaths', $false)
        if (-not $pathsType) {
            throw "Type 'DriftBuster.Backend.DriftbusterPaths' not found in backend assembly at $resolvedAssembly."
        }

        $method = $pathsType.GetMethod(
            'GetCacheDirectory',
            [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static
        )
        if (-not $method) {
            throw "Method DriftbusterPaths.GetCacheDirectory not available in backend assembly."
        }

        $segments = @('powershell', 'backend')
        if ($Version) {
            $segments += $Version
        }

        $cacheDirectory = $method.Invoke($null, @([string[]]$segments))
    }
    finally {
        $context.Unload()
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        [System.GC]::Collect()
    }

    if ([string]::IsNullOrWhiteSpace($cacheDirectory)) {
        throw "Backend cache directory resolution returned an empty path."
    }

    return $cacheDirectory
}

function Get-DriftBusterBackendAssembly {
    param()

    if ($script:BackendAssemblyPath) {
        return $script:BackendAssemblyPath
    }

    $backendVersion = Get-DriftBusterBackendVersion

    $candidatePaths = @()
    $searchedPaths = @()

    $packagedAssembly = Join-Path $PSScriptRoot 'DriftBuster.Backend.dll'
    $searchedPaths += $packagedAssembly
    if (Test-Path -LiteralPath $packagedAssembly) {
        $candidatePaths += (Resolve-Path -LiteralPath $packagedAssembly).Path
    }

    $devRoot = Join-Path $PSScriptRoot '..\..\gui\DriftBuster.Backend\bin'
    if (Test-Path -LiteralPath $devRoot) {
        $devCandidates = Get-ChildItem -LiteralPath $devRoot -Filter 'DriftBuster.Backend.dll' -Recurse -ErrorAction SilentlyContinue
        if ($devCandidates) {
            $candidatePaths += $devCandidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -ExpandProperty FullName
            $searchedPaths += $devCandidates | Select-Object -ExpandProperty FullName
        }
        else {
            $searchedPaths += $devRoot
        }
    }
    else {
        $searchedPaths += $devRoot
    }

    $candidatePaths = $candidatePaths | Where-Object { $_ } | Select-Object -Unique
    if (-not $candidatePaths) {
        throw (New-DriftBusterBackendMissingError -SearchedPaths $searchedPaths)
    }

    $selectedCandidate = $candidatePaths |
        Where-Object { Test-Path -LiteralPath $_ } |
        Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
        Select-Object -First 1

    if (-not $selectedCandidate) {
        throw (New-DriftBusterBackendMissingError -SearchedPaths $candidatePaths)
    }

    $resolvedCandidate = (Resolve-Path -LiteralPath $selectedCandidate).Path

    $cacheDirectory = Get-DriftBusterBackendCacheDirectory -AssemblyPath $resolvedCandidate -Version $backendVersion
    $cacheAssemblyPath = Join-Path $cacheDirectory 'DriftBuster.Backend.dll'

    if ($resolvedCandidate -ne $cacheAssemblyPath) {
        $shouldCopy = $true

        if (Test-Path -LiteralPath $cacheAssemblyPath) {
            try {
                $sourceAssemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($resolvedCandidate)
                $targetAssemblyName = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $cacheAssemblyPath).Path)

                if ($sourceAssemblyName.Version -eq $targetAssemblyName.Version) {
                    $sourceWrite = (Get-Item -LiteralPath $resolvedCandidate).LastWriteTimeUtc
                    $targetWrite = (Get-Item -LiteralPath $cacheAssemblyPath).LastWriteTimeUtc
                    if ($targetWrite -ge $sourceWrite) {
                        $shouldCopy = $false
                    }
                }
            }
            catch {
                $shouldCopy = $true
            }
        }

        if ($shouldCopy) {
            Copy-Item -LiteralPath $resolvedCandidate -Destination $cacheAssemblyPath -Force
        }
    }

    $script:BackendAssemblyPath = (Resolve-Path -LiteralPath $cacheAssemblyPath).Path
    return $script:BackendAssemblyPath
}

function Get-DriftBusterSerializerOptions {
    if ($script:SerializerOptions) {
        return $script:SerializerOptions
    }

    $options = [System.Text.Json.JsonSerializerOptions]::new()
    $options.DefaultIgnoreCondition = [System.Text.Json.Serialization.JsonIgnoreCondition]::WhenWritingNull
    $options.PropertyNameCaseInsensitive = $true
    $options.Converters.Add([System.Text.Json.Serialization.JsonStringEnumMemberConverter]::new())

    $script:SerializerOptions = $options
    return $script:SerializerOptions
}

function ConvertFrom-DriftBusterJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Json
    )

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return $null
    }

    return $Json | ConvertFrom-Json -Depth 64
}

function ConvertFrom-DriftBusterModel {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]
        $Model
    )

    if ($null -eq $Model) {
        return $null
    }

    $options = Get-DriftBusterSerializerOptions
    $modelType = $Model.GetType()
    $json = [System.Text.Json.JsonSerializer]::Serialize($Model, $modelType, $options)
    return ConvertFrom-DriftBusterJson -Json $json
}

function Get-DriftBusterPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]
        $Object,

        [Parameter(Mandatory = $true)]
        [string[]]
        $Names
    )

    foreach ($name in $Names) {
        if ($Object -is [hashtable]) {
            foreach ($key in $Object.Keys) {
                if ($null -ne $key -and $key.ToString().Equals($name, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $Object[$key]
                }
            }
        }
        elseif ($Object -is [System.Collections.IDictionary]) {
            foreach ($key in $Object.Keys) {
                if ($null -ne $key -and $key.ToString().Equals($name, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $Object[$key]
                }
            }
        }
        elseif ($Object -ne $null -and $Object.PSObject) {
            $member = $Object.PSObject.Properties |
                Where-Object { $_.Name.Equals($name, [System.StringComparison]::OrdinalIgnoreCase) } |
                Select-Object -First 1

            if ($member) {
                return $member.Value
            }
        }
    }

    return $null
}

function ConvertTo-DriftBusterRunProfileDefinition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, ValueFromPipeline = $true)]
        [object]
        $InputObject
    )

    process {
        if ($null -eq $InputObject) {
            throw 'Run profile input cannot be null.'
        }

        if ($InputObject -is [DriftBuster.Backend.Models.RunProfileDefinition]) {
            return $InputObject
        }

        if ($InputObject -is [string]) {
            $text = $InputObject
            if (Test-Path -LiteralPath $InputObject) {
                $text = Get-Content -LiteralPath $InputObject -Raw
            }

            if ([string]::IsNullOrWhiteSpace($text)) {
                throw 'Run profile content was empty.'
            }

            $parsed = ConvertFrom-DriftBusterJson -Json $text
            return ConvertTo-DriftBusterRunProfileDefinition -InputObject $parsed
        }

        $profile = [DriftBuster.Backend.Models.RunProfileDefinition]::new()

        $name = Get-DriftBusterPropertyValue -Object $InputObject -Names @('name', 'Name')
        if ($null -ne $name) {
            $profile.Name = [string]$name
        }

        $description = Get-DriftBusterPropertyValue -Object $InputObject -Names @('description', 'Description')
        if ($null -ne $description) {
            $profile.Description = [string]$description
        }

        $baseline = Get-DriftBusterPropertyValue -Object $InputObject -Names @('baseline', 'Baseline')
        if ($null -ne $baseline) {
            $profile.Baseline = [string]$baseline
        }

        $sources = Get-DriftBusterPropertyValue -Object $InputObject -Names @('sources', 'Sources')
        if ($null -ne $sources) {
            if ($sources -is [System.Collections.IEnumerable] -and -not ($sources -is [string])) {
                $profile.Sources = @($sources | ForEach-Object { [string]$_ })
            }
            else {
                $profile.Sources = @([string]$sources)
            }
        }

        $options = Get-DriftBusterPropertyValue -Object $InputObject -Names @('options', 'Options')
        if ($null -ne $options) {
            if ($options -is [System.Collections.IDictionary]) {
                foreach ($key in $options.Keys) {
                    $profile.Options[[string]$key] = [string]$options[$key]
                }
            }
            elseif ($options -ne $null -and $options.PSObject) {
                foreach ($property in $options.PSObject.Properties) {
                    $profile.Options[[string]$property.Name] = [string]$property.Value
                }
            }
        }

        $secretScanner = Get-DriftBusterPropertyValue -Object $InputObject -Names @('secret_scanner', 'SecretScanner')
        if ($null -ne $secretScanner) {
            $ignoreRules = Get-DriftBusterPropertyValue -Object $secretScanner -Names @('ignore_rules', 'IgnoreRules')
            if ($ignoreRules) {
                if ($ignoreRules -is [System.Collections.IEnumerable] -and -not ($ignoreRules -is [string])) {
                    $profile.SecretScanner.IgnoreRules = @($ignoreRules | ForEach-Object { [string]$_ })
                }
                else {
                    $profile.SecretScanner.IgnoreRules = @([string]$ignoreRules)
                }
            }

            $ignorePatterns = Get-DriftBusterPropertyValue -Object $secretScanner -Names @('ignore_patterns', 'IgnorePatterns')
            if ($ignorePatterns) {
                if ($ignorePatterns -is [System.Collections.IEnumerable] -and -not ($ignorePatterns -is [string])) {
                    $profile.SecretScanner.IgnorePatterns = @($ignorePatterns | ForEach-Object { [string]$_ })
                }
                else {
                    $profile.SecretScanner.IgnorePatterns = @([string]$ignorePatterns)
                }
            }
        }

        return $profile
    }
}

if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'DriftBuster.Backend' })) {
    $assemblyPath = Get-DriftBusterBackendAssembly
    Add-Type -Path $assemblyPath
}

if (-not $script:DriftBusterBackend) {
    $script:DriftBusterBackend = [DriftBuster.Backend.DriftbusterBackend]::new()
}

function Test-DriftBusterPing {
<#
.SYNOPSIS
Verifies connectivity to the DriftBuster backend.

.DESCRIPTION
Invokes the backend `PingAsync` method and returns a status payload so
callers can confirm that the PowerShell module is wired correctly.

.EXAMPLE
Test-DriftBusterPing
#>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()

    $response = $script:DriftBusterBackend.PingAsync().GetAwaiter().GetResult()
    [pscustomobject]@{
        status = $response
    }
}

function Invoke-DriftBusterDiff {
<#
.SYNOPSIS
Compares configuration versions using the DriftBuster backend.

.DESCRIPTION
Wraps the backend `DiffAsync` API, accepting either an ordered collection of
versions or an explicit left/right pair. Results are emitted as PowerShell
objects that align with the backend JSON contract.

.PARAMETER Versions
Ordered list of version paths to diff. Accepts pipeline input.

.PARAMETER Left
Left-hand file or directory to diff.

.PARAMETER Right
Right-hand file or directory to diff.

.PARAMETER RawJson
Returns the raw JSON payload instead of converting to a PowerShell object.

.EXAMPLE
Invoke-DriftBusterDiff -Versions 'baseline/appsettings.json','release/appsettings.json'
#>
    [CmdletBinding(DefaultParameterSetName = 'Versions')]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Versions', ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true)]
        [Alias('Version')]
        [ValidateNotNullOrEmpty()]
        [string[]]
        $Versions,

        [Parameter(Mandatory = $true, ParameterSetName = 'Pair')]
        [ValidateNotNullOrEmpty()]
        [string]
        $Left,

        [Parameter(Mandatory = $true, ParameterSetName = 'Pair')]
        [ValidateNotNullOrEmpty()]
        [string]
        $Right,

        [Parameter()]
        [switch]
        $RawJson
    )

    begin {
        $collected = New-Object System.Collections.Generic.List[string]
    }

    process {
        if ($PSCmdlet.ParameterSetName -eq 'Versions') {
            if ($PSBoundParameters.ContainsKey('Versions')) {
                foreach ($version in @($Versions)) {
                    if ($version) {
                        $collected.Add([string]$version) | Out-Null
                    }
                }
            }
            elseif ($null -ne $PSItem) {
                foreach ($value in @($PSItem)) {
                    if ($value) {
                        $collected.Add([string]$value) | Out-Null
                    }
                }
            }
        }
    }

    end {
        $paths = if ($PSCmdlet.ParameterSetName -eq 'Pair') {
            @($Left, $Right)
        }
        else {
            if ($collected.Count -eq 0) {
                throw 'At least one version path is required.'
            }

            $collected.ToArray()
        }

        $typedPaths = [string[]]($paths | Where-Object { $_ })
        $result = $script:DriftBusterBackend.DiffAsync($typedPaths).GetAwaiter().GetResult()

        if ($RawJson) {
            return $result.RawJson
        }

        return ConvertFrom-DriftBusterJson -Json $result.RawJson
    }
}

function Invoke-DriftBusterHunt {
<#
.SYNOPSIS
Scans a directory for drift indicators using backend hunt rules.

.DESCRIPTION
Executes the backend `HuntAsync` routine to surface files and lines that
match built-in detection rules. Results are normalised to the backend JSON
schema.

.PARAMETER Directory
The root directory to scan.

.PARAMETER Pattern
Optional file glob to limit scanned files.

.PARAMETER RawJson
Outputs the backend JSON payload without conversion.

.EXAMPLE
Invoke-DriftBusterHunt -Directory C:\logs -Pattern '*.config'
#>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true, ValueFromPipelineByPropertyName = $true)]
        [ValidateNotNullOrEmpty()]
        [string]
        $Directory,

        [Parameter(ValueFromPipelineByPropertyName = $true)]
        [string]
        $Pattern,

        [Parameter()]
        [switch]
        $RawJson
    )

    process {
        $result = $script:DriftBusterBackend.HuntAsync($Directory, $Pattern).GetAwaiter().GetResult()

        if ($RawJson) {
            return $result.RawJson
        }

        return ConvertFrom-DriftBusterJson -Json $result.RawJson
    }
}

function Get-DriftBusterRunProfile {
<#
.SYNOPSIS
Lists saved DriftBuster run profiles.

.DESCRIPTION
Fetches profiles from the backend cache and emits PowerShell objects whose
property names mirror the backend JSON contract. Use `-PassThru` to access
the underlying .NET objects.

.PARAMETER BaseDir
Optional base directory that overrides the default profile store.

.PARAMETER Name
Filters the returned profiles by name.

.PARAMETER Raw
Outputs the full backend payload instead of each profile entry.

.PARAMETER PassThru
Returns the backend `RunProfileDefinition` instances.

.EXAMPLE
Get-DriftBusterRunProfile | Where-Object name -eq 'Baseline'
#>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(ValueFromPipelineByPropertyName = $true)]
        [string]
        $BaseDir,

        [Parameter(ValueFromPipelineByPropertyName = $true)]
        [string]
        $Name,

        [Parameter()]
        [switch]
        $Raw,

        [Parameter()]
        [switch]
        $PassThru
    )

    process {
        $result = $script:DriftBusterBackend.ListProfilesAsync($BaseDir).GetAwaiter().GetResult()

        if ($PassThru) {
            return $result.Profiles
        }

        $converted = ConvertFrom-DriftBusterModel -Model $result

        if ($Raw) {
            return $converted
        }

        $profiles = @($converted.profiles)
        if ($Name) {
            $profiles = $profiles | Where-Object { $_.name -eq $Name }
        }

        foreach ($profile in $profiles) {
            if ($profile) {
                Write-Output $profile
            }
        }
    }
}

function Save-DriftBusterRunProfile {
<#
.SYNOPSIS
Persists a DriftBuster run profile to disk.

.DESCRIPTION
Normalises the supplied profile (PSCustomObject, hashtable, JSON, or typed
model) before delegating to the backend store. Supports `-WhatIf`/`-Confirm`
and can emit the saved profile in JSON-aligned form.

.PARAMETER Profile
Profile definition to save. Accepts pipeline input.

.PARAMETER BaseDir
Optional base directory override for the profile store.

.PARAMETER PassThru
Returns the saved profile as a PowerShell object.

.EXAMPLE
$profile | Save-DriftBusterRunProfile -BaseDir .\.driftbuster -PassThru
#>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true, ValueFromPipeline = $true)]
        [object]
        $Profile,

        [Parameter(ValueFromPipelineByPropertyName = $true)]
        [string]
        $BaseDir,

        [Parameter()]
        [switch]
        $PassThru
    )

    process {
        $definition = ConvertTo-DriftBusterRunProfileDefinition -InputObject $Profile

        if ([string]::IsNullOrWhiteSpace($definition.Name)) {
            throw 'Run profiles require a Name property.'
        }

        $target = if ($definition.Name) { $definition.Name } else { 'DriftBuster profile' }

        if ($PSCmdlet.ShouldProcess($target, 'Save DriftBuster run profile')) {
            $script:DriftBusterBackend.SaveProfileAsync($definition, $BaseDir).GetAwaiter().GetResult() | Out-Null
            Write-Verbose "Saved profile '$($definition.Name)'"

            if ($PassThru) {
                return ConvertFrom-DriftBusterModel -Model $definition
            }
        }
    }
}

function Invoke-DriftBusterRunProfile {
<#
.SYNOPSIS
Executes a DriftBuster run profile and returns the output manifest.

.DESCRIPTION
Accepts rich profile input (typed, hashtable, JSON, file path) and executes
it through the backend runner. Results default to JSON-aligned PowerShell
objects; use `-PassThru` for the .NET result or `-Raw` for JSON text.

.PARAMETER Profile
Run profile definition to execute. Accepts pipeline input.

.PARAMETER NoSave
Prevents the profile from being persisted before execution.

.PARAMETER BaseDir
Optional working directory for profile resolution and persistence.

.PARAMETER Timestamp
Override timestamp for the run output.

.PARAMETER PassThru
Returns the backend `RunProfileRunResult` object.

.PARAMETER Raw
Returns the backend JSON payload instead of a PowerShell object.

.EXAMPLE
Invoke-DriftBusterRunProfile -Profile $profile -BaseDir .\.driftbuster
#>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true, ValueFromPipeline = $true)]
        [object]
        $Profile,

        [Parameter()]
        [switch]
        $NoSave,

        [Parameter(ValueFromPipelineByPropertyName = $true)]
        [string]
        $BaseDir,

        [Parameter(ValueFromPipelineByPropertyName = $true)]
        [string]
        $Timestamp,

        [Parameter()]
        [switch]
        $PassThru,

        [Parameter()]
        [switch]
        $Raw
    )

    process {
        $definition = ConvertTo-DriftBusterRunProfileDefinition -InputObject $Profile

        if ([string]::IsNullOrWhiteSpace($definition.Name)) {
            throw 'Run profiles require a Name property.'
        }

        $target = if ($definition.Name) { $definition.Name } else { 'DriftBuster profile' }
        $saveProfile = -not $NoSave.IsPresent

        if ($PSCmdlet.ShouldProcess($target, 'Execute DriftBuster run profile')) {
            $result = $script:DriftBusterBackend.RunProfileAsync($definition, $saveProfile, $BaseDir, $Timestamp).GetAwaiter().GetResult()

            if ($Raw) {
                $options = Get-DriftBusterSerializerOptions
                $json = [System.Text.Json.JsonSerializer]::Serialize($result, $result.GetType(), $options)
                return $json
            }

            if ($PassThru) {
                return $result
            }

            return ConvertFrom-DriftBusterModel -Model $result
        }
    }
}

function Export-DriftBusterSqlSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]
        $Database,

        [string]
        $OutputDir = "sql-exports",

        [string[]]
        $Table,

        [string[]]
        $ExcludeTable,

        [string[]]
        $MaskColumn,

        [string[]]
        $HashColumn,

        [string]
        $Placeholder = "[REDACTED]",

        [string]
        $HashSalt = "",

        [int]
        $Limit,

        [string]
        $Prefix,

        [string]
        $PythonPath = "python"
    )

    $arguments = @("-m", "driftbuster.cli", "export-sql", "--output-dir", $OutputDir)

    if ($Table) {
        foreach ($value in $Table) {
            $arguments += @("--table", $value)
        }
    }

    if ($ExcludeTable) {
        foreach ($value in $ExcludeTable) {
            $arguments += @("--exclude-table", $value)
        }
    }

    if ($MaskColumn) {
        foreach ($value in $MaskColumn) {
            $arguments += @("--mask-column", $value)
        }
    }

    if ($HashColumn) {
        foreach ($value in $HashColumn) {
            $arguments += @("--hash-column", $value)
        }
    }

    if ($PSBoundParameters.ContainsKey("Placeholder")) {
        $arguments += @("--placeholder", $Placeholder)
    }

    if ($PSBoundParameters.ContainsKey("HashSalt")) {
        $arguments += @("--hash-salt", $HashSalt)
    }

    if ($PSBoundParameters.ContainsKey("Limit")) {
        $arguments += @("--limit", [string]$Limit)
    }

    if ($PSBoundParameters.ContainsKey("Prefix") -and $Prefix) {
        $arguments += @("--prefix", $Prefix)
    }

    foreach ($databasePath in $Database) {
        $arguments += $databasePath
    }

    $output = & $PythonPath @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = "driftbuster export failed with exit code $LASTEXITCODE"
        if ($output) {
            $message += "`n" + ($output -join [Environment]::NewLine)
        }
        throw $message
    }

    if ($output) {
        Write-Verbose ($output -join [Environment]::NewLine)
    }

    $manifestPath = Join-Path $OutputDir "sql-manifest.json"
    if (Test-Path -LiteralPath $manifestPath) {
        $content = Get-Content -LiteralPath $manifestPath -Raw
        if ($content) {
            return ConvertFrom-DriftBusterJson -Json $content
        }
    }

    return $null
}

Export-ModuleMember -Function *-DriftBuster*
