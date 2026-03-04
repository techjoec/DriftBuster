# DriftBuster GUI Automation Helper
# Sends JSON commands to the DriftBuster automation named pipe.
# Usage:
#   . C:\DriftBusterTest\vm-automation.ps1
#   Send-DriftBusterCommand 'DriftBuster-Automation-1234' @{cmd='ping'}
#   Send-DriftBusterCommand 'DriftBuster-Automation-1234' @{cmd='navigate'; tab='multi-server'}
#   Send-DriftBusterCommand 'DriftBuster-Automation-1234' @{cmd='run-all'}
#   Wait-DriftBusterIdle 'DriftBuster-Automation-1234' -TimeoutSeconds 120
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

function Wait-DriftBusterIdle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PipeName,

        [int]$TimeoutSeconds = 120,

        [int]$PollIntervalMs = 500
    )

    if ($TimeoutSeconds -le 0) {
        Write-Error "TimeoutSeconds must be greater than zero."
        return $null
    }

    if ($PollIntervalMs -le 0) {
        Write-Error "PollIntervalMs must be greater than zero."
        return $null
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $stateResponse = Send-DriftBusterCommand -PipeName $PipeName -Command @{ cmd = 'get-state' }
        if ($null -eq $stateResponse) {
            Start-Sleep -Milliseconds $PollIntervalMs
            continue
        }

        if (-not $stateResponse.ok) {
            Write-Error "get-state failed while waiting for idle: $($stateResponse.error)"
            return $null
        }

        $state = $stateResponse.data
        if ($null -eq $state -or -not ($state.PSObject.Properties.Name -contains 'isBusy')) {
            Write-Error "State response does not include 'isBusy'. Navigate to multi-server before waiting for idle."
            return $null
        }

        if (-not [bool]$state.isBusy) {
            return $stateResponse
        }

        Start-Sleep -Milliseconds $PollIntervalMs
    }

    Write-Error "Timed out after $TimeoutSeconds seconds waiting for DriftBuster to become idle."
    return $null
}
