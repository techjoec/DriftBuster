$ErrorActionPreference = 'Stop'

$script:ModuleManifest = $null
$script:BackendVersion = $null
$script:BackendAssemblyPath = $null
$script:BackendSourceDirectory = $null
$script:SerializerOptions = $null

function Write-DriftBusterBackendMissingError {
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

        $arguments = [object[]]::new(1)
        $arguments[0] = [string[]]$segments
        $cacheDirectory = $method.Invoke($null, $arguments)
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

function Copy-DriftBusterNativeRuntime {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]
        $CacheDirectory
    )

    # Microsoft.Data.Sqlite binds the native e_sqlite3 library, which a publish folder keeps under runtimes/<rid>/native.
    $runtimeId = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    $sourceNative = Join-Path $SourceDirectory 'runtimes' $runtimeId 'native'
    if (-not (Test-Path -LiteralPath $sourceNative)) {
        return
    }

    $targetNative = Join-Path $CacheDirectory 'runtimes' $runtimeId 'native'
    if (-not (Test-Path -LiteralPath $targetNative)) {
        $null = New-Item -ItemType Directory -Path $targetNative -Force
    }

    foreach ($library in Get-ChildItem -LiteralPath $sourceNative -File) {
        $targetPath = Join-Path $targetNative $library.Name
        if (-not (Test-Path -LiteralPath $targetPath) -or (Get-Item -LiteralPath $targetPath).LastWriteTimeUtc -lt $library.LastWriteTimeUtc) {
            Copy-Item -LiteralPath $library.FullName -Destination $targetPath -Force
        }
    }
}

function Initialize-DriftBusterNativeLibrary {
    [CmdletBinding()]
    param()

    # The native library is loaded by full path before first use; the runtime's later load by name resolves to the loaded image.
    $assemblyDirectory = Split-Path -Parent ([DriftBuster.Backend.DriftbusterBackend].Assembly.Location)
    $runtimeId = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    $nativeDirectory = Join-Path $assemblyDirectory 'runtimes' $runtimeId 'native'
    if (-not (Test-Path -LiteralPath $nativeDirectory)) {
        return
    }

    foreach ($library in Get-ChildItem -LiteralPath $nativeDirectory -File -Filter '*e_sqlite3*') {
        $handle = [IntPtr]::Zero
        $null = [System.Runtime.InteropServices.NativeLibrary]::TryLoad($library.FullName, [ref]$handle)
    }
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
        throw (Write-DriftBusterBackendMissingError -SearchedPaths $searchedPaths)
    }

    # The newest assembly wins, so a fresh build is never shadowed by an older publish folder; on a tie, the folder holding the SQLite
    # dependencies does.
    $selectedCandidate = $candidatePaths |
        Where-Object { Test-Path -LiteralPath $_ } |
        Sort-Object -Property @(
            @{ Expression = { (Get-Item -LiteralPath $_).LastWriteTimeUtc }; Descending = $true },
            @{ Expression = { Test-Path -LiteralPath (Join-Path (Split-Path -Parent $_) 'Microsoft.Data.Sqlite.dll') }; Descending = $true }
        ) |
        Select-Object -First 1

    if (-not $selectedCandidate) {
        throw (Write-DriftBusterBackendMissingError -SearchedPaths $candidatePaths)
    }

    $resolvedCandidate = (Resolve-Path -LiteralPath $selectedCandidate).Path

    $cacheDirectory = Get-DriftBusterBackendCacheDirectory -AssemblyPath $resolvedCandidate -Version $backendVersion
    $cacheAssemblyPath = Join-Path $cacheDirectory 'DriftBuster.Backend.dll'

    $sourceDirectory = Split-Path -Parent $resolvedCandidate
    $script:BackendSourceDirectory = $sourceDirectory

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

    $dependencyPatterns = @('*.dll', '*.json')
    foreach ($pattern in $dependencyPatterns) {
        $dependencies = Get-ChildItem -LiteralPath $sourceDirectory -Filter $pattern -File -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -ne 'DriftBuster.Backend.dll'
        }

        foreach ($dependency in $dependencies) {
            $targetPath = Join-Path $cacheDirectory $dependency.Name
            $copyDependency = $true

            if (Test-Path -LiteralPath $targetPath) {
                $sourceWrite = $dependency.LastWriteTimeUtc
                $targetWrite = (Get-Item -LiteralPath $targetPath).LastWriteTimeUtc
                if ($targetWrite -ge $sourceWrite) {
                    $copyDependency = $false
                }
            }

            if ($copyDependency) {
                Copy-Item -LiteralPath $dependency.FullName -Destination $targetPath -Force
            }
        }
    }

    Copy-DriftBusterNativeRuntime -SourceDirectory $sourceDirectory -CacheDirectory $cacheDirectory

    $script:BackendAssemblyPath = (Resolve-Path -LiteralPath $cacheAssemblyPath).Path
    return $script:BackendAssemblyPath
}

function Get-DriftBusterSerializerOption {
    if ($script:SerializerOptions) {
        return $script:SerializerOptions
    }

    # snake_case, as the backend's own JSON names its models.
    $options = [System.Text.Json.JsonSerializerOptions]::new()
    $options.DefaultIgnoreCondition = [System.Text.Json.Serialization.JsonIgnoreCondition]::WhenWritingNull
    $options.PropertyNamingPolicy = [System.Text.Json.JsonNamingPolicy]::SnakeCaseLower
    $options.PropertyNameCaseInsensitive = $true
    $converterType = [System.Type]::GetType('System.Text.Json.Serialization.JsonStringEnumMemberConverter, System.Text.Json', $false)
    if ($converterType) {
        $options.Converters.Add([System.Activator]::CreateInstance($converterType))
    }
    else {
        $options.Converters.Add([System.Text.Json.Serialization.JsonStringEnumConverter]::new())
    }

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

    $options = Get-DriftBusterSerializerOption
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
        elseif ($null -ne $Object -and $Object.PSObject) {
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
    [OutputType([DriftBuster.Backend.Models.RunProfileDefinition])]
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

        # A path or JSON text, a hashtable or an object: read strictly as profile.json is, except that a source may be a plain path.
        $value = $InputObject
        if ($value -is [string]) {
            $text = if (Test-Path -LiteralPath $value) { Get-Content -LiteralPath $value -Raw } else { $value }
            if ([string]::IsNullOrWhiteSpace($text)) {
                throw 'Run profile content was empty.'
            }

            $value = $text | ConvertFrom-Json -AsHashtable -Depth 64
        }

        $map = [ordered]@{}
        foreach ($entry in @(if ($value -is [System.Collections.IDictionary]) { $value.GetEnumerator() } else { $value.PSObject.Properties })) {
            $map[[string]$entry.Name] = $entry.Value
        }

        if ($map.Contains('sources')) {
            $map['sources'] = @(foreach ($source in @($map['sources'])) {
                    if ($source -is [string]) { @{ path = $source } } else { $source }
                })
        }

        $json = $map | ConvertTo-Json -Depth 64 -Compress
        return [System.Text.Json.JsonSerializer]::Deserialize($json, [DriftBuster.Backend.Models.RunProfileDefinition], [DriftBuster.Backend.Json.ModelJson]::Options)
    }
}

if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'DriftBuster.Backend' })) {
    $assemblyPath = Get-DriftBusterBackendAssembly
    Add-Type -Path $assemblyPath
}

Initialize-DriftBusterNativeLibrary

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

        foreach ($profileDef in $profiles) {
            if ($profileDef) {
                Write-Output $profileDef
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
                $options = Get-DriftBusterSerializerOption
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

function Wait-DriftBusterTask {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Threading.Tasks.Task]
        $Task
    )

    # GetResult rethrows the backend's own exception; PowerShell wraps it in a MethodInvocationException, which is unwrapped here.
    try {
        return $Task.GetAwaiter().GetResult()
    }
    catch [System.Management.Automation.MethodInvocationException] {
        if ($_.Exception.InnerException) {
            throw $_.Exception.InnerException
        }

        throw
    }
}

function Resolve-DriftBusterProviderPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Path
    )

    # The backend resolves relative paths against the process directory; PowerShell callers mean their current location.
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function ConvertTo-DriftBusterScheduleJson {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [object]
        $Value
    )

    # The backend's own JSON contract (snake_case, indented), as the console tool prints it.
    return [System.Text.Json.JsonSerializer]::Serialize($Value, $Value.GetType(), [DriftBuster.Backend.Json.ModelJson]::Options)
}

function ConvertTo-DriftBusterScheduleTime {
    [CmdletBinding()]
    [OutputType([System.Nullable[System.DateTimeOffset]])]
    param(
        [string]
        $Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    # ISO 8601; a time without an offset is UTC.
    return [DriftBuster.Backend.Scheduling.ScheduleParsing]::ParseTimestamp($Text)
}

function Export-DriftBusterSqlSnapshot {
<#
.SYNOPSIS
Exports anonymised SQLite snapshots through the DriftBuster backend.

.DESCRIPTION
Calls the backend `ExportSqlSnapshotAsync` API, which writes one
`<prefix>-sql-snapshot.json` per database and a `sql-manifest.json` under the
output directory. Masked columns hold the placeholder and hashed columns hold
salted SHA-256 digests. Returns the manifest as a PowerShell object; throws
when any database is missing or fails to export.

.PARAMETER Database
SQLite database paths, exported in order.

.PARAMETER OutputDir
Directory the snapshots and manifest are written to.

.PARAMETER Table
Tables to export; every table is exported when omitted.

.PARAMETER ExcludeTable
Tables never exported.

.PARAMETER MaskColumn
`table.column` entries whose values are replaced by the placeholder.

.PARAMETER HashColumn
`table.column` entries whose values are hashed.

.PARAMETER Placeholder
Text masked columns hold.

.PARAMETER HashSalt
Salt applied to hashed values.

.PARAMETER Limit
Maximum rows exported per table.

.PARAMETER Prefix
Prefix of the snapshot file names.

.EXAMPLE
Export-DriftBusterSqlSnapshot -Database .\app.sqlite -MaskColumn accounts.secret -HashColumn accounts.email -OutputDir .\exports
#>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
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
        $Prefix
    )

    $request = [DriftBuster.Backend.Models.SqlExportRequest]::new()
    $request.Databases = [string[]]@($Database | ForEach-Object { Resolve-DriftBusterProviderPath -Path $_ })
    $request.OutputDir = Resolve-DriftBusterProviderPath -Path $OutputDir
    $request.Tables = [string[]]@($Table | Where-Object { $_ })
    $request.ExcludeTables = [string[]]@($ExcludeTable | Where-Object { $_ })
    $request.MaskColumns = [string[]]@($MaskColumn | Where-Object { $_ })
    $request.HashColumns = [string[]]@($HashColumn | Where-Object { $_ })
    $request.Placeholder = $Placeholder
    $request.HashSalt = $HashSalt
    if ($PSBoundParameters.ContainsKey('Limit')) {
        $request.Limit = $Limit
    }
    if ($Prefix) {
        $request.Prefix = $Prefix
    }

    $result = Wait-DriftBusterTask -Task $script:DriftBusterBackend.ExportSqlSnapshotAsync($request)
    if ($result.Output) {
        Write-Verbose $result.Output.TrimEnd()
    }

    if ($result.ExitCode -ne 0) {
        $message = "driftbuster export failed with exit code $($result.ExitCode)"
        if ($result.Errors) {
            $message += "`n" + $result.Errors.TrimEnd()
        }
        throw $message
    }

    return ConvertFrom-DriftBusterJson -Json $result.ManifestJson
}

function Invoke-DriftBusterCaptureRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Root,

        [Parameter(Mandatory = $true)]
        [string]
        $OutputDir,

        [string]
        $ProfilesPath,

        [string[]]
        $ProfileTag,

        [string[]]
        $MaskToken,

        [switch]
        $AllowUnmasked,

        [switch]
        $SkipHunt,

        [string]
        $Operator,

        [string]
        $Environment,

        [string]
        $Reason
    )

    $request = [DriftBuster.Backend.Models.CaptureRunRequest]::new()
    $request.Root = $Root
    $request.OutputDir = $OutputDir
    if ($ProfilesPath) {
        $request.ProfilesPath = $ProfilesPath
    }
    $request.ProfileTags = [string[]]@($ProfileTag | Where-Object { $_ })
    $request.MaskTokens = [string[]]@($MaskToken | Where-Object { $_ })
    $request.AllowUnmasked = [bool]$AllowUnmasked
    $request.SkipHunt = [bool]$SkipHunt
    $request.Operator = if ($Operator) { $Operator } else { $null }
    $request.Environment = $Environment
    $request.Reason = $Reason

    $result = Wait-DriftBusterTask -Task $script:DriftBusterBackend.RunCaptureAsync($request)
    if ($result.Output) {
        Write-Verbose $result.Output.TrimEnd()
    }

    if ($result.ExitCode -ne 0) {
        $message = "Capture of $Root failed with exit code $($result.ExitCode)"
        if ($result.Errors) {
            $message += "`n" + $result.Errors.TrimEnd()
        }
        throw $message
    }

    if ($result.Errors) {
        Write-Warning $result.Errors.TrimEnd()
    }

    return [pscustomobject]@{
        SnapshotPath = $result.SnapshotPath
        ManifestPath = $result.ManifestPath
    }
}

function Get-DriftBusterAdminShareTargetPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $ComputerName,

        [Parameter(Mandatory = $true)]
        [string]
        $Share,

        [Parameter(Mandatory = $true)]
        [string]
        $Path
    )

    if ($Path.StartsWith('\\')) {
        return $Path
    }

    # Built as text rather than with Join-Path so the UNC form is the same on every host OS.
    $shareRoot = '\\{0}\{1}' -f $ComputerName, $Share
    $relative = if ($Path -match '^[A-Za-z]:\\') { $Path.Substring(3) } else { $Path }
    $relative = ($relative -replace '\\+', '\').Trim('\')

    if ([string]::IsNullOrWhiteSpace($relative)) {
        return $shareRoot
    }

    return '{0}\{1}' -f $shareRoot, $relative
}

function Invoke-DriftBusterAdminShareScan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $ComputerName,

        [Parameter(Mandatory = $true)]
        [string]
        $RemotePath,

        [Parameter(Mandatory = $true)]
        [string]
        $AdminShare,

        [Parameter(Mandatory = $true)]
        [string]
        $LocalOutput,

        [Parameter(Mandatory = $true)]
        [hashtable]
        $CaptureParameters,

        [string]
        $ProfilePath,

        [switch]
        $PersistShare,

        [System.Management.Automation.PSCredential]
        $Credential
    )

    $targetPath = Get-DriftBusterAdminShareTargetPath -ComputerName $ComputerName -Share $AdminShare -Path $RemotePath
    $driveName = $null

    if ($Credential) {
        # The drive authenticates the SMB connection; the capture itself reads the UNC path.
        $driveName = 'DBR{0}' -f ([Guid]::NewGuid().ToString('N').Substring(0, 8).ToUpperInvariant())
        $driveParams = @{
            Name       = $driveName
            PSProvider = 'FileSystem'
            Root       = ('\\{0}\{1}' -f $ComputerName, $AdminShare)
            Scope      = 'Script'
            Credential = $Credential
        }

        if ($PersistShare) {
            $driveParams.Persist = $true
        }

        $null = New-PSDrive @driveParams
    }

    try {
        $runParameters = @{} + $CaptureParameters
        $runParameters.Root = $targetPath
        $runParameters.OutputDir = $LocalOutput
        if ($ProfilePath) {
            $runParameters.ProfilesPath = $ProfilePath
        }

        $capture = Invoke-DriftBusterCaptureRun @runParameters

        return [pscustomobject]@{
            ComputerName    = $ComputerName
            Mode            = 'AdminShare'
            OutputDirectory = $LocalOutput
            TargetPath      = $targetPath
            SnapshotPath    = $capture.SnapshotPath
            ManifestPath    = $capture.ManifestPath
        }
    }
    finally {
        if ($driveName -and -not $PersistShare) {
            Remove-PSDrive -Name $driveName -Force -ErrorAction SilentlyContinue
        }
    }
}

# Runs on the remote host: creates a unique staging folder under the working directory and reports the paths the scan uses there.
$script:RemoteStageScript = {
    param([string] $WorkingDirectory)

    $expanded = $ExecutionContext.InvokeCommand.ExpandString($WorkingDirectory)
    $staging = Join-Path $expanded ([Guid]::NewGuid().ToString('N'))
    $moduleDirectory = Join-Path $staging 'DriftBuster'
    $runtimesDirectory = Join-Path $moduleDirectory 'runtimes'
    $outputDirectory = Join-Path $staging 'captures'
    try {
        foreach ($directory in @($runtimesDirectory, $outputDirectory)) {
            $null = New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop
        }
    }
    catch {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }

    [pscustomobject]@{
        StagingDirectory  = $staging
        ModuleDirectory   = $moduleDirectory
        ModuleManifest    = Join-Path $moduleDirectory 'DriftBuster.psd1'
        RuntimesDirectory = $runtimesDirectory
        RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
        ProfilesPath      = Join-Path $staging 'profiles.json'
        OutputDirectory   = $outputDirectory
        OutputItems       = Join-Path $outputDirectory '*'
    }
}

# Runs on the remote host: imports the staged module and runs the capture in process there.
$script:RemoteCaptureScript = {
    param([string] $ModuleManifest, [hashtable] $CaptureParameters)

    $module = Import-Module -Name $ModuleManifest -Force -PassThru -ErrorAction Stop
    & $module { param($Parameters) Invoke-DriftBusterCaptureRun @Parameters } $CaptureParameters
}

# Runs on the remote host: removes the staging folder.
$script:RemoteCleanupScript = {
    param([string] $StagingDirectory)

    Remove-Item -LiteralPath $StagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

function Copy-DriftBusterStagedModule {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]
        $Session,

        [Parameter(Mandatory = $true)]
        [object]
        $Remote
    )

    # The module folder and the backend assembly folder land together, so the staged module finds its backend beside it.
    $backendDirectory = Split-Path -Parent ([DriftBuster.Backend.DriftbusterBackend].Assembly.Location)
    if ($script:BackendSourceDirectory) {
        $backendDirectory = $script:BackendSourceDirectory
    }

    $moduleFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -File)
    $backendFiles = @(Get-ChildItem -LiteralPath $backendDirectory -File | Where-Object { $_.Extension -in '.dll', '.json' })
    foreach ($file in @($moduleFiles + $backendFiles)) {
        Copy-Item -ToSession $Session -LiteralPath $file.FullName -Destination $Remote.ModuleDirectory -Force -ErrorAction Stop
    }

    $nativeRuntime = Join-Path $backendDirectory 'runtimes' $Remote.RuntimeIdentifier
    if (Test-Path -LiteralPath $nativeRuntime) {
        Copy-Item -ToSession $Session -LiteralPath $nativeRuntime -Destination $Remote.RuntimesDirectory -Recurse -Force -ErrorAction Stop
    }
}

function Invoke-DriftBusterWinRMScan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $ComputerName,

        [Parameter(Mandatory = $true)]
        [string]
        $RemotePath,

        [Parameter(Mandatory = $true)]
        [string]
        $LocalOutput,

        [Parameter(Mandatory = $true)]
        [hashtable]
        $CaptureParameters,

        [Parameter(Mandatory = $true)]
        [hashtable]
        $SessionParameters,

        [Parameter(Mandatory = $true)]
        [string]
        $RemoteWorkingDirectory,

        [string]
        $ProfilePath,

        [switch]
        $KeepRemoteArtifacts
    )

    $session = New-PSSession @SessionParameters -ErrorAction Stop
    try {
        $remote = Invoke-Command -Session $session -ScriptBlock $script:RemoteStageScript -ArgumentList $RemoteWorkingDirectory -ErrorAction Stop
        try {
            Copy-DriftBusterStagedModule -Session $session -Remote $remote

            $runParameters = @{} + $CaptureParameters
            $runParameters.Root = $RemotePath
            $runParameters.OutputDir = $remote.OutputDirectory
            if ($ProfilePath) {
                Copy-Item -ToSession $session -LiteralPath $ProfilePath -Destination $remote.ProfilesPath -Force -ErrorAction Stop
                $runParameters.ProfilesPath = $remote.ProfilesPath
            }

            $capture = Invoke-Command -Session $session -ScriptBlock $script:RemoteCaptureScript -ArgumentList $remote.ModuleManifest, $runParameters -ErrorAction Stop
            Copy-Item -FromSession $session -Path $remote.OutputItems -Destination $LocalOutput -Recurse -Force -ErrorAction Stop
        }
        finally {
            if (-not $KeepRemoteArtifacts) {
                Invoke-Command -Session $session -ScriptBlock $script:RemoteCleanupScript -ArgumentList $remote.StagingDirectory -ErrorAction Continue
            }
        }

        # The remote host reports its own paths; the copies sit under the local output directory with the same file names.
        return [pscustomobject]@{
            ComputerName    = $ComputerName
            Mode            = 'WinRM'
            OutputDirectory = $LocalOutput
            SnapshotPath    = Join-Path $LocalOutput (($capture.SnapshotPath -split '[\\/]')[-1])
            ManifestPath    = Join-Path $LocalOutput (($capture.ManifestPath -split '[\\/]')[-1])
        }
    }
    finally {
        Remove-PSSession -Session $session
    }
}

function Invoke-DriftBusterRemoteScan {
<#
.SYNOPSIS
Runs a DriftBuster capture against remote hosts.

.DESCRIPTION
Coordinates capture runs using either administrative SMB shares or WinRM
remoting. Administrative share mode runs the backend capture in this process
against the UNC path. WinRM mode stages this module and the backend assembly
folder in a unique folder under the remote working directory, imports the
module there, runs the capture on the remote host, copies the snapshot and
manifest back, and removes the staged folder.

The capture refuses to run without an environment, a reason, and either mask
tokens or an explicit -AllowUnmasked.

.PARAMETER ComputerName
Target host name(s) to scan.

.PARAMETER RemotePath
Directory on the target host to capture. UNC paths are respected as-is; drive
roots (for example `C:\ProgramData`) are converted to relative segments when
admin share access is requested.

.PARAMETER RunProfilePath
Optional detection profile store JSON applied to the capture.

.PARAMETER Environment
Environment label recorded in the capture manifest.

.PARAMETER Reason
Reason for the capture, recorded in the manifest.

.PARAMETER Operator
Operator recorded in the manifest; defaults to DRIFTBUSTER_CAPTURE_OPERATOR,
USER or USERNAME on the host that runs the capture.

.PARAMETER MaskToken
Sensitive tokens redacted from the snapshot.

.PARAMETER AllowUnmasked
Allows a capture without mask tokens.

.PARAMETER ProfileTag
Tags activating profiles from the profile store.

.PARAMETER SkipHunt
Skips the hunt scan.

.PARAMETER OutputDirectory
Local directory that stores the capture artefacts for each host.

.PARAMETER AdminShare
Administrative share (defaults to `C$`) when using SMB access.

.PARAMETER PersistShare
Keeps the credential drive mapping after the scan.

.PARAMETER UseWinRM
Switch to enable WinRM staging instead of direct SMB access.

.PARAMETER RemoteWorkingDirectory
Directory on the remote host under which the staging folder is created when
WinRM is used. Expanded on the remote host.

.PARAMETER ConfigurationName
WinRM session configuration; the backend needs PowerShell 7.6, which
`Enable-PSRemoting` registers as `PowerShell.7`.

.PARAMETER Port
Custom WinRM port when the remote endpoint does not use the default.

.PARAMETER UseSSL
Toggle WinRM SSL negotiation for remote endpoints.

.PARAMETER KeepRemoteArtifacts
Skips remote clean-up when WinRM staging is used so operators can examine the
staged folder in place. Clean-up removes the staged folder only: the staged
module's import keeps its versioned backend cache under the remote user's
DriftBuster data root, as any import of the module does, and reuses it on the
next scan.

.PARAMETER Credential
Optional credential applied to the SMB drive mapping or WinRM session.

.EXAMPLE
Invoke-DriftBusterRemoteScan -ComputerName 'branch-01' -RemotePath 'ProgramData\Vendor' -Environment prod -Reason audit -MaskToken 'hunter2'

.EXAMPLE
Invoke-DriftBusterRemoteScan -ComputerName 'hq-core' -RemotePath 'C:\ProgramData\Vendor' -RunProfilePath profiles\hq.json -Environment prod -Reason audit -AllowUnmasked -UseWinRM
#>
    [CmdletBinding(DefaultParameterSetName = 'AdminShare')]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true, ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true)]
        [ValidateNotNullOrEmpty()]
        [Alias('Host')]
        [string[]]
        $ComputerName,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]
        $RemotePath,

        [Parameter()]
        [string]
        $RunProfilePath,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]
        $Environment,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]
        $Reason,

        [Parameter()]
        [string]
        $Operator,

        [Parameter()]
        [string[]]
        $MaskToken,

        [Parameter()]
        [switch]
        $AllowUnmasked,

        [Parameter()]
        [string[]]
        $ProfileTag,

        [Parameter()]
        [switch]
        $SkipHunt,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]
        $OutputDirectory = (Join-Path (Get-Location) 'driftbuster-remote'),

        [Parameter(ParameterSetName = 'AdminShare')]
        [ValidateNotNullOrEmpty()]
        [string]
        $AdminShare = 'C$',

        [Parameter(ParameterSetName = 'AdminShare')]
        [switch]
        $PersistShare,

        [Parameter(ParameterSetName = 'WinRM', Mandatory = $true)]
        [switch]
        $UseWinRM,

        [Parameter(ParameterSetName = 'WinRM')]
        [ValidateNotNullOrEmpty()]
        [string]
        $RemoteWorkingDirectory = '$env:ProgramData\DriftBuster\RemoteScan',

        [Parameter(ParameterSetName = 'WinRM')]
        [ValidateNotNullOrEmpty()]
        [string]
        $ConfigurationName = 'PowerShell.7',

        [Parameter(ParameterSetName = 'WinRM')]
        [int]
        $Port,

        [Parameter(ParameterSetName = 'WinRM')]
        [switch]
        $UseSSL,

        [Parameter(ParameterSetName = 'WinRM')]
        [switch]
        $KeepRemoteArtifacts,

        [Parameter(ParameterSetName = 'AdminShare')]
        [Parameter(ParameterSetName = 'WinRM')]
        [System.Management.Automation.PSCredential]
        $Credential
    )

    begin {
        $resolvedProfile = $null
        if ($RunProfilePath) {
            $resolvedProfile = (Resolve-Path -LiteralPath $RunProfilePath -ErrorAction Stop).Path
        }

        $resolvedOutput = Resolve-DriftBusterProviderPath -Path $OutputDirectory
        if (-not (Test-Path -LiteralPath $resolvedOutput)) {
            $null = New-Item -ItemType Directory -Path $resolvedOutput -Force
        }

        $captureParameters = @{
            Environment   = $Environment
            Reason        = $Reason
            MaskToken     = @($MaskToken | Where-Object { $_ })
            ProfileTag    = @($ProfileTag | Where-Object { $_ })
            AllowUnmasked = [bool]$AllowUnmasked
            SkipHunt      = [bool]$SkipHunt
        }
        if ($Operator) {
            $captureParameters.Operator = $Operator
        }

        $results = New-Object System.Collections.Generic.List[object]
    }

    process {
        foreach ($computer in $ComputerName) {
            if ([string]::IsNullOrWhiteSpace($computer)) {
                continue
            }

            $localOutput = Join-Path $resolvedOutput $computer
            if (-not (Test-Path -LiteralPath $localOutput)) {
                $null = New-Item -ItemType Directory -Path $localOutput -Force
            }

            if ($PSCmdlet.ParameterSetName -eq 'WinRM') {
                $sessionParameters = @{ ComputerName = $computer; ConfigurationName = $ConfigurationName }
                if ($Credential) {
                    $sessionParameters.Credential = $Credential
                }
                if ($PSBoundParameters.ContainsKey('Port')) {
                    $sessionParameters.Port = $Port
                }
                if ($UseSSL) {
                    $sessionParameters.UseSSL = $true
                }

                $scan = @{
                    ComputerName           = $computer
                    RemotePath             = $RemotePath
                    LocalOutput            = $localOutput
                    CaptureParameters      = $captureParameters
                    SessionParameters      = $sessionParameters
                    RemoteWorkingDirectory = $RemoteWorkingDirectory
                    KeepRemoteArtifacts    = $KeepRemoteArtifacts
                }
                if ($resolvedProfile) {
                    $scan.ProfilePath = $resolvedProfile
                }

                $results.Add((Invoke-DriftBusterWinRMScan @scan)) | Out-Null
            }
            else {
                $scan = @{
                    ComputerName      = $computer
                    RemotePath        = $RemotePath
                    AdminShare        = $AdminShare
                    LocalOutput       = $localOutput
                    CaptureParameters = $captureParameters
                    PersistShare      = $PersistShare
                }
                if ($resolvedProfile) {
                    $scan.ProfilePath = $resolvedProfile
                }
                if ($Credential) {
                    $scan.Credential = $Credential
                }

                $results.Add((Invoke-DriftBusterAdminShareScan @scan)) | Out-Null
            }
        }
    }

    end {
        return $results
    }
}

function ConvertFrom-DriftBusterScheduleModel {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]
        $Model
    )

    return ConvertFrom-DriftBusterJson -Json (ConvertTo-DriftBusterScheduleJson -Value $Model)
}

function Write-DriftBusterScheduleOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]
        $Result,

        [object[]]
        $Entry,

        [switch]
        $Raw
    )

    # -Raw is the whole result as the console tool prints it; otherwise one object per entry (or the result itself).
    if ($Raw) {
        return ConvertTo-DriftBusterScheduleJson -Value $Result
    }

    foreach ($item in $(if ($PSBoundParameters.ContainsKey('Entry')) { $Entry } else { @($Result) })) {
        Write-Output (ConvertFrom-DriftBusterScheduleModel -Model $item)
    }
}

function Get-DriftBusterSchedule {
<#
.SYNOPSIS
Lists run profile schedules with their scheduler state.

.DESCRIPTION
Calls the backend `ListScheduleStatusAsync` API and emits one object per
schedule, ordered by name, with `name`, `profile`, `interval_seconds`, `tags`,
`metadata`, `start_at`, `next_run`, `pending` and, when defined, `window`.

.PARAMETER BaseDir
Base directory whose `Profiles` folder holds the default manifest and state.

.PARAMETER ConfigPath
Schedule manifest path; defaults to `Profiles/schedules.json` under the base directory.

.PARAMETER StatePath
Scheduler state path; defaults to `Profiles/scheduler-state.json` under the base directory.

.PARAMETER Raw
Returns the schedules as JSON text.

.EXAMPLE
Get-DriftBusterSchedule -BaseDir .\.driftbuster
#>
    [CmdletBinding()]
    [OutputType([pscustomobject], [string])]
    param(
        [string]
        $BaseDir,

        [string]
        $ConfigPath,

        [string]
        $StatePath,

        [switch]
        $Raw
    )

    $paths = Resolve-DriftBusterSchedulePath -BaseDir $BaseDir -ConfigPath $ConfigPath -StatePath $StatePath
    $result = Wait-DriftBusterTask -Task $script:DriftBusterBackend.ListScheduleStatusAsync($paths.BaseDir, $paths.ConfigPath, $paths.StatePath)
    Write-DriftBusterScheduleOutput -Result $result -Entry @($result.Schedules) -Raw:$Raw
}

function Get-DriftBusterScheduleDue {
<#
.SYNOPSIS
Lists the schedule runs due at a reference time.

.DESCRIPTION
Calls the backend `ListDueSchedulesAsync` API. Each due run is recorded as
pending in the scheduler state file until it is completed. Emits one object per
run with `name`, `profile`, `scheduled_for`, `tags` and `metadata`.

.PARAMETER BaseDir
Base directory whose `Profiles` folder holds the default manifest and state.

.PARAMETER ConfigPath
Schedule manifest path.

.PARAMETER StatePath
Scheduler state path.

.PARAMETER At
ISO-8601 reference time; a time without an offset is UTC. Defaults to now.

.PARAMETER Raw
Returns the runs as JSON text.

.EXAMPLE
Get-DriftBusterScheduleDue -BaseDir .\.driftbuster -At '2025-01-02T00:00:00Z'
#>
    [CmdletBinding()]
    [OutputType([pscustomobject], [string])]
    param(
        [string]
        $BaseDir,

        [string]
        $ConfigPath,

        [string]
        $StatePath,

        [string]
        $At,

        [switch]
        $Raw
    )

    $paths = Resolve-DriftBusterSchedulePath -BaseDir $BaseDir -ConfigPath $ConfigPath -StatePath $StatePath
    $reference = ConvertTo-DriftBusterScheduleTime -Text $At
    $result = Wait-DriftBusterTask -Task $script:DriftBusterBackend.ListDueSchedulesAsync($reference, $paths.BaseDir, $paths.ConfigPath, $paths.StatePath)
    Write-DriftBusterScheduleOutput -Result $result -Entry @($result.Runs) -Raw:$Raw
}

function Complete-DriftBusterSchedule {
<#
.SYNOPSIS
Marks a schedule's pending run complete.

.DESCRIPTION
Calls the backend `CompleteScheduleAsync` API, which advances the schedule's
next run and clears its pending run in the state file. Returns `name`,
`next_run` and `pending`.

.PARAMETER Name
Schedule name.

.PARAMETER BaseDir
Base directory whose `Profiles` folder holds the default manifest and state.

.PARAMETER ConfigPath
Schedule manifest path.

.PARAMETER StatePath
Scheduler state path.

.PARAMETER CompletedAt
ISO-8601 completion time; a time without an offset is UTC. Defaults to now.

.PARAMETER Raw
Returns the state as JSON text.

.EXAMPLE
Complete-DriftBusterSchedule -Name nightly -BaseDir .\.driftbuster
#>
    [CmdletBinding()]
    [OutputType([pscustomobject], [string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Name,

        [string]
        $BaseDir,

        [string]
        $ConfigPath,

        [string]
        $StatePath,

        [string]
        $CompletedAt,

        [switch]
        $Raw
    )

    $paths = Resolve-DriftBusterSchedulePath -BaseDir $BaseDir -ConfigPath $ConfigPath -StatePath $StatePath
    $completed = ConvertTo-DriftBusterScheduleTime -Text $CompletedAt
    $result = Wait-DriftBusterTask -Task $script:DriftBusterBackend.CompleteScheduleAsync($Name, $completed, $paths.BaseDir, $paths.ConfigPath, $paths.StatePath)
    Write-DriftBusterScheduleOutput -Result $result -Raw:$Raw
}

function Skip-DriftBusterSchedule {
<#
.SYNOPSIS
Skips a schedule's runs until a resume time.

.DESCRIPTION
Calls the backend `SkipScheduleAsync` API, which moves the schedule's next run
to the resume time in the state file. Returns `name`, `next_run` and `pending`.

.PARAMETER Name
Schedule name.

.PARAMETER ResumeAt
ISO-8601 time the schedule resumes; a time without an offset is UTC.

.PARAMETER BaseDir
Base directory whose `Profiles` folder holds the default manifest and state.

.PARAMETER ConfigPath
Schedule manifest path.

.PARAMETER StatePath
Scheduler state path.

.PARAMETER Raw
Returns the state as JSON text.

.EXAMPLE
Skip-DriftBusterSchedule -Name nightly -ResumeAt '2025-01-05T09:30:00Z'
#>
    [CmdletBinding()]
    [OutputType([pscustomobject], [string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Name,

        [Parameter(Mandatory = $true)]
        [string]
        $ResumeAt,

        [string]
        $BaseDir,

        [string]
        $ConfigPath,

        [string]
        $StatePath,

        [switch]
        $Raw
    )

    $paths = Resolve-DriftBusterSchedulePath -BaseDir $BaseDir -ConfigPath $ConfigPath -StatePath $StatePath
    $result = Wait-DriftBusterTask -Task $script:DriftBusterBackend.SkipScheduleAsync($Name, (ConvertTo-DriftBusterScheduleTime -Text $ResumeAt), $paths.BaseDir, $paths.ConfigPath, $paths.StatePath)
    Write-DriftBusterScheduleOutput -Result $result -Raw:$Raw
}

function Resolve-DriftBusterSchedulePath {
    [CmdletBinding()]
    param(
        [string]
        $BaseDir,

        [string]
        $ConfigPath,

        [string]
        $StatePath
    )

    $resolve = {
        param([string] $Value)
        if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
        return Resolve-DriftBusterProviderPath -Path $Value
    }

    return [pscustomobject]@{
        BaseDir    = & $resolve $BaseDir
        ConfigPath = & $resolve $ConfigPath
        StatePath  = & $resolve $StatePath
    }
}

Export-ModuleMember -Function @((Get-DriftBusterModuleManifest).FunctionsToExport)
