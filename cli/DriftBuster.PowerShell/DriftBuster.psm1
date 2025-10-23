$ErrorActionPreference = 'Stop'

function Get-DriftBusterBackendAssembly {
    param()

    $root = Join-Path $PSScriptRoot '..\..\gui\DriftBuster.Backend\bin'
    if (-not (Test-Path -LiteralPath $root)) {
        throw "Backend assembly not found. Run 'dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj' first."
    }

    $candidates = Get-ChildItem -LiteralPath $root -Filter 'DriftBuster.Backend.dll' -Recurse
    if (-not $candidates) {
        throw "Unable to locate DriftBuster.Backend.dll under $root. Build the project before importing the module."
    }

    # Prefer assemblies from a 'published' output (which includes dependencies)
    $published = $candidates | Where-Object { $_.FullName -match "[/\\]published[/\\]" } | Sort-Object LastWriteTime -Descending
    if ($published) {
        $assembly = $published | Select-Object -First 1
    }
    else {
        $assembly = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $assembly) {
        throw "Unable to locate DriftBuster.Backend.dll under $root. Build the project before importing the module."
    }

    return $assembly.FullName
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
