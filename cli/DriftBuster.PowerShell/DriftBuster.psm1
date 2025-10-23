$ErrorActionPreference = 'Stop'

$script:ModuleManifest = $null
$script:BackendVersion = $null
$script:BackendAssemblyPath = $null

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

    $packagedAssembly = Join-Path $PSScriptRoot 'DriftBuster.Backend.dll'
    if (Test-Path -LiteralPath $packagedAssembly) {
        $candidatePaths += (Resolve-Path -LiteralPath $packagedAssembly).Path
    }

    $devRoot = Join-Path $PSScriptRoot '..\..\gui\DriftBuster.Backend\bin'
    if (Test-Path -LiteralPath $devRoot) {
        $devCandidates = Get-ChildItem -LiteralPath $devRoot -Filter 'DriftBuster.Backend.dll' -Recurse -ErrorAction SilentlyContinue
        if ($devCandidates) {
            $candidatePaths += $devCandidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -ExpandProperty FullName
        }
    }

    $candidatePaths = $candidatePaths | Where-Object { $_ } | Select-Object -Unique
    if (-not $candidatePaths) {
        throw "Backend assembly not found. Run 'dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj' first."
    }

    $selectedCandidate = $candidatePaths |
        Where-Object { Test-Path -LiteralPath $_ } |
        Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
        Select-Object -First 1

    if (-not $selectedCandidate) {
        throw "Unable to locate DriftBuster.Backend.dll. Build the project before importing the module."
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

if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'DriftBuster.Backend' })) {
    $assemblyPath = Get-DriftBusterBackendAssembly
    Add-Type -Path $assemblyPath
}

if (-not $script:DriftBusterBackend) {
    $script:DriftBusterBackend = [DriftBuster.Backend.DriftbusterBackend]::new()
}

function Test-DriftBusterPing {
    [CmdletBinding()]
    param()

    $response = $script:DriftBusterBackend.PingAsync().GetAwaiter().GetResult()
    [pscustomobject]@{
        Status = $response
    }
}

function Invoke-DriftBusterDiff {
    [CmdletBinding(DefaultParameterSetName = 'Versions')]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Versions')]
        [ValidateNotNullOrEmpty()]
        [string[]]
        $Versions,

        [Parameter(Mandatory = $true, ParameterSetName = 'Pair')]
        [string]
        $Left,

        [Parameter(Mandatory = $true, ParameterSetName = 'Pair')]
        [string]
        $Right
    )

    $paths = if ($PSCmdlet.ParameterSetName -eq 'Pair') { @($Left, $Right) } else { $Versions }
    $typedPaths = [string[]]$paths
    $result = $script:DriftBusterBackend.DiffAsync($typedPaths).GetAwaiter().GetResult()
    $result.RawJson | ConvertFrom-Json
}

function Invoke-DriftBusterHunt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Directory,

        [string]
        $Pattern
    )

    $result = $script:DriftBusterBackend.HuntAsync($Directory, $Pattern).GetAwaiter().GetResult()
    $result.RawJson | ConvertFrom-Json
}

function Get-DriftBusterRunProfile {
    [CmdletBinding()]
    param(
        [string]
        $BaseDir
    )

    $result = $script:DriftBusterBackend.ListProfilesAsync($BaseDir).GetAwaiter().GetResult()
    $result.Profiles
}

function Save-DriftBusterRunProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [DriftBuster.Backend.Models.RunProfileDefinition]
        $Profile,

        [string]
        $BaseDir
    )

    $script:DriftBusterBackend.SaveProfileAsync($Profile, $BaseDir).GetAwaiter().GetResult() | Out-Null
    Write-Verbose "Saved profile '$($Profile.Name)'"
}

function Invoke-DriftBusterRunProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [DriftBuster.Backend.Models.RunProfileDefinition]
        $Profile,

        [switch]
        $NoSave,

        [string]
        $BaseDir,

        [string]
        $Timestamp
    )

    $result = $script:DriftBusterBackend.RunProfileAsync($Profile, -not $NoSave.IsPresent, $BaseDir, $Timestamp).GetAwaiter().GetResult()
    $result
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
            return $content | ConvertFrom-Json
        }
    }

    return $null
}

Export-ModuleMember -Function *-DriftBuster*
