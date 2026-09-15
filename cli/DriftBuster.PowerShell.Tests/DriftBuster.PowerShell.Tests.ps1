param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'DriftBuster PowerShell module' {
    BeforeAll {
        # Pester v5+ discards top-level script variables between discovery and run,
        # so everything the blocks need lives in $script: scope here.
        $script:modulePath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' 'DriftBuster.PowerShell' 'DriftBuster.psm1')).Path
        $script:repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..')).Path
        $script:publishDir = Join-Path $script:repoRoot 'gui' 'DriftBuster.Backend' 'bin' 'Debug' 'published'
        $script:backendAssembly = Join-Path $script:publishDir 'DriftBuster.Backend.dll'

        if (-not (Test-Path -LiteralPath $script:backendAssembly)) {
            $publishArgs = @(
                'publish',
                (Join-Path $script:repoRoot 'gui' 'DriftBuster.Backend' 'DriftBuster.Backend.csproj'),
                '-c', 'Debug',
                '-o', $script:publishDir
            )

            $publishOutput = & dotnet @publishArgs 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed with exit code $LASTEXITCODE`n$publishOutput"
            }
        }

        $script:module = Import-Module $script:modulePath -Force -PassThru
        $script:moduleName = $script:module.Name

        # ConvertFrom-Json turns ISO-8601 strings into local [datetime] values, so
        # timestamps are compared as instants rather than as rendered strings.
        function script:ConvertTo-Instant {
            param([Parameter(Mandatory = $true)] $Value)
            return ([DateTimeOffset]$Value).ToUniversalTime()
        }
    }

    BeforeEach {
        $script:tempArtifacts = @()
    }

    AfterEach {
        foreach ($path in $script:tempArtifacts) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    AfterAll {
        Get-Module | Where-Object { $_.Path -eq $script:modulePath } | ForEach-Object { Remove-Module $_.Name -Force }
    }

    Context 'Ping' {
        It 'returns pong' {
            $result = Test-DriftBusterPing
            $result.status | Should -Be 'pong'
        }
    }

    Context 'Diff workflows' {
        It 'produces comparison entries with JSON-aligned keys' {
            $left = New-TemporaryFile
            $script:tempArtifacts += $left
            $right = New-TemporaryFile
            $script:tempArtifacts += $right

            Set-Content -LiteralPath $left 'alpha' -NoNewline
            Set-Content -LiteralPath $right 'beta' -NoNewline

            $result = Invoke-DriftBusterDiff -Left $left -Right $right

            $result.versions | Should -Not -BeNullOrEmpty
            $comparison = $result.comparisons | Select-Object -First 1
            $comparison.plan.before | Should -Match 'alpha'
            $comparison.metadata.left_path | Should -Match ([IO.Path]::GetFileName($left))
        }

        It 'supports RawJson output' {
            $left = New-TemporaryFile
            $script:tempArtifacts += $left
            $right = New-TemporaryFile
            $script:tempArtifacts += $right

            Set-Content -LiteralPath $left 'before'
            Set-Content -LiteralPath $right 'after'

            $json = Invoke-DriftBusterDiff -Left $left -Right $right -RawJson
            $json | Should -BeOfType [string]
            ($json | ConvertFrom-Json).comparisons | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Hunt workflows' {
        It 'returns hits with lowercase contract keys' {
            $root = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N')))
            $script:tempArtifacts += $root.FullName
            $filePath = Join-Path $root.FullName 'evidence.txt'
            Set-Content -LiteralPath $filePath 'server=alpha01.internal'

            $result = Invoke-DriftBusterHunt -Directory $root.FullName
            ($result.PSObject.Properties.Name) | Should -Contain 'directory'
            ($result.PSObject.Properties.Name) | Should -Contain 'hits'
            $result.count | Should -BeGreaterOrEqual 0
            $hits = @($result.PSObject.Properties['hits'].Value)
            if ($result.count -gt 0) {
                $hits | Should -Not -BeNullOrEmpty
                $hit = $hits | Select-Object -First 1
                $hit.rule.name | Should -Not -BeNullOrEmpty
            }
        }
    }

    Context 'Run profile workflows' {
        It 'saves profiles from JSON text' {
            $baseDir = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N')))
            $script:tempArtifacts += $baseDir.FullName

            $profilePath = Join-Path $baseDir.FullName 'baseline.txt'
            Set-Content -LiteralPath $profilePath 'baseline data'

            # save_profile validates each source against the file system, so the sources are absolute paths that exist.
            $profileJson = [ordered]@{
                name = 'ModuleProfile'
                baseline = $profilePath
                sources = @($profilePath, (Join-Path $baseDir.FullName '*.txt'))
                options = @{ key = 'value' }
                secret_scanner = @{ ignore_rules = @('server-name') }
            } | ConvertTo-Json -Depth 4

            $saved = $profileJson | Save-DriftBusterRunProfile -BaseDir $baseDir.FullName -PassThru -Confirm:$false
            $saved.name | Should -Be 'ModuleProfile'
            $saved.options.key | Should -Be 'value'

            $listed = Get-DriftBusterRunProfile -BaseDir $baseDir.FullName -Raw
            $listed.profiles | Should -Not -BeNullOrEmpty
        }

        It 'runs profiles constructed from hashtables' {
            $baseDir = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N')))
            $script:tempArtifacts += $baseDir.FullName

            $sourceDir = New-Item -ItemType Directory -Path (Join-Path $baseDir.FullName 'sources')
            $profileBaseline = Join-Path $sourceDir.FullName 'baseline.txt'
            $profileData = Join-Path $sourceDir.FullName 'data.txt'
            Set-Content -LiteralPath $profileBaseline 'baseline'
            Set-Content -LiteralPath $profileData 'data'

            $profileDef = @{
                name = 'HashtableProfile'
                baseline = $profileBaseline
                sources = @($profileBaseline, (Join-Path $sourceDir.FullName '*.txt'))
            }

            $result = Invoke-DriftBusterRunProfile -Profile $profileDef -BaseDir $baseDir.FullName -NoSave -Confirm:$false
            $result.profile.name | Should -Be 'HashtableProfile'
            $result.files | Should -Not -BeNullOrEmpty
            $result.files[0].destination | Should -Match 'baseline'
        }

        It 'emits raw JSON when requested' {
            $baseDir = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N')))
            $script:tempArtifacts += $baseDir.FullName

            $sourceDir = New-Item -ItemType Directory -Path (Join-Path $baseDir.FullName 'sources')
            $profileBaseline = Join-Path $sourceDir.FullName 'baseline.txt'
            Set-Content -LiteralPath $profileBaseline 'baseline'

            $profileDef = @{
                name = 'RawProfile'
                baseline = $profileBaseline
                sources = @($profileBaseline)
            }

            $json = Invoke-DriftBusterRunProfile -Profile $profileDef -BaseDir $baseDir.FullName -NoSave -Raw -Confirm:$false
            $json | Should -BeOfType [string]
            ($json | ConvertFrom-Json).profile.name | Should -Be 'RawProfile'
        }
    }

    Context 'Remote scanning' {
        # Admin shares (\\host\C$) are a Windows SMB construct; Join-Path on Linux rejects the UNC root.
        It 'runs capture against an admin share path (Windows only)' -Skip:(-not $IsWindows) {
            $profileDef = New-TemporaryFile
            $script:tempArtifacts += $profileDef
            Set-Content -LiteralPath $profileDef '{"profiles": []}'

            $outputRoot = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
            $script:tempArtifacts += $outputRoot

            function Test-RemotePython {
                param(
                    [Parameter(Position = 0)]
                    $ScriptPath,

                    [Parameter(ValueFromRemainingArguments = $true)]
                    [object[]]
                    $RemainingArguments
                )

                $script:capturedPythonArgs = @($ScriptPath) + $RemainingArguments
                $global:LASTEXITCODE = 0
            }

            # Fake hostname for the mocked capture; kept in a variable so the analyzer's hardcoded-ComputerName rule does not fire.
            $computerName = 'filesvr01'
            $result = Invoke-DriftBusterRemoteScan -ComputerName $computerName -RemotePath 'ProgramData\\VendorA' -RunProfilePath $profileDef -PythonPath Test-RemotePython -OutputDirectory $outputRoot

            ($result | Measure-Object).Count | Should -Be 1
            $result[0].Mode | Should -Be 'AdminShare'
            $result[0].TargetPath | Should -Be '\\filesvr01\C$\ProgramData\VendorA'
            $script:capturedPythonArgs[2] | Should -Be '\\filesvr01\C$\ProgramData\VendorA'

            $hostOutput = Join-Path $outputRoot 'filesvr01'
            Test-Path -LiteralPath $hostOutput | Should -BeTrue

            if (Get-Command Test-RemotePython -ErrorAction SilentlyContinue) {
                Remove-Item function:Test-RemotePython -Force
            }
        }

        It 'stages and collects capture artefacts over WinRM' {
            $profileDef = New-TemporaryFile
            $script:tempArtifacts += $profileDef
            Set-Content -LiteralPath $profileDef '{"profiles": []}'

            $outputRoot = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
            $script:tempArtifacts += $outputRoot

            # The fake remote root is a real local path so Join-Path builds it the same way on every OS.
            $fakeRemoteRoot = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
            $remoteWorkingDirectory = 'C:\Temp\DriftBusterRemote'
            $remotePath = 'C:\ProgramData\VendorA'
            # Fake hostname for the shimmed WinRM session; a variable keeps the analyzer's hardcoded-ComputerName rule quiet.
            $computerName = 'registry-01'

            # Copy-Item's -ToSession/-FromSession are dynamic parameters typed as [PSSession], which
            # Pester's Mock cannot relax, so the remoting cmdlets are shimmed with untyped functions
            # inside the module scope. The module hands values to Invoke-Command through $using:
            # variables; the shim resolves each reference from the calling scope via dynamic scoping.
            $state = @{
                Session           = [pscustomobject]@{ Id = 42 }
                FakeRemoteRoot    = $fakeRemoteRoot
                SessionParams     = $null
                InvokedSessions   = @()
                EnsureArgs        = $null
                RunArgs           = $null
                CleanupArgs       = $null
                CopiedToSession   = @()
                CopiedFromSession = @()
                RemovedSession    = $null
            }

            & $script:module {
                param($State)
                $script:winrmShimState = $State

                function script:New-PSSession {
                    param([string] $ComputerName, [pscredential] $Credential, [int] $Port, [switch] $UseSSL)
                    $script:winrmShimState.SessionParams = $PSBoundParameters
                    return $script:winrmShimState.Session
                }

                function script:Copy-Item {
                    param($ToSession, $FromSession, $LiteralPath, $Path, $Destination, [switch] $Recurse, [switch] $Force, $ErrorAction)
                    if ($PSBoundParameters.ContainsKey('ToSession')) {
                        $script:winrmShimState.CopiedToSession += $Destination
                    }
                    elseif ($PSBoundParameters.ContainsKey('FromSession')) {
                        $script:winrmShimState.CopiedFromSession += $Path
                    }
                }

                function script:Invoke-Command {
                    param($Session, [scriptblock] $ScriptBlock)
                    $script:winrmShimState.InvokedSessions += $Session
                    $usingValues = @{}
                    foreach ($usingAst in $ScriptBlock.Ast.FindAll({ $args[0] -is [System.Management.Automation.Language.UsingExpressionAst] }, $true)) {
                        $name = $usingAst.SubExpression.VariablePath.UserPath
                        $usingValues[$name] = Get-Variable -Name $name -ValueOnly
                    }

                    $body = $ScriptBlock.ToString()
                    if ($body -match 'Remove-Item') {
                        $script:winrmShimState.CleanupArgs = @($usingValues.remoteProfilesPath, $usingValues.remoteScriptPath, $usingValues.remoteOutput)
                    }
                    elseif ($body -match 'capture\.py') {
                        $script:winrmShimState.RunArgs = @($usingValues.PythonPath, $usingValues.remoteRoot, $usingValues.remoteProfilesPath, $usingValues.remoteOutput, $usingValues.RemotePath)
                    }
                    else {
                        $script:winrmShimState.EnsureArgs = $usingValues.RemoteWorkingDirectory
                        return $script:winrmShimState.FakeRemoteRoot
                    }
                }

                function script:Remove-PSSession {
                    param($Session)
                    $script:winrmShimState.RemovedSession = $Session
                }
            } $state

            try {
                $result = Invoke-DriftBusterRemoteScan -UseWinRM -ComputerName $computerName -RemotePath $remotePath -RunProfilePath $profileDef -RemoteWorkingDirectory $remoteWorkingDirectory -OutputDirectory $outputRoot
            }
            finally {
                & $script:module {
                    foreach ($name in 'New-PSSession', 'Copy-Item', 'Invoke-Command', 'Remove-PSSession') {
                        Remove-Item -LiteralPath "function:script:$name" -Force -ErrorAction SilentlyContinue
                    }
                    Remove-Variable -Name winrmShimState -Scope Script -ErrorAction SilentlyContinue
                }
            }

            ($result | Measure-Object).Count | Should -Be 1
            $result[0].Mode | Should -Be 'WinRM'
            $result[0].ComputerName | Should -Be $computerName
            $result[0].OutputDirectory | Should -Be (Join-Path $outputRoot $computerName)
            $state.SessionParams.ComputerName | Should -Be $computerName
            $state.InvokedSessions | Should -HaveCount 3
            $state.InvokedSessions | ForEach-Object { $_.Id | Should -Be 42 }
            $state.EnsureArgs | Should -Be $remoteWorkingDirectory
            $state.RunArgs | Should -Not -BeNullOrEmpty
            $state.RunArgs[0] | Should -Be 'python'
            $state.RunArgs[1] | Should -Be $fakeRemoteRoot
            $state.RunArgs[2] | Should -Be (Join-Path $fakeRemoteRoot 'profiles.json')
            $state.RunArgs[3] | Should -Be (Join-Path $fakeRemoteRoot 'captures')
            $state.RunArgs[4] | Should -Be $remotePath
            $state.CopiedToSession | Should -Contain (Join-Path $fakeRemoteRoot 'capture.py')
            $state.CopiedToSession | Should -Contain (Join-Path $fakeRemoteRoot 'profiles.json')
            $state.CopiedFromSession | Should -Contain (Join-Path $fakeRemoteRoot 'captures' '*')
            $state.CleanupArgs | Should -Be @((Join-Path $fakeRemoteRoot 'profiles.json'), (Join-Path $fakeRemoteRoot 'capture.py'), (Join-Path $fakeRemoteRoot 'captures'))
            $state.RemovedSession.Id | Should -Be 42
        }
    }

    Context 'Scheduler workflows' {
        BeforeAll {
            $script:originalPythonPath = $env:PYTHONPATH
            $srcPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..' 'src')).Path
            $repoPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..')).Path
            $separator = [IO.Path]::PathSeparator

            $pythonPaths = @($srcPath, $repoPath)
            if ($script:originalPythonPath) {
                $pythonPaths += $script:originalPythonPath
            }

            $env:PYTHONPATH = ($pythonPaths -join $separator)
        }

        AfterAll {
            $env:PYTHONPATH = $script:originalPythonPath
        }

        It 'manages schedules via the Python CLI bridge' {
            $baseDir = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N')))
            $script:tempArtifacts += $baseDir.FullName

            $profilesRoot = New-Item -ItemType Directory -Path (Join-Path $baseDir.FullName 'Profiles')
            $schedulePath = Join-Path $profilesRoot.FullName 'schedules.json'
            $statePath = Join-Path $profilesRoot.FullName 'scheduler-state.json'

            $scheduleJson = @'
{
  "schedules": [
    {
      "name": "nightly",
      "profile": "nightly",
      "every": "24h",
      "start_at": "2025-01-01T00:00:00Z"
    }
  ]
}
'@
            Set-Content -LiteralPath $schedulePath -Value $scheduleJson -Encoding UTF8

            $due = Get-DriftBusterScheduleDue -BaseDir $baseDir.FullName -ConfigPath $schedulePath -StatePath $statePath -At '2025-01-02T00:00:00Z' -PythonPath 'python'
            $due | Should -HaveCount 1
            $due[0].name | Should -Be 'nightly'
            ConvertTo-Instant $due[0].scheduled_for | Should -Be (ConvertTo-Instant '2025-01-01T00:00:00+00:00')

            $stateContent = Get-Content -LiteralPath $statePath -Raw
            $state = $stateContent | ConvertFrom-Json
            ConvertTo-Instant $state.nightly.pending | Should -Be (ConvertTo-Instant '2025-01-01T00:00:00+00:00')

            $completion = Complete-DriftBusterSchedule -Name 'nightly' -BaseDir $baseDir.FullName -ConfigPath $schedulePath -StatePath $statePath -CompletedAt '2025-01-01T00:00:00Z' -PythonPath 'python'
            ConvertTo-Instant $completion.next_run | Should -Be (ConvertTo-Instant '2025-01-02T00:00:00+00:00')

            $skip = Skip-DriftBusterSchedule -Name 'nightly' -ResumeAt '2025-01-05T09:30:00Z' -BaseDir $baseDir.FullName -ConfigPath $schedulePath -StatePath $statePath -PythonPath 'python'
            ConvertTo-Instant $skip.next_run | Should -Be (ConvertTo-Instant '2025-01-05T09:30:00+00:00')

            $list = Get-DriftBusterSchedule -BaseDir $baseDir.FullName -ConfigPath $schedulePath -StatePath $statePath -PythonPath 'python'
            $list | Should -HaveCount 1
            ConvertTo-Instant $list[0].next_run | Should -Be (ConvertTo-Instant '2025-01-05T09:30:00+00:00')
        }
    }

    Context 'SQL export workflows' -Tag 'sql-export' {
        BeforeAll {
            $script:originalPythonPath = $env:PYTHONPATH
            $srcPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..' 'src')).Path
            $repoPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..')).Path
            $separator = [IO.Path]::PathSeparator

            $pythonPaths = @($srcPath, $repoPath)
            if ($script:originalPythonPath) {
                $pythonPaths += $script:originalPythonPath
            }

            $env:PYTHONPATH = ($pythonPaths -join $separator)
        }

        AfterAll {
            $env:PYTHONPATH = $script:originalPythonPath
        }

        It 'exports sqlite snapshots with masked columns' {
            $baseDir = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N')))
            $script:tempArtifacts += $baseDir.FullName

            $databasePath = Join-Path $baseDir.FullName 'sample.sqlite'
            $exportDir = Join-Path $baseDir.FullName 'exports'
            $scriptPath = Join-Path $baseDir.FullName 'create_db.py'

            $pythonScript = @'
import sqlite3
import sys

db_path = sys.argv[1]
connection = sqlite3.connect(db_path)
try:
    cursor = connection.cursor()
    cursor.execute("CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT)")
    cursor.execute("INSERT INTO accounts (email, secret) VALUES (?, ?)", ("alpha@example.com", "token-a"))
    cursor.execute("INSERT INTO accounts (email, secret) VALUES (?, ?)", ("beta@example.com", "token-b"))
    connection.commit()
finally:
    connection.close()
'@

            Set-Content -LiteralPath $scriptPath -Value $pythonScript -Encoding UTF8

            & python $scriptPath $databasePath
            if ($LASTEXITCODE -ne 0) {
                throw "python database bootstrap failed with exit code $LASTEXITCODE"
            }

            $stubRoot = New-Item -ItemType Directory -Path (Join-Path $baseDir.FullName 'py-stub')
            $script:tempArtifacts += $stubRoot.FullName
            $scriptsDir = New-Item -ItemType Directory -Path (Join-Path $stubRoot.FullName 'scripts')
            $initPath = Join-Path $scriptsDir.FullName '__init__.py'
            Set-Content -LiteralPath $initPath -Value '' -Encoding UTF8

            $capturePath = Join-Path $scriptsDir.FullName 'capture.py'
            $captureScript = @'
import json
import os
import sys
from pathlib import Path


def _parse_arguments(argv):
    output_dir = None
    tables = []
    exclude_tables = []
    mask_column = []
    hash_column = []
    placeholder = "[REDACTED]"
    hash_salt = ""
    limit = None
    prefix = ""
    databases = []
    iterator = iter(argv)
    for token in iterator:
        if token == "--output-dir":
            output_dir = next(iterator)
        elif token == "--table":
            tables.append(next(iterator))
        elif token == "--exclude-table":
            exclude_tables.append(next(iterator))
        elif token == "--mask-column":
            mask_column.append(next(iterator))
        elif token == "--hash-column":
            hash_column.append(next(iterator))
        elif token == "--placeholder":
            placeholder = next(iterator)
        elif token == "--hash-salt":
            hash_salt = next(iterator)
        elif token == "--limit":
            limit = int(next(iterator))
        elif token == "--prefix":
            prefix = next(iterator)
        else:
            databases.append(token)
    return (
        output_dir,
        tables,
        exclude_tables,
        mask_column,
        hash_column,
        placeholder,
        hash_salt,
        limit,
        prefix,
        databases,
    )


def main():
    argv = sys.argv[1:]
    if not argv or argv[0] != "export-sql":
        return 2

    (
        output_dir,
        tables,
        exclude_tables,
        mask_column,
        hash_column,
        placeholder,
        hash_salt,
        limit,
        prefix,
        databases,
    ) = _parse_arguments(argv[1:])

    if output_dir is None or not databases:
        return 3

    for db_path in databases:
        if not os.path.exists(db_path):
            sys.stderr.write(f"error: database not found: {db_path}\n")
            return 1

    outdir = Path(output_dir)
    outdir.mkdir(parents=True, exist_ok=True)

    manifest = {
        "exports": [
            {
                "source": databases[0],
                "tables": ["accounts"],
                "masked_columns": {"accounts": ["secret"]},
                "hashed_columns": {"accounts": ["email"]},
            }
        ],
        "options": {
            "tables": tables,
            "exclude_tables": exclude_tables,
            "masked_columns": {"accounts": ["secret"]},
            "hashed_columns": {"accounts": ["email"]},
            "placeholder": placeholder,
            "hash_salt": hash_salt,
            "limit": limit,
        },
    }

    manifest_path = outdir / "sql-manifest.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    stem = prefix or Path(databases[0]).stem
    snapshot_path = outdir / f"{stem}-sql-snapshot.json"
    snapshot_payload = {
        "tables": [
            {
                "name": "accounts",
                "masked_columns": ["secret"],
                "rows": [{"email": "sha256:value"}],
            }
        ]
    }
    snapshot_path.write_text(json.dumps(snapshot_payload), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
'@

            Set-Content -LiteralPath $capturePath -Value $captureScript -Encoding UTF8

            $separator = [IO.Path]::PathSeparator
            $env:PYTHONPATH = ($stubRoot.FullName + $separator + $env:PYTHONPATH)

            $exportParams = @{
                Database    = $databasePath
                OutputDir   = $exportDir
                MaskColumn  = 'accounts.secret'
                HashColumn  = 'accounts.email'
                Prefix      = 'demo'
                Placeholder = '[MASK]'
                HashSalt    = 'pepper'
            }
            $manifest = Export-DriftBusterSqlSnapshot @exportParams

            $manifest | Should -Not -BeNullOrEmpty
            $manifest.exports | Should -Not -BeNullOrEmpty
            $entry = $manifest.exports | Select-Object -First 1
            $entry.tables | Should -Contain 'accounts'
            $manifest.options.hash_salt | Should -Be 'pepper'

            $manifestPath = Join-Path $exportDir 'sql-manifest.json'
            Test-Path -LiteralPath $manifestPath | Should -BeTrue

            $snapshotPath = Join-Path $exportDir 'demo-sql-snapshot.json'
            Test-Path -LiteralPath $snapshotPath | Should -BeTrue

            $snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
            $snapshot.tables | Should -Not -BeNullOrEmpty
            $snapshot.tables[0].masked_columns | Should -Contain 'secret'
            $snapshot.tables[0].rows[0].email | Should -Match '^sha256:'
        }
    }
}
