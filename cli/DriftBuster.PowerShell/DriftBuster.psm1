$ErrorActionPreference = 'Stop'

function Get-DriftBusterBackendAssembly {
    param()

    $root = Join-Path $PSScriptRoot '..\..\gui\DriftBuster.Backend\bin'
    if (-not (Test-Path -LiteralPath $root)) {
        throw "Backend assembly not found. Run 'dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj' first."
    }

    $assembly = Get-ChildItem -LiteralPath $root -Filter 'DriftBuster.Backend.dll' -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
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
    $result = $script:DriftBusterBackend.DiffAsync($paths).GetAwaiter().GetResult()
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

Export-ModuleMember -Function *-DriftBuster*
