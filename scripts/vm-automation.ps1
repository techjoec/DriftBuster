# DriftBuster GUI Automation Helper
# Sends JSON commands to the DriftBuster automation named pipe.
# Usage:
#   . C:\DriftBusterTest\vm-automation.ps1
#   Send-DriftBusterCommand 'DriftBuster-Automation-1234' @{cmd='ping'}
#   Send-DriftBusterCommand 'DriftBuster-Automation-1234' @{cmd='navigate'; tab='multi-server'}
#   Send-DriftBusterCommand 'DriftBuster-Automation-1234' @{cmd='get-state'}

function Send-DriftBusterCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PipeName,

        [Parameter(Mandatory)]
        [hashtable]$Command,

        [int]$TimeoutMs = 10000
    )

    $pipe = $null
    $writer = $null
    $reader = $null

    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, 'InOut')
        $pipe.Connect($TimeoutMs)

        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe)

        $json = $Command | ConvertTo-Json -Compress -Depth 10
        $writer.WriteLine($json)

        $response = $reader.ReadLine()

        if ($null -eq $response) {
            Write-Error "No response received from automation server."
            return $null
        }

        return $response | ConvertFrom-Json
    }
    catch {
        Write-Error "Automation command failed: $_"
        return $null
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $pipe) { $pipe.Dispose() }
    }
}

function Find-DriftBusterPipe {
    [CmdletBinding()]
    param()

    $pipes = Get-ChildItem "\\.\pipe\" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'DriftBuster-Automation-*' }

    if ($null -eq $pipes -or $pipes.Count -eq 0) {
        Write-Warning "No DriftBuster automation pipes found. Is the GUI running with DRIFTBUSTER_AUTOMATION=1?"
        return $null
    }

    foreach ($p in $pipes) {
        Write-Host "Found pipe: $($p.Name)"
    }

    return $pipes[0].Name
}
