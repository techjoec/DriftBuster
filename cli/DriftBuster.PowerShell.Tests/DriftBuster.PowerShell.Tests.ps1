param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'DriftBuster PowerShell module' {
    BeforeAll {
        # Pester v5+ discards top-level script variables between discovery and run,
        # so everything the blocks need lives in $script: scope here.
        $script:moduleRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' 'DriftBuster.PowerShell')).Path
        $script:manifestPath = Join-Path $script:moduleRoot 'DriftBuster.psd1'
        $script:repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..')).Path
        $script:sampleDatabase = Join-Path $script:repoRoot 'fixtures' 'sql' 'sample.sqlite'

        # Build the backend every run (incrementally): the module loads the newest assembly under gui/DriftBuster.Backend/bin, so the
        # suite always exercises the code in the working tree.
        $buildArgs = @(
            'build',
            (Join-Path $script:repoRoot 'gui' 'DriftBuster.Backend' 'DriftBuster.Backend.csproj'),
            '-c', 'Debug'
        )

        $buildOutput = & dotnet @buildArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE`n$buildOutput"
        }

        $script:tempArtifacts = [System.Collections.Generic.List[string]]::new()

        function script:New-TestDirectory {
            $path = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path $path
            $script:tempArtifacts.Add($path)
            return $path
        }

        # The module caches the backend under the data root; the suite keeps that cache out of the real profile.
        $script:originalDataRoot = $env:DRIFTBUSTER_DATA_ROOT
        $env:DRIFTBUSTER_DATA_ROOT = New-TestDirectory

        $script:module = Import-Module $script:manifestPath -Force -PassThru
        $script:moduleName = $script:module.Name

        # ConvertFrom-Json turns ISO-8601 strings into local [datetime] values, so
        # timestamps are compared as instants rather than as rendered strings.
        function script:ConvertTo-Instant {
            param([Parameter(Mandatory = $true)] $Value)
            return ([DateTimeOffset]$Value).ToUniversalTime()
        }
    }

    AfterAll {
        Get-Module | Where-Object { $_.Path -like "$script:moduleRoot*" } | ForEach-Object { Remove-Module $_.Name -Force }
        foreach ($path in $script:tempArtifacts) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        $env:DRIFTBUSTER_DATA_ROOT = $script:originalDataRoot
    }

    Context 'Module manifest' {
        It 'exports exactly the functions the manifest lists' {
            $manifest = Import-PowerShellDataFile -Path $script:manifestPath
            $exported = @($script:module.ExportedFunctions.Keys | Sort-Object)
            $exported | Should -Be @($manifest.FunctionsToExport | Sort-Object)
        }

        It 'requires PowerShell 7.6' {
            $manifest = Import-PowerShellDataFile -Path $script:manifestPath
            $manifest.PowerShellVersion | Should -Be '7.6'
        }

        It 'pins the backend version to the core version' {
            $manifest = Import-PowerShellDataFile -Path $script:manifestPath
            $versions = Get-Content -LiteralPath (Join-Path $script:repoRoot 'versions.json') -Raw | ConvertFrom-Json
            $manifest.PrivateData.BackendVersion | Should -Not -BeNullOrEmpty
            $manifest.PrivateData.BackendVersion | Should -Be $versions.core
        }

        It 'gives every exported function comment-based help' {
            foreach ($name in $script:module.ExportedFunctions.Keys) {
                $help = Get-Help -Name "$script:moduleName\$name" -Full
                $help.Synopsis | Should -Not -BeNullOrEmpty -Because $name
                $help.Synopsis.Trim() | Should -Not -BeLike "$name *" -Because "$name needs a .SYNOPSIS"
            }
        }

        It 'surfaces DriftBusterBackendMissing when no backend assembly can be found' {
            $copyRoot = Join-Path (New-TestDirectory) 'module'
            $null = New-Item -ItemType Directory -Path $copyRoot
            Get-ChildItem -LiteralPath $script:moduleRoot -File | Where-Object Name -ne 'DriftBuster.Backend.dll' |
                Copy-Item -Destination $copyRoot
            $copiedManifest = (Join-Path $copyRoot 'DriftBuster.psd1').Replace("'", "''")

            $command = "`$PSStyle.OutputRendering = 'PlainText'; `$ErrorView = 'NormalView'; Import-Module '$copiedManifest'"
            $output = & pwsh -NoProfile -NonInteractive -Command $command 2>&1
            $exitCode = $LASTEXITCODE
            $text = ($output | Out-String) -replace "`e\[[0-9;]*m", ''

            $exitCode | Should -Not -Be 0
            $text | Should -Match 'DriftBusterBackendMissing|Unable to load DriftBuster\.Backend\.dll for the PowerShell module\.'
            $text | Should -BeLike '*dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj*'
        }
    }

    Context 'Ping' {
        It 'returns pong' {
            $result = Test-DriftBusterPing
            $result.status | Should -Be 'pong'
        }
    }

    Context 'Diff workflows' {
        It 'round trips a left/right pair' {
            $root = New-TestDirectory
            $left = Join-Path $root 'left.txt'
            $right = Join-Path $root 'right.txt'
            Set-Content -LiteralPath $left 'alpha' -NoNewline
            Set-Content -LiteralPath $right 'beta' -NoNewline

            $result = Invoke-DriftBusterDiff -Left $left -Right $right

            $result.versions | Should -Not -BeNullOrEmpty
            $comparison = $result.comparisons | Select-Object -First 1
            $comparison.plan.before | Should -BeLike 'alpha*'
            $comparison.plan.after | Should -BeLike 'beta*'
            $comparison.metadata.left_path | Should -Match 'left\.txt'
        }

        It 'supports RawJson output' {
            $root = New-TestDirectory
            $left = Join-Path $root 'left.txt'
            $right = Join-Path $root 'right.txt'
            Set-Content -LiteralPath $left 'before'
            Set-Content -LiteralPath $right 'after'

            $json = Invoke-DriftBusterDiff -Left $left -Right $right -RawJson
            $json | Should -BeOfType [string]
            ($json | ConvertFrom-Json).comparisons | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Hunt workflows' {
        It 'returns hits with lowercase contract keys' {
            $root = New-TestDirectory
            Set-Content -LiteralPath (Join-Path $root 'evidence.txt') 'server=alpha01.internal'

            $result = Invoke-DriftBusterHunt -Directory $root
            ($result.PSObject.Properties.Name) | Should -Contain 'directory'
            ($result.PSObject.Properties.Name) | Should -Contain 'hits'
            $result.count | Should -BeGreaterOrEqual 0
            $hits = @($result.PSObject.Properties['hits'].Value)
            if ($result.count -gt 0) {
                $hits | Should -Not -BeNullOrEmpty
                ($hits | Select-Object -First 1).rule.name | Should -Not -BeNullOrEmpty
            }
        }
    }

    Context 'Run profile workflows' {
        It 'saves profiles from JSON text' {
            $baseDir = New-TestDirectory
            $profilePath = Join-Path $baseDir 'baseline.txt'
            Set-Content -LiteralPath $profilePath 'baseline data'

            # save_profile validates each source against the file system, so the sources are absolute paths that exist.
            $profileJson = [ordered]@{
                name = 'ModuleProfile'
                baseline = $profilePath
                sources = @($profilePath, (Join-Path $baseDir '*.txt'))
                options = @{ key = 'value' }
                secret_scanner = @{ ignore_rules = @('server-name') }
            } | ConvertTo-Json -Depth 4

            $saved = $profileJson | Save-DriftBusterRunProfile -BaseDir $baseDir -PassThru -Confirm:$false
            $saved.name | Should -Be 'ModuleProfile'
            $saved.options.key | Should -Be 'value'

            $listed = Get-DriftBusterRunProfile -BaseDir $baseDir -Raw
            $listed.profiles | Should -Not -BeNullOrEmpty
        }

        It 'runs typed profiles and writes their artifacts' {
            $baseDir = New-TestDirectory
            $sourceDir = New-Item -ItemType Directory -Path (Join-Path $baseDir 'sources')
            $profileBaseline = Join-Path $sourceDir.FullName 'baseline.txt'
            Set-Content -LiteralPath $profileBaseline 'baseline'
            Set-Content -LiteralPath (Join-Path $sourceDir.FullName 'data.txt') 'data'

            $profileDef = [DriftBuster.Backend.Models.RunProfileDefinition]@{
                Name     = 'Profile One'
                Baseline = $profileBaseline
                Sources  = [DriftBuster.Backend.Models.RunProfileSource[]]@(
                    [DriftBuster.Backend.Models.RunProfileSource]@{ Path = $profileBaseline },
                    [DriftBuster.Backend.Models.RunProfileSource]@{ Path = (Join-Path $sourceDir.FullName '*.txt') })
            }

            $result = Invoke-DriftBusterRunProfile -Profile $profileDef -BaseDir $baseDir -Confirm:$false
            $result.files | Should -Not -BeNullOrEmpty
            @($result.files | Where-Object { $_.destination -like '*baseline.txt' }) | Should -Not -BeNullOrEmpty
            @($result.files | Where-Object { Test-Path -LiteralPath $_.destination }) | Should -HaveCount @($result.files).Count
        }

        It 'runs profiles constructed from hashtables' {
            $baseDir = New-TestDirectory
            $sourceDir = New-Item -ItemType Directory -Path (Join-Path $baseDir 'sources')
            $profileBaseline = Join-Path $sourceDir.FullName 'baseline.txt'
            Set-Content -LiteralPath $profileBaseline 'baseline'
            Set-Content -LiteralPath (Join-Path $sourceDir.FullName 'data.txt') 'data'

            $profileDef = @{
                name = 'HashtableProfile'
                baseline = $profileBaseline
                sources = @($profileBaseline, (Join-Path $sourceDir.FullName '*.txt'))
            }

            $result = Invoke-DriftBusterRunProfile -Profile $profileDef -BaseDir $baseDir -NoSave -Confirm:$false
            $result.profile.name | Should -Be 'HashtableProfile'
            $result.files | Should -Not -BeNullOrEmpty
            $result.files[0].destination | Should -Match 'baseline'
        }

        It 'emits raw JSON when requested' {
            $baseDir = New-TestDirectory
            $sourceDir = New-Item -ItemType Directory -Path (Join-Path $baseDir 'sources')
            $profileBaseline = Join-Path $sourceDir.FullName 'baseline.txt'
            Set-Content -LiteralPath $profileBaseline 'baseline'

            $profileDef = @{
                name = 'RawProfile'
                baseline = $profileBaseline
                sources = @($profileBaseline)
            }

            $json = Invoke-DriftBusterRunProfile -Profile $profileDef -BaseDir $baseDir -NoSave -Raw -Confirm:$false
            $json | Should -BeOfType [string]
            ($json | ConvertFrom-Json).profile.name | Should -Be 'RawProfile'
        }
    }

    Context 'SQL export workflows' -Tag 'sql-export' {
        It 'exports the sample database with masked and hashed columns' {
            $exportDir = Join-Path (New-TestDirectory) 'exports'

            $exportParams = @{
                Database    = $script:sampleDatabase
                OutputDir   = $exportDir
                MaskColumn  = 'accounts.secret'
                HashColumn  = 'accounts.email'
                Prefix      = 'demo'
                Placeholder = '[MASK]'
                HashSalt    = 'pepper'
            }
            $manifest = Export-DriftBusterSqlSnapshot @exportParams

            $entry = @($manifest.exports) | Select-Object -First 1
            $entry.output | Should -Be 'demo-sql-snapshot.json'
            $entry.dialect | Should -Be 'sqlite'
            $entry.tables | Should -Contain 'accounts'
            $entry.row_counts.accounts | Should -Be 2
            $entry.masked_columns.accounts | Should -Be @('secret')
            $entry.hashed_columns.accounts | Should -Be @('email')
            $manifest.options.hash_salt | Should -Be 'pepper'
            $manifest.options.placeholder | Should -Be '[MASK]'

            Test-Path -LiteralPath (Join-Path $exportDir 'sql-manifest.json') | Should -BeTrue
            $snapshot = Get-Content -LiteralPath (Join-Path $exportDir 'demo-sql-snapshot.json') -Raw | ConvertFrom-Json
            $table = @($snapshot.tables) | Where-Object name -eq 'accounts'
            $table.masked_columns | Should -Contain 'secret'
            $table.hashed_columns | Should -Contain 'email'
            foreach ($row in @($table.rows)) {
                $row.secret | Should -Be '[MASK]'
                $row.email | Should -Match '^sha256:[0-9a-f]{64}$'
            }
        }

        It 'resolves relative paths against the current location and honours Table and Limit' {
            $workDir = New-TestDirectory
            Copy-Item -LiteralPath $script:sampleDatabase -Destination (Join-Path $workDir 'copy.sqlite')

            Push-Location $workDir
            try {
                $manifest = Export-DriftBusterSqlSnapshot -Database 'copy.sqlite' -OutputDir 'out' -Table 'accounts' -Limit 1
            }
            finally {
                Pop-Location
            }

            $manifest.options.limit | Should -Be 1
            $manifest.options.tables | Should -Be @('accounts')
            $snapshot = Get-Content -LiteralPath (Join-Path $workDir 'out' 'copy-sql-snapshot.json') -Raw | ConvertFrom-Json
            @(@($snapshot.tables)[0].rows) | Should -HaveCount 1
        }
    }

    Context 'Scheduler workflows' {
        BeforeEach {
            $script:scheduleBase = New-TestDirectory
            $profilesRoot = New-Item -ItemType Directory -Path (Join-Path $script:scheduleBase 'Profiles')
            $script:schedulePath = Join-Path $profilesRoot.FullName 'schedules.json'
            $script:statePath = Join-Path $script:scheduleBase 'state' 'scheduler-state.json'

            $scheduleJson = @'
{
  "schedules": [
    {
      "name": "nightly",
      "profile": "nightly",
      "every": "24h",
      "start_at": "2025-01-01T00:00:00Z",
      "tags": ["prod"],
      "metadata": {"owner": "ops"}
    }
  ]
}
'@
            Set-Content -LiteralPath $script:schedulePath -Value $scheduleJson -Encoding UTF8
            $script:schedulePaths = @{
                BaseDir    = $script:scheduleBase
                ConfigPath = $script:schedulePath
                StatePath  = $script:statePath
            }
        }

        It 'lists schedules with their state' {
            $schedulePaths = $script:schedulePaths
            $list = @(Get-DriftBusterSchedule @schedulePaths)
            $list | Should -HaveCount 1
            $list[0].name | Should -Be 'nightly'
            $list[0].profile | Should -Be 'nightly'
            $list[0].interval_seconds | Should -Be 86400
            $list[0].tags | Should -Be @('prod')
            $list[0].metadata.owner | Should -Be 'ops'
            ConvertTo-Instant $list[0].start_at | Should -Be (ConvertTo-Instant '2025-01-01T00:00:00+00:00')
            ($list[0].PSObject.Properties.Name) | Should -Contain 'pending'
            $list[0].window | Should -BeNullOrEmpty

            $raw = Get-DriftBusterSchedule @schedulePaths -Raw
            $raw | Should -BeOfType [string]
            $raw | Should -BeLike '*"start_at": "2025-01-01T00:00:00+00:00"*'
            @(($raw | ConvertFrom-Json).schedules) | Should -HaveCount 1
        }

        It 'records due runs as pending in the state file' {
            $schedulePaths = $script:schedulePaths
            $due = @(Get-DriftBusterScheduleDue @schedulePaths -At '2025-01-02T00:00:00Z')
            $due | Should -HaveCount 1
            $due[0].name | Should -Be 'nightly'
            $due[0].tags | Should -Be @('prod')
            ConvertTo-Instant $due[0].scheduled_for | Should -Be (ConvertTo-Instant '2025-01-01T00:00:00+00:00')

            $state = Get-Content -LiteralPath $script:statePath -Raw | ConvertFrom-Json
            ConvertTo-Instant $state.nightly.pending | Should -Be (ConvertTo-Instant '2025-01-01T00:00:00+00:00')

            @(Get-DriftBusterScheduleDue @schedulePaths -At '2024-12-31T00:00:00Z') | Should -HaveCount 0
        }

        It 'completes a pending run' {
            $schedulePaths = $script:schedulePaths
            $null = Get-DriftBusterScheduleDue @schedulePaths -At '2025-01-02T00:00:00Z'

            $completion = Complete-DriftBusterSchedule -Name 'nightly' @schedulePaths -CompletedAt '2025-01-01T00:00:00Z'
            $completion.name | Should -Be 'nightly'
            ConvertTo-Instant $completion.next_run | Should -Be (ConvertTo-Instant '2025-01-02T00:00:00+00:00')
            $completion.pending | Should -BeNullOrEmpty

            $state = Get-Content -LiteralPath $script:statePath -Raw | ConvertFrom-Json
            ConvertTo-Instant $state.nightly.next_run | Should -Be (ConvertTo-Instant '2025-01-02T00:00:00+00:00')
        }

        It 'skips a schedule until the resume time' {
            $schedulePaths = $script:schedulePaths
            $skip = Skip-DriftBusterSchedule -Name 'nightly' -ResumeAt '2025-01-05T09:30:00Z' @schedulePaths
            ConvertTo-Instant $skip.next_run | Should -Be (ConvertTo-Instant '2025-01-05T09:30:00+00:00')

            $raw = Skip-DriftBusterSchedule -Name 'nightly' -ResumeAt '2025-01-06T00:00:00Z' @schedulePaths -Raw
            $raw | Should -BeLike '*"next_run": "2025-01-06T00:00:00+00:00"*'

            $list = @(Get-DriftBusterSchedule @schedulePaths)
            ConvertTo-Instant $list[0].next_run | Should -Be (ConvertTo-Instant '2025-01-06T00:00:00+00:00')
        }

        It 'reads the default manifest under the base directory' {
            $list = @(Get-DriftBusterSchedule -BaseDir $script:scheduleBase)
            $list | Should -HaveCount 1
        }
    }

    Context 'Remote scanning over admin shares' {
        BeforeEach {
            $script:captureTree = New-TestDirectory
            $appDir = New-Item -ItemType Directory -Path (Join-Path $script:captureTree 'VendorA')
            Set-Content -LiteralPath (Join-Path $appDir.FullName 'appsettings.json') '{"ConnectionString": "Server=db01"}'
            Set-Content -LiteralPath (Join-Path $appDir.FullName 'settings.ini') "[main]`nhost=alpha01.internal"
            $script:outputRoot = Join-Path (New-TestDirectory) 'captures'
        }

        It 'builds admin share UNC paths from drive, relative and UNC inputs' {
            InModuleScope $script:moduleName {
                # Fake hostname kept in a variable so the analyzer's hardcoded-ComputerName rule does not fire.
                $computerName = 'filesvr01'
                Get-DriftBusterAdminShareTargetPath -ComputerName $computerName -Share 'C$' -Path 'ProgramData\\VendorA' | Should -Be '\\filesvr01\C$\ProgramData\VendorA'
                Get-DriftBusterAdminShareTargetPath -ComputerName $computerName -Share 'C$' -Path 'C:\ProgramData\VendorA\' | Should -Be '\\filesvr01\C$\ProgramData\VendorA'
                Get-DriftBusterAdminShareTargetPath -ComputerName $computerName -Share 'D$' -Path 'C:\' | Should -Be '\\filesvr01\D$'
                Get-DriftBusterAdminShareTargetPath -ComputerName $computerName -Share 'C$' -Path '\\other\share\dir' | Should -Be '\\other\share\dir'
            }
        }

        It 'captures the share path in process and writes the snapshot and manifest per host' {
            # Admin shares only exist on Windows; the UNC target is redirected to a local tree so the in-process capture runs anywhere.
            Mock Get-DriftBusterAdminShareTargetPath -ModuleName $script:moduleName { $script:captureTree }
            $profiles = Join-Path (New-TestDirectory) 'profiles.json'
            Set-Content -LiteralPath $profiles '{"profiles": []}'
            $computerName = 'filesvr01'

            $scanParams = @{
                ComputerName    = $computerName
                RemotePath      = 'C:\ProgramData'
                RunProfilePath  = $profiles
                Environment     = 'test'
                Reason          = 'pester'
                Operator        = 'pester-operator'
                MaskToken       = 'alpha01'
                OutputDirectory = $script:outputRoot
            }
            $result = @(Invoke-DriftBusterRemoteScan @scanParams -WarningVariable captureWarnings -WarningAction SilentlyContinue)

            $result | Should -HaveCount 1
            $result[0].ComputerName | Should -Be $computerName
            $result[0].Mode | Should -Be 'AdminShare'
            $result[0].TargetPath | Should -Be $script:captureTree
            $result[0].OutputDirectory | Should -Be (Join-Path $script:outputRoot $computerName)
            Should -Invoke Get-DriftBusterAdminShareTargetPath -ModuleName $script:moduleName -Times 1 -Exactly -ParameterFilter {
                $ComputerName -eq 'filesvr01' -and $Share -eq 'C$' -and $Path -eq 'C:\ProgramData'
            }

            Test-Path -LiteralPath $result[0].SnapshotPath | Should -BeTrue
            (Split-Path -Parent $result[0].SnapshotPath) | Should -Be $result[0].OutputDirectory
            $manifest = Get-Content -LiteralPath $result[0].ManifestPath -Raw | ConvertFrom-Json
            $manifest.capture.root | Should -Be $script:captureTree
            $manifest.capture.operator | Should -Be 'pester-operator'
            $manifest.capture.environment | Should -Be 'test'
            $manifest.capture.reason | Should -Be 'pester'
            $manifest.capture.snapshot_path | Should -Be (Split-Path -Leaf $result[0].SnapshotPath)
            $snapshot = Get-Content -LiteralPath $result[0].SnapshotPath -Raw | ConvertFrom-Json
            $snapshot.capture.mask_token_count | Should -Be 1
            # Nothing in the tree carries the token, so the capture's redaction warning reaches the warning stream.
            @($captureWarnings | ForEach-Object { "$_" }) | Should -Contain 'warning: redaction filter configured but no tokens were replaced'
        }
    }

    Context 'Remote scanning over WinRM' {
        BeforeEach {
            # Copy-Item's -ToSession/-FromSession are dynamic parameters typed [PSSession] that a mock cannot relax,
            # so the fake session is a real, never-connected PSSession instance.
            $script:fakeSession = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([System.Management.Automation.Runspaces.PSSession])
            $script:winrm = @{
                Calls          = [System.Collections.Generic.List[string]]::new()
                SessionParams  = $null
                StageArgs      = $null
                CaptureArgs    = $null
                CleanupArgs    = $null
                CopiedTo       = [System.Collections.Generic.List[object]]::new()
                CopiedFrom     = [System.Collections.Generic.List[object]]::new()
                FailCapture    = $false
            }
            $script:remote = [pscustomobject]@{
                StagingDirectory  = 'C:\ProgramData\DriftBuster\RemoteScan\stage01'
                ModuleDirectory   = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\DriftBuster'
                ModuleManifest    = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\DriftBuster\DriftBuster.psd1'
                RuntimesDirectory = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\DriftBuster\runtimes'
                RuntimeIdentifier = 'win-x64'
                ProfilesPath      = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\profiles.json'
                OutputDirectory   = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\captures'
                OutputItems       = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\captures\*'
            }
            $script:scripts = & $script:module {
                @{ Stage = $script:RemoteStageScript; Capture = $script:RemoteCaptureScript; Cleanup = $script:RemoteCleanupScript }
            }

            Mock New-PSSession -ModuleName $script:moduleName {
                $script:winrm.Calls.Add('New-PSSession')
                $script:winrm.SessionParams = $PesterBoundParameters
                return $script:fakeSession
            }

            Mock Invoke-Command -ModuleName $script:moduleName -RemoveParameterType Session {
                if (-not [object]::ReferenceEquals(@($Session)[0], $script:fakeSession)) { throw 'Invoke-Command received an unexpected session.' }
                if ([object]::ReferenceEquals($ScriptBlock, $script:scripts.Stage)) {
                    $script:winrm.Calls.Add('stage')
                    $script:winrm.StageArgs = $ArgumentList
                    return $script:remote
                }
                if ([object]::ReferenceEquals($ScriptBlock, $script:scripts.Capture)) {
                    $script:winrm.Calls.Add('capture')
                    $script:winrm.CaptureArgs = $ArgumentList
                    if ($script:winrm.FailCapture) { throw 'Capture of C:\ProgramData\VendorA failed with exit code 1' }
                    return [pscustomobject]@{
                        SnapshotPath = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\captures\20250101T000000Z-snapshot.json'
                        ManifestPath = 'C:\ProgramData\DriftBuster\RemoteScan\stage01\captures\20250101T000000Z-manifest.json'
                    }
                }
                if ([object]::ReferenceEquals($ScriptBlock, $script:scripts.Cleanup)) {
                    $script:winrm.Calls.Add('cleanup')
                    $script:winrm.CleanupArgs = $ArgumentList
                    return
                }
                throw "Unexpected remote script block: $ScriptBlock"
            }

            Mock Copy-Item -ModuleName $script:moduleName -ParameterFilter { $PesterBoundParameters.ContainsKey('ToSession') -or $PesterBoundParameters.ContainsKey('FromSession') } {
                if ($PesterBoundParameters.ContainsKey('ToSession')) {
                    $script:winrm.Calls.Add('copy-to')
                    $script:winrm.CopiedTo.Add([pscustomobject]@{ Source = $LiteralPath; Destination = $Destination; Recurse = $PesterBoundParameters.ContainsKey('Recurse') })
                }
                else {
                    $script:winrm.Calls.Add('copy-from')
                    $script:winrm.CopiedFrom.Add([pscustomobject]@{ Source = $Path; Destination = $Destination; Recurse = $PesterBoundParameters.ContainsKey('Recurse') })
                }
            }

            Mock Remove-PSSession -ModuleName $script:moduleName -RemoveParameterType Session {
                $script:winrm.Calls.Add('Remove-PSSession')
            }

            $script:outputRoot = Join-Path (New-TestDirectory) 'captures'
            $script:profiles = Join-Path (New-TestDirectory) 'profiles.json'
            Set-Content -LiteralPath $script:profiles '{"profiles": []}'
        }

        It 'stages the module and backend, runs the capture remotely, collects the artefacts and cleans up' {
            $computerName = 'registry-01'
            $scanParams = @{
                UseWinRM               = $true
                ComputerName           = $computerName
                RemotePath             = 'C:\ProgramData\VendorA'
                RunProfilePath         = $script:profiles
                Environment            = 'prod'
                Reason                 = 'audit'
                MaskToken              = 'hunter2'
                RemoteWorkingDirectory = 'C:\Temp\DriftBusterRemote'
                Port                   = 5986
                UseSSL                 = $true
                OutputDirectory        = $script:outputRoot
            }
            $result = @(Invoke-DriftBusterRemoteScan @scanParams)

            $expectedLocal = Join-Path $script:outputRoot $computerName
            $result | Should -HaveCount 1
            $result[0].Mode | Should -Be 'WinRM'
            $result[0].ComputerName | Should -Be $computerName
            $result[0].OutputDirectory | Should -Be $expectedLocal
            $result[0].SnapshotPath | Should -Be (Join-Path $expectedLocal '20250101T000000Z-snapshot.json')
            $result[0].ManifestPath | Should -Be (Join-Path $expectedLocal '20250101T000000Z-manifest.json')

            $calls = @($script:winrm.Calls)
            $calls[0] | Should -Be 'New-PSSession'
            $calls[1] | Should -Be 'stage'
            $calls[-4] | Should -Be 'capture'
            $calls[-3] | Should -Be 'copy-from'
            $calls[-2] | Should -Be 'cleanup'
            $calls[-1] | Should -Be 'Remove-PSSession'
            @($calls[2..($calls.Count - 5)] | Where-Object { $_ -ne 'copy-to' }) | Should -BeNullOrEmpty

            $script:winrm.SessionParams.ComputerName | Should -Be $computerName
            $script:winrm.SessionParams.ConfigurationName | Should -Be 'PowerShell.7'
            $script:winrm.SessionParams.Port | Should -Be 5986
            $script:winrm.SessionParams.UseSSL | Should -BeTrue
            @($script:winrm.StageArgs) | Should -Be @('C:\Temp\DriftBusterRemote')

            $moduleCopies = @($script:winrm.CopiedTo | Where-Object Destination -eq $script:remote.ModuleDirectory)
            $copiedNames = @($moduleCopies | ForEach-Object { Split-Path -Leaf $_.Source })
            $copiedNames | Should -Contain 'DriftBuster.psd1'
            $copiedNames | Should -Contain 'DriftBuster.psm1'
            $copiedNames | Should -Contain 'DriftBuster.Backend.dll'
            $copiedNames | Should -Contain 'Microsoft.Data.Sqlite.dll'
            $runtimeCopy = @($script:winrm.CopiedTo | Where-Object Destination -eq $script:remote.RuntimesDirectory)
            $runtimeCopy | Should -HaveCount 1
            $backendSource = & $script:module { $script:BackendSourceDirectory }
            $runtimeCopy[0].Source | Should -Be (Join-Path $backendSource 'runtimes' 'win-x64')
            $runtimeCopy[0].Recurse | Should -BeTrue
            $profileCopy = @($script:winrm.CopiedTo | Where-Object Destination -eq $script:remote.ProfilesPath)
            $profileCopy | Should -HaveCount 1
            $profileCopy[0].Source | Should -Be (Resolve-Path -LiteralPath $script:profiles).Path

            $script:winrm.CaptureArgs[0] | Should -Be $script:remote.ModuleManifest
            $captureParameters = $script:winrm.CaptureArgs[1]
            $captureParameters.Root | Should -Be 'C:\ProgramData\VendorA'
            $captureParameters.OutputDir | Should -Be $script:remote.OutputDirectory
            $captureParameters.ProfilesPath | Should -Be $script:remote.ProfilesPath
            $captureParameters.Environment | Should -Be 'prod'
            $captureParameters.Reason | Should -Be 'audit'
            $captureParameters.MaskToken | Should -Be @('hunter2')

            $script:winrm.CopiedFrom | Should -HaveCount 1
            $script:winrm.CopiedFrom[0].Source | Should -Be $script:remote.OutputItems
            $script:winrm.CopiedFrom[0].Destination | Should -Be $expectedLocal
            @($script:winrm.CleanupArgs) | Should -Be @($script:remote.StagingDirectory)
        }

        It 'cleans up the staged folder and closes the session when the remote capture fails' {
            $script:winrm.FailCapture = $true
            $computerName = 'registry-01'

            { Invoke-DriftBusterRemoteScan -UseWinRM -ComputerName $computerName -RemotePath 'C:\ProgramData\VendorA' -Environment 'prod' -Reason 'audit' -AllowUnmasked -OutputDirectory $script:outputRoot } |
                Should -Throw -ExpectedMessage '*failed with exit code 1*'

            $calls = @($script:winrm.Calls)
            $calls | Should -Not -Contain 'copy-from'
            $calls[-2] | Should -Be 'cleanup'
            $calls[-1] | Should -Be 'Remove-PSSession'
            @($script:winrm.CopiedTo | Where-Object Destination -eq $script:remote.ProfilesPath) | Should -BeNullOrEmpty
            $script:winrm.CaptureArgs[1].ContainsKey('ProfilesPath') | Should -BeFalse
        }

        It 'keeps the staged folder when KeepRemoteArtifacts is set' {
            $computerName = 'registry-01'
            $null = Invoke-DriftBusterRemoteScan -UseWinRM -KeepRemoteArtifacts -ComputerName $computerName -RemotePath 'C:\ProgramData\VendorA' -Environment 'prod' -Reason 'audit' -AllowUnmasked -OutputDirectory $script:outputRoot

            $script:winrm.Calls | Should -Not -Contain 'cleanup'
            @($script:winrm.Calls)[-1] | Should -Be 'Remove-PSSession'
        }
    }

    Context 'Remote scanning over a loopback WinRM transport' {
        BeforeEach {
            # The transport is replaced by local equivalents: remote script blocks run here, session copies are plain copies, and the
            # capture runs in a fresh pwsh so the staged module loads its backend exactly as a remote host would.
            $script:fakeSession = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([System.Management.Automation.Runspaces.PSSession])
            $script:scripts = & $script:module {
                @{ Stage = $script:RemoteStageScript; Capture = $script:RemoteCaptureScript }
            }
            $script:loopback = @{ Staging = $null; ChildDataRoot = New-TestDirectory }

            Mock New-PSSession -ModuleName $script:moduleName { $script:fakeSession }
            Mock Remove-PSSession -ModuleName $script:moduleName -RemoveParameterType Session { }

            Mock Copy-Item -ModuleName $script:moduleName -ParameterFilter { $PesterBoundParameters.ContainsKey('ToSession') -or $PesterBoundParameters.ContainsKey('FromSession') } {
                if ($PesterBoundParameters.ContainsKey('ToSession')) {
                    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Recurse:($PesterBoundParameters.ContainsKey('Recurse')) -Force
                }
                else {
                    Microsoft.PowerShell.Management\Copy-Item -Path $Path -Destination $Destination -Recurse:($PesterBoundParameters.ContainsKey('Recurse')) -Force
                }
            }

            Mock Invoke-Command -ModuleName $script:moduleName -RemoveParameterType Session {
                if (-not [object]::ReferenceEquals($ScriptBlock, $script:scripts.Capture)) {
                    $output = & $ScriptBlock @ArgumentList
                    if ([object]::ReferenceEquals($ScriptBlock, $script:scripts.Stage)) {
                        $script:loopback.Staging = $output.StagingDirectory
                    }
                    return $output
                }

                $work = New-TestDirectory
                Set-Content -LiteralPath (Join-Path $work 'capture.ps1') -Value $ScriptBlock.ToString()
                $ArgumentList | Export-Clixml -LiteralPath (Join-Path $work 'arguments.xml')
                $runner = @'
$ErrorActionPreference = 'Stop'
$arguments = @(Import-Clixml -LiteralPath (Join-Path $PSScriptRoot 'arguments.xml'))
$capture = [scriptblock]::Create((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'capture.ps1') -Raw))
& $capture $arguments[0] $arguments[1] | Export-Clixml -LiteralPath (Join-Path $PSScriptRoot 'result.xml')
'@
                Set-Content -LiteralPath (Join-Path $work 'runner.ps1') -Value $runner

                $originalDataRoot = $env:DRIFTBUSTER_DATA_ROOT
                $env:DRIFTBUSTER_DATA_ROOT = $script:loopback.ChildDataRoot
                try {
                    $childOutput = & pwsh -NoProfile -NonInteractive -File (Join-Path $work 'runner.ps1') 2>&1
                }
                finally {
                    $env:DRIFTBUSTER_DATA_ROOT = $originalDataRoot
                }
                if ($LASTEXITCODE -ne 0) {
                    throw "Staged capture failed: $($childOutput | Out-String)"
                }

                return Import-Clixml -LiteralPath (Join-Path $work 'result.xml')
            }
        }

        It 'stages a module that imports and captures on its own, then removes the staged folder' {
            $workingDirectory = New-TestDirectory
            $tree = New-TestDirectory
            Set-Content -LiteralPath (Join-Path $tree 'app.json') '{"name": "vendor"}'
            $outputRoot = Join-Path (New-TestDirectory) 'captures'
            $computerName = 'loopback-01'

            $scanParams = @{
                UseWinRM               = $true
                ComputerName           = $computerName
                RemotePath             = $tree
                Environment            = 'lab'
                Reason                 = 'loopback'
                Operator               = 'pester-operator'
                AllowUnmasked          = $true
                RemoteWorkingDirectory = $workingDirectory
                OutputDirectory        = $outputRoot
            }
            $result = @(Invoke-DriftBusterRemoteScan @scanParams)

            $result | Should -HaveCount 1
            Test-Path -LiteralPath $result[0].SnapshotPath | Should -BeTrue
            Test-Path -LiteralPath $result[0].ManifestPath | Should -BeTrue
            $manifest = Get-Content -LiteralPath $result[0].ManifestPath -Raw | ConvertFrom-Json
            $manifest.capture.root | Should -Be $tree
            $manifest.capture.environment | Should -Be 'lab'

            $script:loopback.Staging | Should -Not -BeNullOrEmpty
            (Split-Path -Parent $script:loopback.Staging) | Should -Be $workingDirectory
            Test-Path -LiteralPath $script:loopback.Staging | Should -BeFalse

            # The staged module cached its backend, with the native SQLite library for this runtime, under the child's data root.
            $runtimeId = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
            $cached = @(Get-ChildItem -LiteralPath $script:loopback.ChildDataRoot -Recurse -File)
            @($cached | Where-Object Name -eq 'DriftBuster.Backend.dll') | Should -HaveCount 1
            @($cached | Where-Object { $_.Name -like '*e_sqlite3*' -and $_.FullName -like "*$runtimeId*" }) | Should -Not -BeNullOrEmpty
        }

        It 'removes a half-made staging folder when staging fails' {
            $workingDirectory = New-TestDirectory
            $computerName = 'loopback-01'
            Mock New-Item -ModuleName $script:moduleName { Microsoft.PowerShell.Management\New-Item @PesterBoundParameters }
            Mock New-Item -ModuleName $script:moduleName -ParameterFilter { $Path -like '*captures' } { throw 'Access to the path is denied.' }

            { Invoke-DriftBusterRemoteScan -UseWinRM -ComputerName $computerName -RemotePath (New-TestDirectory) -Environment 'lab' -Reason 'loopback' -AllowUnmasked -RemoteWorkingDirectory $workingDirectory -OutputDirectory (New-TestDirectory) } |
                Should -Throw -ExpectedMessage '*denied*'

            @(Get-ChildItem -LiteralPath $workingDirectory -Force) | Should -BeNullOrEmpty
        }
    }
}
