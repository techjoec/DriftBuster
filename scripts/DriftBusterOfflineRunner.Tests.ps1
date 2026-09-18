<#
  Pester 5 tests for driftbuster-offline-runner.ps1 (dot-sourced for its helpers, run as a script for the entry point).

  Covers package encryption, config helpers, secret masking, runner execution, SQL snapshots, live registry hives and
  OfflineRegistryScanSource parsing. Tests tagged 'Windows' (live registry, DPAPI) are skipped elsewhere; SQL tests use
  winsqlite3 on Windows and libsqlite3.so.0 on Linux.

  Run: Invoke-Pester -Path scripts/DriftBusterOfflineRunner.Tests.ps1 -Output Detailed
#>

BeforeDiscovery {
    $script:OnWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'driftbuster-offline-runner.ps1') -ConfigPath 'dot-sourced'
    Add-Type -AssemblyName System.IO.Compression

    $script:OnWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
    $script:RepoRoot = Split-Path -Path $PSScriptRoot -Parent
    $script:RunnerScript = Join-Path -Path $PSScriptRoot -ChildPath 'driftbuster-offline-runner.ps1'
    $script:ConfigSchema = 'https://driftbuster.dev/offline-runner/config/v1'
    $script:ManifestSchema = 'https://driftbuster.dev/offline-runner/manifest/v1'
    $script:KeysetSchema = 'https://driftbuster.dev/offline-runner/encryption/keyset/v1'
    $script:EncryptedSchema = 'https://driftbuster.dev/offline-runner/encryption/dpapi-aes/v1'
    [DriftBusterOfflineRunner.EngineOs]::Cwd = $TestDrive

    function Get-TestDirectory {
        $path = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
        [void][System.IO.Directory]::CreateDirectory($path)
        return $path
    }

    function Join-TestPath {
        param([string] $Base, [string[]] $Child)
        $path = $Base
        foreach ($part in $Child) {
            $path = [System.IO.Path]::Combine($path, $part)
        }

        return $path
    }

    # The runner's JSON value for a PowerShell literal (a dump and load through its JSON reader).
    function ConvertTo-EngineValue {
        param($Value)
        return , [DriftBusterOfflineRunner.EngineJson]::Loads([DriftBusterOfflineRunner.EngineJson]::Dumps($Value, -1, $false))
    }

    function Write-TestText {
        param([string] $Path, [string] $Content)
        [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path))
        [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
    }

    function Write-TestConfig {
        param([string] $Directory, $Payload)
        $path = Join-Path -Path $Directory -ChildPath 'config.json'
        Write-TestText -Path $path -Content ([DriftBusterOfflineRunner.EngineJson]::Dumps($Payload, 2, $false))
        return $path
    }

    function ConvertTo-TestConfig {
        param($Payload)
        return ConvertFrom-DbOfflineRunnerConfig (ConvertTo-EngineValue $Payload)
    }

    # _build_config(tmp_path, profile=..., runner=..., metadata=...)
    function ConvertTo-BuiltConfig {
        param([string] $TmpPath, $ProfilePayload, $Runner, $Metadata)
        $payload = [ordered]@{ profile = $ProfilePayload }
        if ($null -eq $Runner) {
            $Runner = [ordered]@{ output_directory = (Join-Path -Path $TmpPath -ChildPath 'out'); cleanup_staging = $false }
        }

        $payload['runner'] = $Runner
        if ($null -ne $Metadata) {
            $payload['metadata'] = $Metadata
        }

        return ConvertTo-TestConfig $payload
    }

    function Read-ZipText {
        param([string] $ZipPath, [string] $EntryName)
        $stream = [System.IO.File]::OpenRead($ZipPath)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
            try {
                $entry = $archive.GetEntry($EntryName)
                if ($null -eq $entry) {
                    throw "zip entry not found: $EntryName"
                }

                $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false))
                try {
                    return $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $archive.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }

    function Get-ZipEntryName {
        param([byte[]] $Bytes, [string] $ZipPath)
        if ($ZipPath) {
            $Bytes = [System.IO.File]::ReadAllBytes($ZipPath)
        }

        $stream = [System.IO.MemoryStream]::new($Bytes)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
            try {
                return , @($archive.Entries | ForEach-Object { $_.FullName })
            }
            finally {
                $archive.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }

    function Read-ManifestFromPackage {
        param([string] $PackagePath)
        $PackagePath | Should -Not -BeNullOrEmpty
        return [DriftBusterOfflineRunner.EngineJson]::Loads((Read-ZipText -ZipPath $PackagePath -EntryName 'manifest.json'))
    }

    function Read-RunnerLogFromPackage {
        param([string] $PackagePath)
        $PackagePath | Should -Not -BeNullOrEmpty
        return Read-ZipText -ZipPath $PackagePath -EntryName 'logs/runner.log'
    }

    function Read-JsonFile {
        param([string] $Path)
        return [DriftBusterOfflineRunner.EngineJson]::Loads([DriftBusterOfflineRunner.EngineFile]::ReadText($Path))
    }

    function Get-FileSha256 {
        param([string] $Path)
        return [DriftBusterOfflineRunner.EngineFile]::HashFile($Path)
    }

    function Write-TestKeyset {
        param([string] $Path, [byte[]] $AesKey, [byte[]] $HmacKey)
        $payload = [ordered]@{
            schema   = $script:KeysetSchema
            aes_key  = [ordered]@{ encoding = 'base64'; data = [System.Convert]::ToBase64String($AesKey) }
            hmac_key = [ordered]@{ encoding = 'base64'; data = [System.Convert]::ToBase64String($HmacKey) }
        }
        Write-TestText -Path $Path -Content ([DriftBusterOfflineRunner.EngineJson]::Dumps($payload, 2, $false))
    }

    function Get-RepeatedByte {
        param([char] $Character, [int] $Count)
        return , [byte[]]([System.Text.Encoding]::ASCII.GetBytes([string]::new($Character, $Count)))
    }

    # AES-256-CBC with PKCS7 padding.
    function Unprotect-TestCiphertext {
        param([byte[]] $AesKey, [byte[]] $Iv, [byte[]] $Ciphertext)
        $aes = [System.Security.Cryptography.Aes]::Create()
        try {
            $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
            $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
            $aes.Key = $AesKey
            $aes.IV = $Iv
            $decryptor = $aes.CreateDecryptor()
            try {
                return , $decryptor.TransformFinalBlock($Ciphertext, 0, $Ciphertext.Length)
            }
            finally {
                $decryptor.Dispose()
            }
        }
        finally {
            $aes.Dispose()
        }
    }

    function Get-TestHmac {
        param([byte[]] $Key, [byte[]] $Iv, [byte[]] $Ciphertext)
        $hmac = [System.Security.Cryptography.HMACSHA256]::new($Key)
        try {
            return , $hmac.ComputeHash([byte[]]($Iv + $Ciphertext))
        }
        finally {
            $hmac.Dispose()
        }
    }

    function Assert-EngineError {
        param([scriptblock] $Script, [string] $Type, [string] $MessageLike)
        $caught = $null
        try {
            & $Script | Out-Null
        }
        catch {
            $caught = Get-DbEngineException $_
            if ($null -eq $caught) {
                throw
            }
        }

        $caught | Should -Not -BeNullOrEmpty -Because "a $Type was expected"
        $caught.ErrorType | Should -BeExactly $Type
        if ($MessageLike) {
            $caught.Message | Should -BeLike $MessageLike
        }
    }

    function Get-TextLine {
        param([string] $Text)
        $lines = [System.Collections.Generic.List[string]]::new()
        foreach ($line in ($Text -split "`r`n|`n|`r")) {
            $lines.Add($line)
        }

        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') {
            $lines.RemoveAt($lines.Count - 1)
        }

        return , $lines.ToArray()
    }

    # The sample accounts database fixtures/sql/README.md documents
    function Initialize-SampleDatabase {
        param([string] $Path)
        [DriftBusterOfflineRunner.SqlSnapshots]::Execute($Path, [System.Collections.ArrayList]@(
                'CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL)',
                "INSERT INTO accounts (email, secret, balance) VALUES ('alice@example.com', 'token-1', 42.5)",
                "INSERT INTO accounts (email, secret, balance) VALUES ('bob@example.com', 'token-2', 13.75)"
            ))
        return $Path
    }
}

Describe 'package encryption' {
    It 'execute config encrypts package with dpapi aes keyset' {
        $tmp = Get-TestDirectory
        $sourceDir = Join-Path $tmp 'source'
        Write-TestText -Path (Join-Path $sourceDir 'secrets.txt') -Content 'token-123'

        $keysetPath = Join-Path $tmp 'keyset.json'
        $aesKey = Get-RepeatedByte 'A' 32
        $hmacKey = Get-RepeatedByte 'B' 32
        Write-TestKeyset -Path $keysetPath -AesKey $aesKey -HmacKey $hmacKey
        $outputDir = Join-Path $tmp 'output'

        $config = ConvertTo-TestConfig ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{
                    name           = 'encrypt-demo'
                    description    = 'demo'
                    sources        = @([ordered]@{ path = (Join-Path $sourceDir 'secrets.txt'); alias = 'secret' })
                    options        = @{}
                    secret_scanner = @{}
                }
                runner   = [ordered]@{
                    output_directory = $outputDir
                    compress         = $true
                    cleanup_staging  = $false
                    encryption       = [ordered]@{
                        enabled = $true; mode = 'dpapi-aes'; keyset_path = $keysetPath; output_extension = '.enc'; remove_plaintext = $true
                    }
                }
                metadata = @{}
            })
        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20240101T000000Z'

        $result.package_path | Should -Not -BeNullOrEmpty
        [DriftBusterOfflineRunner.EnginePath]::Suffix($result.package_path) | Should -BeExactly '.enc'
        $result.encrypted_package_path | Should -BeExactly $result.package_path
        Test-Path -LiteralPath $result.package_path | Should -BeTrue -Because 'expected encrypted package'

        $result.unencrypted_package_path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $result.unencrypted_package_path | Should -BeFalse -Because 'plaintext package should be removed'

        $encrypted = Read-JsonFile $result.package_path
        $encrypted['schema'] | Should -BeExactly $script:EncryptedSchema
        $encrypted['algorithm'] | Should -BeExactly 'aes-256-cbc+hmac-sha256'

        $iv = [System.Convert]::FromBase64String($encrypted['iv'])
        $ciphertext = [System.Convert]::FromBase64String($encrypted['ciphertext'])
        $mac = [System.Convert]::FromBase64String($encrypted['mac'])
        [System.Convert]::ToBase64String($mac) | Should -BeExactly ([System.Convert]::ToBase64String((Get-TestHmac -Key $hmacKey -Iv $iv -Ciphertext $ciphertext)))

        $plaintext = Unprotect-TestCiphertext -AesKey $aesKey -Iv $iv -Ciphertext $ciphertext
        $names = Get-ZipEntryName -Bytes $plaintext
        @($names | Where-Object { $_.StartsWith('data/') }).Count | Should -BeGreaterThan 0
        $names | Should -Contain 'manifest.json'

        if ($result.manifest_path -and (Test-Path -LiteralPath $result.manifest_path)) {
            $manifest = Read-JsonFile $result.manifest_path
        }
        else {
            throw 'expected the staging manifest'
        }

        $encryptionInfo = $manifest['package']['encryption']
        $encryptionInfo['enabled'] | Should -BeTrue
        $encryptionInfo['output_name'].EndsWith('.enc') | Should -BeTrue
        $encryptionInfo['remove_plaintext'] | Should -BeTrue
        $encryptionInfo['sha256'] | Should -BeExactly (Get-FileSha256 $result.package_path)
    }

    It 'execute config requires compress for encryption' {
        $tmp = Get-TestDirectory
        $keysetPath = Join-Path $tmp 'keyset.json'
        Write-TestKeyset -Path $keysetPath -AesKey (Get-RepeatedByte 'A' 32) -HmacKey (Get-RepeatedByte 'B' 32)

        $config = ConvertTo-TestConfig ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{
                    name = 'no-compress'; sources = @([ordered]@{ path = $keysetPath; alias = 'key' }); options = @{}; secret_scanner = @{}
                }
                runner   = [ordered]@{
                    output_directory = (Join-Path $tmp 'out'); compress = $false; cleanup_staging = $false
                    encryption = [ordered]@{ enabled = $true; keyset_path = $keysetPath }
                }
                metadata = @{}
            })

        Assert-EngineError { Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20240101T000000Z' } 'InvalidDataException'
    }

    It 'execute config path supports relative paths' {
        $tmp = Get-TestDirectory
        $configDir = Join-Path $tmp 'bundle'
        Write-TestText -Path (Join-Path $configDir 'secrets.txt') -Content 'token-456'
        $aesKey = Get-RepeatedByte 'C' 32
        $hmacKey = Get-RepeatedByte 'D' 32
        Write-TestKeyset -Path (Join-Path $configDir 'keyset.json') -AesKey $aesKey -HmacKey $hmacKey

        $configPath = Write-TestConfig -Directory $configDir -Payload ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{ name = 'relative-paths'; sources = @([ordered]@{ path = 'secrets.txt' }); options = @{}; secret_scanner = @{} }
                runner   = [ordered]@{
                    compress = $true; cleanup_staging = $true
                    encryption = [ordered]@{ enabled = $true; mode = 'dpapi-aes'; keyset_path = 'keyset.json'; output_extension = '.enc'; remove_plaintext = $true }
                }
                metadata = @{}
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath -Timestamp '20240202T120000Z'

        $result.package_path | Should -Not -BeNullOrEmpty
        [DriftBusterOfflineRunner.EnginePath]::Parent($result.package_path) | Should -BeExactly ([DriftBusterOfflineRunner.EnginePath]::Normalise($configDir))
        [DriftBusterOfflineRunner.EnginePath]::Suffix($result.package_path) | Should -BeExactly '.enc'
        $result.encrypted_package_path | Should -BeExactly $result.package_path

        $result.unencrypted_package_path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $result.unencrypted_package_path | Should -BeFalse

        $payload = $result.encryption_payload
        $payload | Should -Not -BeNullOrEmpty
        $payload['package']['original_name'].EndsWith('.zip') | Should -BeTrue

        $iv = [System.Convert]::FromBase64String($payload['iv'])
        $ciphertext = [System.Convert]::FromBase64String($payload['ciphertext'])
        $plaintext = Unprotect-TestCiphertext -AesKey $aesKey -Iv $iv -Ciphertext $ciphertext
        $names = Get-ZipEntryName -Bytes $plaintext
        @($names | Where-Object { $_.EndsWith('secrets.txt') }).Count | Should -BeGreaterThan 0
        $names | Should -Contain 'manifest.json'
    }
}

Describe 'config helpers' {
    It 'offline registry scan source from dict normalises values' {
        $payload = ConvertTo-EngineValue ([ordered]@{
                registry_scan = [ordered]@{
                    token = 'ExampleApp '; keywords = 'alpha, beta'; patterns = @('value1', 'value2'); max_depth = '8'; max_hits = '150'; time_budget_s = '15'
                }
                alias         = '  ExampleAlias  '
            })
        $source = ConvertFrom-DbOfflineRegistryScanSource $payload
        $source.token | Should -BeExactly 'ExampleApp'
        $source.keywords | Should -Be @('alpha', 'beta')
        $source.patterns | Should -Be @('value1', 'value2')
        $source.max_depth | Should -Be 8
        $source.max_hits | Should -Be 150
        $source.time_budget_s | Should -Be 15.0
        Get-DbDestinationName -Source $source -FallbackIndex 1 | Should -BeExactly '--ExampleAlias--'

        $noAlias = ConvertFrom-DbOfflineRegistryScanSource (ConvertTo-EngineValue ([ordered]@{ registry_scan = [ordered]@{ token = 'Example'; keywords = @('one'); patterns = @() } }))
        (Get-DbDestinationName -Source $noAlias -FallbackIndex 2).StartsWith('registry_') | Should -BeTrue
    }

    It 'normalise snapshot columns handles sequences' {
        $mapping = ConvertTo-DbSnapshotColumnMap (ConvertTo-EngineValue ([ordered]@{ users = @('id', 'email'); events = @('timestamp', 'severity') }))
        $mapping['users'] | Should -Be @('id', 'email')
        $mapping['events'] | Should -Be @('timestamp', 'severity')

        $sequence = ConvertTo-DbSnapshotColumnMap (ConvertTo-EngineValue @('audit.id', 'audit.created', 'logs.message', 'invalid', 'logs.'))
        $sequence['audit'] | Should -Be @('id', 'created')
        $sequence['logs'] | Should -Be @('message')
        (ConvertTo-DbSnapshotColumnMap $null).Count | Should -Be 0
    }

    It 'offline sql snapshot source from dict and kwargs' {
        $tmp = Get-TestDirectory
        $dbPath = Join-Path $tmp 'db.sqlite'
        $payload = ConvertTo-EngineValue ([ordered]@{
                sql_snapshot = [ordered]@{
                    path = $dbPath; tables = @('users', 'logs'); exclude_tables = 'audit'; mask_columns = [ordered]@{ users = @('password') }
                    hash_columns = @('users.email', 'users.id'); limit = '25'; placeholder = '[MASKED]'; hash_salt = 'pepper'
                }
                alias        = ' database '
            })
        $source = ConvertFrom-DbOfflineSqlSnapshotSource $payload
        $source.path | Should -BeExactly $dbPath
        $source.tables | Should -Be @('users', 'logs')
        $source.exclude_tables | Should -Be @('audit')
        $source.mask_columns['users'] | Should -Be @('password')
        $source.hash_columns['users'] | Should -Be @('email', 'id')
        $source.limit | Should -Be 25
        $source.placeholder | Should -BeExactly '[MASKED]'
        $source.hash_salt | Should -BeExactly 'pepper'
        Get-DbDestinationName -Source $source -FallbackIndex 2 | Should -BeExactly 'database'

        $kwargs = Get-DbSnapshotArgument $source
        $kwargs['tables'] | Should -Be @('users', 'logs')
        $kwargs['limit'] | Should -Be 25
    }

    It 'offline sql snapshot source limit validation' {
        $payload = ConvertTo-EngineValue ([ordered]@{ sql_snapshot = [ordered]@{ path = 'sample.db'; limit = 0 } })
        Assert-EngineError { ConvertFrom-DbOfflineSqlSnapshotSource $payload } 'InvalidDataException'
    }

    It 'offline sql snapshot source dialect validation' {
        $payload = ConvertTo-EngineValue ([ordered]@{ sql_snapshot = [ordered]@{ path = 'sample.db'; dialect = 'postgres' } })
        Assert-EngineError { ConvertFrom-DbOfflineSqlSnapshotSource $payload } 'InvalidDataException'
    }

    It 'offline runner profile with registry and sql sources' {
        $tmp = Get-TestDirectory
        $filePath = Join-Path $tmp 'config.txt'
        Write-TestText -Path $filePath -Content 'example'
        $payload = ConvertTo-EngineValue ([ordered]@{
                name           = 'profile-sample'
                sources        = @(
                    [ordered]@{ path = $filePath },
                    [ordered]@{ registry_scan = [ordered]@{ token = 'ExampleToken'; keywords = @('alpha'); patterns = @('value') } },
                    [ordered]@{ sql_snapshot = [ordered]@{ path = 'sample.db' } }
                )
                baseline       = $filePath
                tags           = @('audit')
                options        = [ordered]@{ secret_ignore_rules = @('PasswordAssignment') }
                secret_scanner = [ordered]@{ ignore_rules = @('GenericApiToken') }
            })
        $profileObject = ConvertFrom-DbOfflineRunnerProfile $payload
        $profileObject.sources.Count | Should -Be 3
        @($profileObject.sources | Where-Object { $_.kind -eq 'registry_scan' }).Count | Should -BeGreaterThan 0
        @($profileObject.sources | Where-Object { $_.kind -eq 'sql_snapshot' }).Count | Should -BeGreaterThan 0
    }

    It 'offline encryption settings from dict formats extension' {
        $tmp = Get-TestDirectory
        $keysetPath = Join-Path $tmp 'key.json'
        Write-TestText -Path $keysetPath -Content '{}'
        $settings = ConvertFrom-DbOfflineEncryptionSetting (ConvertTo-EngineValue ([ordered]@{
                    enabled = $true; mode = 'DPAPI-AES'; keyset_path = $keysetPath; output_extension = 'encpkg'; remove_plaintext = $false
                }))
        $settings.enabled | Should -BeTrue
        $settings.mode | Should -BeExactly 'dpapi-aes'
        $settings.output_extension | Should -BeExactly '.encpkg'
        $settings.remove_plaintext | Should -BeFalse

        Assert-EngineError { ConvertFrom-DbOfflineEncryptionSetting (ConvertTo-EngineValue ([ordered]@{ enabled = $true })) } 'InvalidDataException'
    }

    It 'offline runner settings from dict handles defaults' {
        $tmp = Get-TestDirectory
        $settings = ConvertFrom-DbOfflineRunnerSetting (ConvertTo-EngineValue ([ordered]@{
                    output_directory = $tmp; package_name = ' '; max_total_bytes = '2048'; encryption = [ordered]@{ enabled = $false }
                }))
        $settings.output_directory | Should -BeExactly ([DriftBusterOfflineRunner.EnginePath]::Normalise($tmp))
        $settings.package_name | Should -BeNullOrEmpty
        $settings.max_total_bytes | Should -Be 2048
        $settings.encryption | Should -Not -BeNullOrEmpty
        $settings.encryption.enabled | Should -BeFalse
    }

    It 'execute config skips registry scan on non windows' {
        # The runner is pinned to a host view that is not Windows.
        Mock Test-DbWindowsPlatform { $false }
        $tmp = Get-TestDirectory
        $config = ConvertTo-TestConfig ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{
                    name           = 'registry-only'
                    sources        = @([ordered]@{ registry_scan = [ordered]@{ token = 'ExampleToken'; keywords = @('alpha'); patterns = @('value') } })
                    options        = @{}
                    secret_scanner = @{}
                }
                runner   = [ordered]@{ output_directory = (Join-Path $tmp 'out'); compress = $false; cleanup_staging = $false }
                metadata = @{}
            })
        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20251025T083000Z'

        $result.manifest_path | Should -Not -BeNullOrEmpty
        $manifest = Read-JsonFile $result.manifest_path
        $manifest['sources'].Count | Should -BeGreaterThan 0
        $summary = $manifest['sources'][0]
        $summary['type'] | Should -BeExactly 'registry_scan'
        $summary['skipped'] | Should -BeTrue
        $summary['reason'] | Should -BeExactly 'not-windows'
    }
}

Describe 'secret masking' {
    It 'execute config masks secret samples' {
        $tmp = Get-TestDirectory
        $fixturesRoot = Join-TestPath $script:RepoRoot @('fixtures', 'secret_samples')
        $keysetPath = Join-Path $tmp 'keyset.json'
        Write-TestKeyset -Path $keysetPath -AesKey (Get-RepeatedByte 'A' 32) -HmacKey (Get-RepeatedByte 'B' 32)

        $config = ConvertTo-TestConfig ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{
                    name           = 'fixtures-secret-validation'
                    description    = 'integration validation for secret masking'
                    sources        = @([ordered]@{ path = (Join-Path $fixturesRoot 'auth_secrets.txt'); alias = 'secret-fixtures' })
                    options        = @{}
                    secret_scanner = @{}
                }
                runner   = [ordered]@{
                    output_directory = (Join-Path $tmp 'output'); compress = $true; cleanup_staging = $false
                    encryption = [ordered]@{ enabled = $true; mode = 'dpapi-aes'; keyset_path = $keysetPath; output_extension = '.enc'; remove_plaintext = $true }
                }
                metadata = [ordered]@{ audit = 'secret-masking' }
            })
        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20251025T070000Z'

        $result.package_path | Should -Not -BeNullOrEmpty
        [DriftBusterOfflineRunner.EnginePath]::Suffix($result.package_path) | Should -BeExactly '.enc'
        $result.encrypted_package_path | Should -BeExactly $result.package_path
        $result.unencrypted_package_path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $result.unencrypted_package_path | Should -BeFalse

        Test-Path -LiteralPath $result.manifest_path | Should -BeTrue
        $manifest = Read-JsonFile $result.manifest_path
        $details = $manifest['package']['encryption']
        $details['enabled'] | Should -BeTrue
        $details['schema'] | Should -BeExactly $script:EncryptedSchema

        $findings = $manifest['secrets']['findings']
        $rules = @($findings | ForEach-Object { $_['rule'] })
        foreach ($expected in @('PasswordAssignment', 'GenericApiToken', 'AwsAccessKeyId')) {
            $rules | Should -Contain $expected
        }

        @($findings | Where-Object { $_['path'].EndsWith('auth_secrets.txt') }).Count | Should -BeGreaterThan 0 -Because 'findings should reference the secret fixture'

        $secretEntry = @($result.files | Where-Object { $_.relative_path.EndsWith('auth_secrets.txt') })[0]
        $secretEntry | Should -Not -BeNullOrEmpty -Because 'expected collected secret fixture'

        $sanitized = [System.IO.File]::ReadAllText($secretEntry.destination)
        $sanitized | Should -Match ([regex]::Escape('[SECRET]'))
        $sanitized | Should -Not -Match 'SuperSecret1234'
        $sanitized | Should -Not -Match 'ABCDEF1234567890ABCD'
        $sanitized | Should -Not -Match 'AKIA1234567890ABCDEF'

        $snippets = @($findings | ForEach-Object { $_['snippet'] })
        $snippets | Should -Contain '[SECRET]'
        @($snippets | Where-Object { $_.Contains('SuperSecret1234') }).Count | Should -Be 0
    }
}

Describe 'runner execution' {
    It 'load config accepts string and object sources' {
        $tmp = Get-TestDirectory
        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                schema  = $script:ConfigSchema
                profile = [ordered]@{
                    name    = 'demo'
                    sources = @((Join-Path $tmp 'file.txt'), [ordered]@{ path = (Join-Path $tmp 'dir'); alias = 'dir'; optional = $true })
                }
            })

        $config = Import-DbOfflineRunnerConfig -Path $configPath
        $config.profile.name | Should -BeExactly 'demo'
        $config.profile.sources.Count | Should -Be 2
        $first, $second = $config.profile.sources
        $first.kind | Should -BeExactly 'file'
        $second.kind | Should -BeExactly 'file'
        $first.path.EndsWith('file.txt') | Should -BeTrue
        $second.alias | Should -BeExactly 'dir'
        $second.optional | Should -BeTrue
    }

    It 'execute offline run collects files' {
        $tmp = Get-TestDirectory
        $logsDir = Join-Path $tmp 'logs'
        $sampleLog = Join-Path $logsDir 'firewall.log'
        Write-TestText -Path $sampleLog -Content 'entry'

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                schema   = $script:ConfigSchema
                version  = '1.0'
                profile  = [ordered]@{
                    name        = 'windows_baseline'
                    description = 'Collect baseline logs'
                    sources     = @([ordered]@{ path = $sampleLog }, [ordered]@{ path = $logsDir; alias = 'logs'; exclude = @('*.tmp') })
                    tags        = @('windows', 'baseline')
                }
                runner   = [ordered]@{
                    output_directory = (Join-Path $tmp 'out'); compress = $true; include_config = $true; include_logs = $true; include_manifest = $true
                }
                metadata = [ordered]@{ request_id = 'abc-123' }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath

        $result.package_path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $result.package_path | Should -BeTrue
        $result.manifest_path | Should -BeNullOrEmpty
        $result.log_path | Should -BeNullOrEmpty
        $result.staging_dir | Should -BeNullOrEmpty
        $result.files.Count | Should -BeGreaterOrEqual 2

        $contents = Get-ZipEntryName -ZipPath $result.package_path
        @($contents | Where-Object { $_.StartsWith('data/') }).Count | Should -BeGreaterThan 0
        $contents | Should -Contain 'manifest.json'
        @($contents | Where-Object { $_ -eq 'config.json' -or $_.EndsWith('config.json') }).Count | Should -BeGreaterThan 0

        $manifest = Read-ManifestFromPackage $result.package_path
        $logContents = Read-RunnerLogFromPackage $result.package_path
        $manifest['schema'] | Should -BeExactly $script:ManifestSchema
        $manifest['profile']['name'] | Should -BeExactly 'windows_baseline'
        $manifest['metadata']['request_id'] | Should -BeExactly 'abc-123'
        @($manifest['files'] | Where-Object { $_['relative_path'].EndsWith('firewall.log') }).Count | Should -BeGreaterThan 0
        $manifest['package']['cleanup_staging'] | Should -BeTrue
        $logContents | Should -Match 'offline collection finished'
    }

    It 'execute offline run handles optional source' {
        $tmp = Get-TestDirectory
        $existing = Join-Path $tmp 'present.log'
        Write-TestText -Path $existing -Content 'log'

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{
                    name    = 'optional'
                    sources = @([ordered]@{ path = $existing }, [ordered]@{ path = (Join-TestPath $tmp @('missing', '*.log')); alias = 'missing'; optional = $true })
                }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out') }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath

        @($result.files | Where-Object { $_.alias -eq 'missing' }).Count | Should -Be 0
        $result.manifest_path | Should -BeNullOrEmpty
        $manifest = Read-ManifestFromPackage $result.package_path
        $summary = @($manifest['sources'] | Where-Object { $_['alias'] -eq 'missing' })[0]
        $summary['skipped'] | Should -BeTrue
        $summary['reason'] | Should -BeExactly 'no-matches'
    }

    It 'execute offline run missing required source' {
        $tmp = Get-TestDirectory
        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{ name = 'missing-required'; sources = @((Join-Path $tmp 'missing.txt')) }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out') }
            })

        Assert-EngineError { Invoke-DbOfflineRunnerPath -ConfigPath $configPath } 'FileNotFoundException'
    }

    It 'execute offline run respects exclude patterns' {
        $tmp = Get-TestDirectory
        $dataDir = Join-Path $tmp 'data'
        Write-TestText -Path (Join-Path $dataDir 'keep.log') -Content 'keep'
        Write-TestText -Path (Join-Path $dataDir 'ignore.tmp') -Content 'ignore'

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{ name = 'excludes'; sources = @([ordered]@{ path = $dataDir; exclude = @('*.tmp') }) }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out') }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath
        $paths = @($result.files | ForEach-Object { $_.relative_path })
        @($paths | Where-Object { $_.Contains('ignore.tmp') }).Count | Should -Be 0
        @($paths | Where-Object { $_.EndsWith('keep.log') }).Count | Should -BeGreaterThan 0
    }

    It 'execute offline run deduplicates recursive glob matches' {
        $tmp = Get-TestDirectory
        $sourceRoot = Join-Path $tmp 'source'
        Write-TestText -Path (Join-Path $sourceRoot 'root.log') -Content 'root'
        Write-TestText -Path (Join-TestPath $sourceRoot @('nested', 'child.log')) -Content 'child'

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{ name = 'recursive-glob'; sources = @([ordered]@{ path = "$sourceRoot/**/*" }) }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out') }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath
        $collected = @($result.files | ForEach-Object { $_.relative_path })
        $collected.Count | Should -Be @($collected | Select-Object -Unique).Count
        @($collected | Where-Object { $_.EndsWith('root.log') }).Count | Should -BeGreaterThan 0
        @($collected | Where-Object { $_.EndsWith('child.log') }).Count | Should -BeGreaterThan 0
    }

    It 'execute offline run enforces max total bytes' {
        $tmp = Get-TestDirectory
        $source = Join-Path $tmp 'large.bin'
        [System.IO.File]::WriteAllBytes($source, (Get-RepeatedByte '0' 1024))

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{ name = 'limits'; sources = @($source) }
                runner  = [ordered]@{ max_total_bytes = 10; output_directory = (Join-Path $tmp 'out') }
            })

        Assert-EngineError { Invoke-DbOfflineRunnerPath -ConfigPath $configPath } 'InvalidOperationException' '*max_total_bytes*'
    }

    It 'execute offline run scrubs secret lines' {
        $tmp = Get-TestDirectory
        $secretFile = Join-Path $tmp 'secrets.txt'
        Write-TestText -Path $secretFile -Content "safe line`npassword = SUPERSECRET123456`nkeep me`n"

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{ name = 'secret-scan'; sources = @($secretFile) }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out'); include_logs = $true; include_manifest = $true }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath

        Read-RunnerLogFromPackage $result.package_path | Should -Match 'secret candidate redacted'

        $collectedFile = @($result.files | Where-Object { $_.source -eq $secretFile })[0]
        $collectedText = Read-ZipText -ZipPath $result.package_path -EntryName "data/$($collectedFile.relative_path)"
        $collectedText | Should -Not -Match 'SUPERSECRET123456'
        $lines = Get-TextLine $collectedText
        $lines | Should -Be @('safe line', '[SECRET]', 'keep me')

        $manifest = Read-ManifestFromPackage $result.package_path
        $manifest['profile'].Contains('secret_scanner') | Should -BeTrue
        $profileScanner = $manifest['profile']['secret_scanner']
        $profileScanner['ruleset_version'] | Should -BeExactly '2024-06-01'
        $profileScanner.Contains('rules') | Should -BeFalse
        $secrets = $manifest['secrets']
        $secrets['ruleset_version'] | Should -BeExactly '2024-06-01'
        $secrets['ignored_rules'].Count | Should -Be 0
        $secrets['ignored_patterns'].Count | Should -Be 0
        $secrets['findings'].Count | Should -Be 1
        $finding = $secrets['findings'][0]
        @('PasswordAssignment', 'GenericApiToken') | Should -Contain $finding['rule']
        ($finding['snippet'].EndsWith('[SECRET]') -or $finding['snippet'].Contains('[SECRET]')) | Should -BeTrue
    }

    It 'execute offline run honours secret ignore patterns' {
        $tmp = Get-TestDirectory
        $secretFile = Join-Path $tmp 'allowlist.txt'
        Write-TestText -Path $secretFile -Content 'password = ALLOW_ME'

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{ name = 'secret-ignore'; sources = @($secretFile); secret_scanner = [ordered]@{ ignore_patterns = @('ALLOW_ME') } }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out'); include_logs = $true; include_manifest = $true }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath

        Read-RunnerLogFromPackage $result.package_path | Should -Not -Match 'secret candidate redacted'
        $collectedFile = @($result.files | Where-Object { $_.source -eq $secretFile })[0]
        Read-ZipText -ZipPath $result.package_path -EntryName "data/$($collectedFile.relative_path)" | Should -Match 'ALLOW_ME'

        $manifest = Read-ManifestFromPackage $result.package_path
        $secrets = $manifest['secrets']
        $secrets['findings'].Count | Should -Be 0
        $secrets['ignored_patterns'] | Should -Contain 'ALLOW_ME'
        $manifest['profile']['secret_scanner']['ignore_patterns'] | Should -Contain 'ALLOW_ME'
        $manifest['profile']['secret_scanner'].Contains('rules') | Should -BeFalse
    }

    It 'execute offline run prefers ruleset from config' {
        $tmp = Get-TestDirectory
        $secretFile = Join-Path $tmp 'custom.txt'
        Write-TestText -Path $secretFile -Content 'token = TOTALLY_CUSTOM_SECRET'

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{
                    name           = 'secret-config'
                    sources        = @($secretFile)
                    secret_scanner = [ordered]@{
                        ruleset = [ordered]@{ version = 'custom-1'; rules = @([ordered]@{ name = 'CustomToken'; pattern = 'TOTALLY_CUSTOM_SECRET'; flags = '' }) }
                    }
                }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out'); include_logs = $true; include_manifest = $true }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath

        $manifest = Read-ManifestFromPackage $result.package_path
        $secrets = $manifest['secrets']
        $secrets['ruleset_version'] | Should -BeExactly 'custom-1'
        $secrets['findings'].Count | Should -BeGreaterThan 0
        $profileScanner = $manifest['profile']['secret_scanner']
        $profileScanner['ruleset_version'] | Should -BeExactly 'custom-1'
        $profileScanner.Contains('rules') | Should -BeFalse
    }

    It 'execute offline run retains staging when cleanup disabled' {
        $tmp = Get-TestDirectory
        $sample = Join-Path $tmp 'artifact.txt'
        Write-TestText -Path $sample -Content 'data'

        $configPath = Write-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{ name = 'no-cleanup'; sources = @($sample) }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out'); cleanup_staging = $false; include_manifest = $true; include_logs = $true }
            })

        $result = Invoke-DbOfflineRunnerPath -ConfigPath $configPath

        $result.staging_dir | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $result.staging_dir | Should -BeTrue
        $result.manifest_path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $result.manifest_path | Should -BeTrue
        $result.log_path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $result.log_path | Should -BeTrue
    }

    It 'compile ruleset from mapping handles invalid entries' {
        ConvertTo-DbCompiledRuleset $null | Should -BeNullOrEmpty
        ConvertTo-DbCompiledRuleset (ConvertTo-EngineValue ([ordered]@{ rules = 'invalid' })) | Should -BeNullOrEmpty

        $payload = ConvertTo-EngineValue ([ordered]@{
                version = 'custom'
                rules   = @([ordered]@{ name = 'Valid'; pattern = 'secret'; flags = 'i' }, [ordered]@{ name = 'Broken'; pattern = '[' })
            })
        $compiled = ConvertTo-DbCompiledRuleset $payload
        $compiled | Should -Not -BeNullOrEmpty
        $compiled.Version | Should -BeExactly 'custom'
        $compiled.Rules.Count | Should -Be 1
        $compiled.Rules[0] | Should -BeOfType ([DriftBusterOfflineRunner.SecretRule])
    }

    It 'secret option values and manifest helpers' {
        $values = Get-DbSecretOptionValue 'a, b ; c'
        $values | Should -Be @('a', 'b', 'c')
        $values = Get-DbSecretOptionValue (ConvertTo-EngineValue @('x', $null, ' y '))
        $values | Should -Be @('x', 'y')

        $context = [DriftBusterOfflineRunner.SecretContext]::new()
        $context.Version = 'v1'
        [void]$context.IgnoreRules.Add('Skip')
        $context.IgnorePatterns.Add([regex]::new('SKIP'))
        $context.IgnorePatternText.Add('SKIP')
        $context.RulesLoaded = $true

        $manifest = Get-DbManifestSecretScanner -Options (ConvertTo-EngineValue ([ordered]@{ secret_ignore_rules = 'Skip' })) `
            -SecretScanner (ConvertTo-EngineValue ([ordered]@{ ignore_patterns = @('SKIP') })) -Context $context
        $manifest['ruleset_version'] | Should -BeExactly 'v1'
        $manifest['ignore_rules'] | Should -Be @('Skip')
        $manifest['ignore_patterns'] | Should -Be @('SKIP')
    }

    It 'build secret context prefers inline rules' {
        $payload = ConvertTo-EngineValue ([ordered]@{
                ruleset      = [ordered]@{ version = 'inline'; rules = @([ordered]@{ name = 'Token'; pattern = 'VALUE' }) }
                ignore_rules = @('Token')
            })
        $context = Get-DbSecretContext -Options (ConvertTo-EngineValue ([ordered]@{ secret_ignore_patterns = @('ALLOW') })) -SecretScanner $payload
        $context.Version | Should -BeExactly 'inline'
        $context.RulesLoaded | Should -BeTrue
        @($context.IgnoreRules) | Should -Be @('Token')
        $context.IgnorePatternText | Should -Contain 'ALLOW'
    }

    It 'offline collection source validations' {
        Assert-EngineError { ConvertFrom-DbOfflineCollectionSource (ConvertTo-EngineValue @{}) } 'InvalidDataException'

        $source = ConvertFrom-DbOfflineCollectionSource (ConvertTo-EngineValue ([ordered]@{ path = '~/data'; alias = '  '; exclude = '*.tmp' }))
        $source.alias | Should -BeNullOrEmpty
        $source.exclude | Should -Be @('*.tmp')

        $rootSource = ConvertFrom-DbOfflineCollectionSource (ConvertTo-EngineValue ([ordered]@{ path = '/' }))
        Get-DbDestinationName -Source $rootSource -FallbackIndex 7 | Should -BeExactly 'source_07'
    }

    It 'offline runner profile validations' {
        Assert-EngineError { ConvertFrom-DbOfflineRunnerProfile (ConvertTo-EngineValue ([ordered]@{ name = '' })) } 'InvalidDataException'
        Assert-EngineError { ConvertFrom-DbOfflineRunnerProfile (ConvertTo-EngineValue ([ordered]@{ name = 'demo'; sources = @('/tmp/a'); baseline = 'missing' })) } 'InvalidDataException'
        Assert-EngineError { ConvertFrom-DbOfflineRunnerProfile (ConvertTo-EngineValue ([ordered]@{ name = 'demo'; sources = @('/tmp/a'); options = 'invalid' })) } 'InvalidDataException'
        Assert-EngineError { ConvertFrom-DbOfflineRunnerProfile (ConvertTo-EngineValue ([ordered]@{ name = 'demo'; sources = @('/tmp/a'); secret_scanner = 'invalid' })) } 'InvalidDataException'
        Assert-EngineError { ConvertFrom-DbOfflineRunnerProfile (ConvertTo-EngineValue ([ordered]@{ name = 'demo' })) } 'InvalidDataException'

        $profileObject = ConvertFrom-DbOfflineRunnerProfile (ConvertTo-EngineValue ([ordered]@{ name = 'tags'; sources = @('/tmp/a'); tags = 'prod' }))
        $profileObject.tags | Should -Be @('prod')
    }

    It 'offline runner config default package name' {
        Mock Get-DbTimestamp { '20230101T000000Z' }
        $config = ConvertTo-TestConfig ([ordered]@{ profile = [ordered]@{ name = 'Demo'; sources = @('/tmp/a') } })
        Get-DbDefaultPackageName -Config $config | Should -BeExactly 'Demo-20230101T000000Z'
    }

    It 'execute config logs when secret rules missing' {
        Mock Get-DbSecretContext {
            $context = [DriftBusterOfflineRunner.SecretContext]::new()
            $context.Version = 'v'
            $context.RulesLoaded = $false
            return $context
        }

        $tmp = Get-TestDirectory
        $filePath = Join-Path $tmp 'data.txt'
        Write-TestText -Path $filePath -Content 'content'
        $config = ConvertTo-BuiltConfig -TmpPath $tmp -ProfilePayload ([ordered]@{ name = 'demo'; sources = @($filePath) }) `
            -Runner ([ordered]@{ output_directory = (Join-Path $tmp 'out'); cleanup_staging = $false })

        $result = Invoke-DbOfflineRunner -Config $config -Timestamp '20230101T010101Z'

        $result.log_path | Should -Not -BeNullOrEmpty
        [System.IO.File]::ReadAllText($result.log_path) | Should -Match 'secret detection rules unavailable'
    }

    It 'execute config skips symlink' {
        $tmp = Get-TestDirectory
        $realFile = Join-Path $tmp 'real.txt'
        Write-TestText -Path $realFile -Content 'data'
        $symlink = Join-Path $tmp 'link.txt'
        New-Item -ItemType SymbolicLink -Path $symlink -Target $realFile | Out-Null

        $config = ConvertTo-BuiltConfig -TmpPath $tmp -ProfilePayload ([ordered]@{ name = 'symlinks'; sources = @($realFile, $symlink) })

        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp
        $paths = @($result.files | ForEach-Object { $_.source })
        @($paths | Where-Object { $_.EndsWith('real.txt') }).Count | Should -BeGreaterThan 0
        $paths | Should -Not -Contain $symlink
    }

    It 'execute config appends zip extension' {
        $tmp = Get-TestDirectory
        $filePath = Join-Path $tmp 'file.log'
        Write-TestText -Path $filePath -Content 'data'
        $config = ConvertTo-BuiltConfig -TmpPath $tmp -ProfilePayload ([ordered]@{ name = 'archive'; sources = @($filePath) }) `
            -Runner ([ordered]@{ output_directory = (Join-Path $tmp 'out'); package_name = 'artifact'; compress = $true; cleanup_staging = $true })

        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp
        $result.package_path | Should -Not -BeNullOrEmpty
        [DriftBusterOfflineRunner.EnginePath]::Name($result.package_path).EndsWith('.zip') | Should -BeTrue
    }
}

Describe 'SQL snapshots' {
    It 'build sqlite snapshot masks and hashes' {
        $tmp = Get-TestDirectory
        $dbPath = Initialize-SampleDatabase (Join-Path $tmp 'sample.sqlite')

        $payload = Get-DbSqliteSnapshot -Path $dbPath -MaskColumns ([ordered]@{ accounts = @('secret') }) -HashColumns ([ordered]@{ accounts = @('email') }) `
            -Placeholder '[MASK]' -HashSalt 'pepper'

        $payload['database'] | Should -BeExactly 'sample.sqlite'
        $payload['dialect'] | Should -BeExactly 'sqlite'
        $payload['tables'].Count | Should -BeGreaterThan 0 -Because 'expected exported tables'

        $accounts = $payload['tables'][0]
        $accounts['name'] | Should -BeExactly 'accounts'
        $accounts['row_count'] | Should -Be 2
        $accounts['masked_columns'] | Should -Be @('secret')
        $accounts['hashed_columns'] | Should -Be @('email')

        $rows = $accounts['rows']
        $rows[0]['secret'] | Should -BeExactly '[MASK]'
        $rows[0]['email'].StartsWith('sha256:') | Should -BeTrue
        [double]$rows[0]['balance'] | Should -Be 42.5
    }

    It 'write sqlite snapshot with limits and sequences' {
        $tmp = Get-TestDirectory
        $dbPath = Initialize-SampleDatabase (Join-Path $tmp 'limited.sqlite')
        [DriftBusterOfflineRunner.SqlSnapshots]::Execute($dbPath, [System.Collections.ArrayList]@(
                'CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB)',
                "INSERT INTO audit (payload) VALUES (X'6175646974')"
            ))

        $destination = Join-Path $tmp 'out.json'
        $snapshot = Get-DbSqliteSnapshot -Path $dbPath -Tables @('accounts') -ExcludeTables @('nonexistent') `
            -MaskColumns (ConvertTo-EngineValue @('accounts.secret')) -HashColumns (ConvertTo-EngineValue @('accounts.email')) -Limit 1
        [DriftBusterOfflineRunner.EngineFile]::WriteText($destination, [DriftBusterOfflineRunner.EngineJson]::Dumps($snapshot, 2, $true))

        $payload = Read-JsonFile $destination
        $payload['tables'][0]['row_count'] | Should -Be 2
        $payload['tables'][0]['rows'].Count | Should -Be 1
        $payload['tables'][0]['rows'][0]['secret'] | Should -BeExactly '[REDACTED]'

        $audit = Get-DbSqliteSnapshot -Path $dbPath -Tables @('audit')
        $audit['tables'][0]['rows'][0]['payload']['type'] | Should -BeExactly 'base64'

        Assert-EngineError { Get-DbSqliteSnapshot -Path $dbPath -Limit 0 } 'ArgumentOutOfRangeException'
    }

    It 'offline runner sql snapshot source' {
        $tmp = Get-TestDirectory
        $dbPath = Initialize-SampleDatabase (Join-Path $tmp 'runner.sqlite')
        $config = ConvertTo-TestConfig ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{
                    name           = 'sql-demo'
                    description    = 'demo'
                    sources        = @([ordered]@{
                            sql_snapshot = [ordered]@{
                                path = $dbPath; mask_columns = [ordered]@{ accounts = @('secret') }; hash_columns = [ordered]@{ accounts = @('email') }
                                placeholder = '[MASK]'; hash_salt = 'pepper'
                            }
                            alias        = 'accounts-db'
                        })
                    tags           = @('demo')
                    options        = @{}
                    secret_scanner = @{}
                }
                runner   = [ordered]@{ output_directory = (Join-Path $tmp 'runner-output'); compress = $false; cleanup_staging = $false }
                metadata = @{}
            })

        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20230101T000000Z'

        $result.package_path | Should -BeNullOrEmpty
        $result.files.Count | Should -BeGreaterThan 0 -Because 'expected collected files'
        $exported = Read-JsonFile $result.files[0].destination
        $accountTable = $exported['tables'][0]
        $accountTable['masked_columns'] | Should -Be @('secret')
        $accountTable['rows'][0]['email'].StartsWith('sha256:') | Should -BeTrue

        $result.manifest_path | Should -Not -BeNullOrEmpty
        $manifest = Read-JsonFile $result.manifest_path
        $summary = @($manifest['sources'] | Where-Object { $_['type'] -eq 'sql_snapshot' })[0]
        $summary['alias'] | Should -BeExactly 'accounts-db'
        $summary['tables'] | Should -Be @('accounts')
        $manifest['metadata'].Contains('sql_exports') | Should -BeTrue -Because 'expected sql metadata entries in manifest'
        $entry = @($manifest['metadata']['sql_exports'] | Where-Object { $_['alias'] -eq 'accounts-db' })[0]
        $entry['masked_columns']['accounts'] | Should -Be @('secret')
        @($entry['masked_columns'].Keys) | Should -Be @('accounts')
        $entry['hashed_columns']['accounts'] | Should -Be @('email')
        @($entry['hashed_columns'].Keys) | Should -Be @('accounts')
        $entry['placeholder'] | Should -BeExactly '[MASK]'
    }

    It 'offline runner sql snapshot optional' {
        $tmp = Get-TestDirectory
        $config = ConvertTo-TestConfig ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{
                    name           = 'sql-optional'
                    description    = 'optional'
                    sources        = @([ordered]@{ sql_snapshot = [ordered]@{ path = (Join-Path $tmp 'missing.sqlite'); optional = $true } })
                    tags           = @()
                    options        = @{}
                    secret_scanner = @{}
                }
                runner   = [ordered]@{ output_directory = (Join-Path $tmp 'optional-output'); compress = $false; cleanup_staging = $false }
                metadata = @{}
            })

        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20230102T000000Z'

        $result.files.Count | Should -Be 0
        $result.manifest_path | Should -Not -BeNullOrEmpty
        $manifest = Read-JsonFile $result.manifest_path
        $summary = @($manifest['sources'] | Where-Object { $_['type'] -eq 'sql_snapshot' })[0]
        $summary['skipped'] | Should -BeTrue
        $summary['reason'] | Should -BeExactly 'missing'
    }

    It 'loads SQLite from the platform library' {
        $expected = $(if ([DriftBusterOfflineRunner.EngineOs]::Windows) { 'winsqlite3' } else { 'libsqlite3.so.0' })
        [DriftBusterOfflineRunner.SqliteDatabase]::LibraryName | Should -BeExactly $expected
    }
}

Describe 'live registry hives' {
    It 'offline runner uses explicit roots' {
        $tmp = Get-TestDirectory
        $config = ConvertTo-TestConfig ([ordered]@{
                schema   = $script:ConfigSchema
                profile  = [ordered]@{
                    name           = 'registry-only'
                    sources        = @([ordered]@{
                            registry_scan = [ordered]@{ token = 'VendorA'; roots = @([ordered]@{ hive = 'HKLM'; path = 'Software\\VendorA'; view = '64' }) }
                        })
                    options        = @{}
                    secret_scanner = @{}
                }
                runner   = [ordered]@{ output_directory = (Join-Path $tmp 'out'); compress = $false; cleanup_staging = $false }
                metadata = @{}
            })

        Mock Test-DbWindowsPlatform { $true }
        Mock Get-DbAppRegistryRoot { throw 'find_app_registry_roots should not run when roots are supplied' }
        Mock Get-DbInstalledApp { throw 'find_app_registry_roots should not run when roots are supplied' }
        Mock Search-DbRegistry {
            return , @([pscustomobject]@{ hive = 'HKLM'; path = 'Software\\VendorA'; value_name = 'Server'; data_preview = 'api.internal'; reason = 'keyword' })
        }

        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20250312T010101Z'
        Should -Invoke Search-DbRegistry -Times 1 -Exactly -ParameterFilter {
            @($Roots).Count -eq 1 -and $Roots[0].hive -ceq 'HKLM' -and $Roots[0].path -ceq 'Software\\VendorA' -and $Roots[0].view -ceq '64'
        }

        $result.manifest_path | Should -Not -BeNullOrEmpty
        $manifest = Read-JsonFile $result.manifest_path
        $summary = $manifest['sources'][0]
        $summary['type'] | Should -BeExactly 'registry_scan'
        @($summary['roots'] | ForEach-Object { $_.Replace('\\', '\') }) | Should -Be @('HKLM \ Software\VendorA')
        @($summary['requested_roots'] | ForEach-Object { $_.Replace('\\', '\') }) | Should -Be @('HKLM \ Software\VendorA (view 64)')

        $result.staging_dir | Should -Not -BeNullOrEmpty
        $alias = Get-DbDestinationName -Source $config.profile.sources[0] -FallbackIndex 1
        $dataPath = Join-TestPath $result.staging_dir @($config.settings.data_directory_name, $alias, 'registry_scan.json')
        $payload = Read-JsonFile $dataPath
        $payload['requested_roots'][0]['view'] | Should -BeExactly '64'
    }
}

Describe 'OfflineRegistryScanSource' {
    It 'remote schema parses single target' {
        $payload = ConvertTo-EngineValue ([ordered]@{
                alias         = 'hq-remote'
                registry_scan = [ordered]@{
                    token  = 'VendorA'
                    remote = [ordered]@{
                        host = 'hq-gateway'; username = 'DOMAIN\collector'; password_env = 'DRIFTBUSTER_REMOTE_PASS'; transport = 'winrm'
                        port = 5986; use_ssl = $true; credential_profile = 'hq-collector'
                    }
                }
            })

        $source = ConvertFrom-DbOfflineRegistryScanSource $payload
        $source.remote | Should -Not -BeNullOrEmpty
        $source.remote.host | Should -BeExactly 'hq-gateway'
        $source.remote.username | Should -BeExactly 'DOMAIN\collector'
        $source.remote.password_env | Should -BeExactly 'DRIFTBUSTER_REMOTE_PASS'
        $source.remote.transport | Should -BeExactly 'winrm'
        $source.remote.port | Should -Be 5986
        $source.remote.use_ssl | Should -BeExactly $true
        $source.remote.credential_profile | Should -BeExactly 'hq-collector'
    }

    It 'remote schema supports batch targets' {
        $payload = ConvertTo-EngineValue ([ordered]@{
                registry_scan = [ordered]@{
                    token        = 'VendorA'
                    remote       = 'branch-gateway'
                    remote_batch = @(
                        [ordered]@{ host = 'branch-01'; username = 'svc-collector' },
                        'branch-02',
                        [ordered]@{ host = 'branch-03'; use_ssl = $false; transport = 'winrm'; port = 5985 }
                    )
                }
            })

        $source = ConvertFrom-DbOfflineRegistryScanSource $payload
        $source.remote | Should -Not -BeNullOrEmpty
        $source.remote.host | Should -BeExactly 'branch-gateway'
        $source.remote_batch.Count | Should -Be 3
        @($source.remote_batch | ForEach-Object { $_.host }) | Should -Be @('branch-01', 'branch-02', 'branch-03')
        $source.remote_batch[0].username | Should -BeExactly 'svc-collector'
        $source.remote_batch[2].use_ssl | Should -BeExactly $false
        $source.remote_batch[2].port | Should -Be 5985
    }

    It 'remote schema rejects inline passwords' {
        $payload = ConvertTo-EngineValue ([ordered]@{
                registry_scan = [ordered]@{ token = 'VendorA'; remote = [ordered]@{ host = 'forbidden'; password = 'super-secret' } }
            })
        Assert-EngineError { ConvertFrom-DbOfflineRegistryScanSource $payload } 'InvalidDataException'
    }

    It 'remote batch allows mapping payload' {
        $payload = ConvertTo-EngineValue ([ordered]@{
                registry_scan = [ordered]@{ token = 'VendorA'; remote_batch = [ordered]@{ host = 'branch-unique'; credential_profile = 'branch-profile' } }
            })

        $source = ConvertFrom-DbOfflineRegistryScanSource $payload
        $source.remote | Should -BeNullOrEmpty
        $source.remote_batch.Count | Should -Be 1
        $target = $source.remote_batch[0]
        $target.host | Should -BeExactly 'branch-unique'
        $target.transport | Should -BeExactly 'winrm'
        $target.port | Should -BeNullOrEmpty
        $target.use_ssl | Should -BeNullOrEmpty
        $target.username | Should -BeNullOrEmpty
        $target.password_env | Should -BeNullOrEmpty
        $target.credential_profile | Should -BeExactly 'branch-profile'
        $target.alias | Should -BeNullOrEmpty
    }
}

Describe 'Windows registry scan' -Tag 'Windows' {
    BeforeAll {
        $script:RegistryTestKey = 'Software\DriftBusterOfflineRunnerTest'
    }

    AfterAll {
        if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($script:RegistryTestKey, $false)
        }
    }

    It 'scans explicit roots breadth-first with keywords and patterns' -Skip:(-not $script:OnWindows) {
        $root = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($script:RegistryTestKey)
        try {
            $root.SetValue('Server', 'api.internal', [Microsoft.Win32.RegistryValueKind]::String)
            $root.SetValue('Port', 8443, [Microsoft.Win32.RegistryValueKind]::DWord)
            $root.SetValue('Flags', [string[]]@('alpha', 'beta'), [Microsoft.Win32.RegistryValueKind]::MultiString)
            $root.SetValue('Blob', [System.Text.Encoding]::UTF8.GetBytes('server-blob'), [Microsoft.Win32.RegistryValueKind]::Binary)
            $child = $root.CreateSubKey('Nested')
            $child.SetValue('BackupServer', 'backup.internal', [Microsoft.Win32.RegistryValueKind]::ExpandString)
            $child.SetValue('Negative', -1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $child.Dispose()
        }
        finally {
            $root.Dispose()
        }

        $tmp = Get-TestDirectory
        $config = ConvertTo-TestConfig ([ordered]@{
                profile = [ordered]@{
                    name    = 'registry-live'
                    sources = @([ordered]@{
                            registry_scan = [ordered]@{ token = 'DriftBusterOfflineRunnerTest'; keywords = @('server'); roots = @("HKCU\$($script:RegistryTestKey)") }
                        })
                }
                runner  = [ordered]@{ output_directory = (Join-Path $tmp 'out'); compress = $false; cleanup_staging = $false }
            })
        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20250101T000000Z'

        $scan = Read-JsonFile $result.files[0].destination
        @($scan['hits'] | ForEach-Object { $_['value_name'] }) | Should -Be @('Server', 'Blob', 'BackupServer')
        @($scan['hits'] | ForEach-Object { $_['data_preview'] }) | Should -Be @('api.internal', 'server-blob', 'backup.internal')
        $scan['hits'][2]['path'] | Should -BeExactly "$($script:RegistryTestKey)\Nested"
        $scan['requested_roots'][0]['view'] | Should -BeNullOrEmpty

        $values = Get-DbRegistryValue -Hive 'HKCU' -Path "$($script:RegistryTestKey)\Nested" -View $null
        ($values | Where-Object { $_.Name -eq 'Negative' }).Data | Should -Be 4294967295
        $rootValues = Get-DbRegistryValue -Hive 'HKCU' -Path $script:RegistryTestKey -View $null
        $flags = ($rootValues | Where-Object { $_.Name -eq 'Flags' }).Data
        Get-DbRegistryValueText $flags | Should -BeExactly 'alpha, beta'

        $patterned = (Search-DbRegistry -Roots @([pscustomobject]@{ hive = 'HKCU'; path = $script:RegistryTestKey; view = $null }) -Spec ([pscustomobject]@{
                    keywords = @(); patterns = @([regex]::new('^\d+$')); max_depth = 0; max_hits = 200; time_budget_s = 10.0
                }))
        @($patterned | ForEach-Object { $_.value_name }) | Should -Be @('Port')
    }

    It 'reads value types RegistryKey.GetValue leaves null as winreg reads them' -Skip:(-not $script:OnWindows) {
        if (-not ('DriftBusterOfflineRunnerTests.RawRegistry' -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace DriftBusterOfflineRunnerTests
{
    public static class RawRegistry
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
        public static extern int RegSetValueEx(SafeHandle key, string name, int reserved, int type, byte[] data, int size);
    }
}
"@
        }

        $keyPath = "$($script:RegistryTestKey)\RawTypes"
        $prefix = [System.Text.Encoding]::ASCII.GetBytes('server-')
        $values = [ordered]@{
            ResList             = @(8, [byte[]]($prefix + [byte[]](0x01, 0x00, 0xED, 0xA0, 0x80)))
            FullRes             = @(9, [byte[]]($prefix + [byte[]](0xC0, 0xAF, 0x41)))
            ReqList             = @(10, [byte[]]($prefix + [byte[]](0xF4, 0x90, 0x80, 0x80)))
            Link                = @(6, [System.Text.Encoding]::ASCII.GetBytes('link-server'))
            Custom              = @(0x1234, [byte[]]($prefix + [byte[]](0xE2, 0x82)))
            'server-empty-list' = @(8, [byte[]]@())
            OddSz               = @(1, [System.Text.Encoding]::Unicode.GetBytes("server-odd`0"))
        }
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyPath)
        try {
            foreach ($name in $values.Keys) {
                $data = [byte[]]$values[$name][1]
                [DriftBusterOfflineRunnerTests.RawRegistry]::RegSetValueEx($key.Handle, $name, 0, $values[$name][0], $data, $data.Length) | Should -Be 0
            }
        }
        finally {
            $key.Dispose()
        }

        $r = [string][char]0xFFFD
        $hits = Search-DbRegistry -Roots @([pscustomobject]@{ hive = 'HKCU'; path = $keyPath; view = $null }) -Spec ([pscustomobject]@{
                keywords = @('server'); patterns = @(); max_depth = 0; max_hits = 200; time_budget_s = 10.0
            })
        @($hits | ForEach-Object { $_.value_name }) | Should -Be @('ResList', 'FullRes', 'ReqList', 'Link', 'Custom', 'OddSz')
        @($hits | ForEach-Object { $_.data_preview }) | Should -Be @(
            "server-$([char]1)$([char]0)$($r * 3)", "server-$($r * 2)A", "server-$($r * 4)", 'link-server', "server-$r", 'server-odd')

        $read = Get-DbRegistryValue -Hive 'HKCU' -Path $keyPath -View $null
        ($read | Where-Object { $_.Name -eq 'server-empty-list' }).Data | Should -BeNullOrEmpty
    }
}

Describe 'DPAPI keysets' -Tag 'Windows' {
    It 'decrypts dpapi key entries for the current user and the machine' -Skip:(-not $script:OnWindows) {
        Add-Type -AssemblyName System.Security
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'secrets.txt') -Content 'token-789'
        $aesKey = Get-RepeatedByte 'E' 32
        $hmacKey = Get-RepeatedByte 'F' 40
        $aesBlob = [System.Security.Cryptography.ProtectedData]::Protect($aesKey, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $hmacBlob = [System.Security.Cryptography.ProtectedData]::Protect($hmacKey, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        $keysetPath = Join-Path $tmp 'keyset.json'
        Write-TestText -Path $keysetPath -Content ([DriftBusterOfflineRunner.EngineJson]::Dumps([ordered]@{
                    schema   = $script:KeysetSchema
                    aes_key  = [ordered]@{ encoding = 'dpapi'; data = [System.Convert]::ToBase64String($aesBlob) }
                    hmac_key = [ordered]@{ encoding = 'dpapi'; scope = 'machine'; data = [System.Convert]::ToBase64String($hmacBlob); min_length = 40 }
                }, 2, $false))

        $config = ConvertTo-TestConfig ([ordered]@{
                profile = [ordered]@{ name = 'dpapi'; sources = @((Join-Path $tmp 'secrets.txt')) }
                runner  = [ordered]@{
                    output_directory = (Join-Path $tmp 'out'); cleanup_staging = $false
                    encryption = [ordered]@{ enabled = $true; keyset_path = $keysetPath }
                }
            })
        $result = Invoke-DbOfflineRunner -Config $config -BaseDir $tmp -Timestamp '20250101T000000Z'

        $payload = Read-JsonFile $result.package_path
        $iv = [System.Convert]::FromBase64String($payload['iv'])
        $ciphertext = [System.Convert]::FromBase64String($payload['ciphertext'])
        [System.Convert]::ToBase64String([System.Convert]::FromBase64String($payload['mac'])) |
            Should -BeExactly ([System.Convert]::ToBase64String((Get-TestHmac -Key $hmacKey -Iv $iv -Ciphertext $ciphertext)))
        $names = Get-ZipEntryName -Bytes (Unprotect-TestCiphertext -AesKey $aesKey -Iv $iv -Ciphertext $ciphertext)
        $names | Should -Contain 'data/secrets-txt/secrets.txt'
    }
}

Describe 'driftbuster-offline-runner.ps1' {
    It 'runs a config file and reports the package' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-TestPath $tmp @('bundle', 'app.config')) -Content "password = SUPERSECRET123456`nkeep`n"
        $configPath = Write-TestConfig -Directory (Join-Path $tmp 'bundle') -Payload ([ordered]@{
                profile = [ordered]@{ name = 'script entry'; sources = @('app.config') }
                runner  = [ordered]@{ package_name = 'collected' }
            })

        $output = & $script:RunnerScript -ConfigPath $configPath -OutputDirectory (Join-Path $tmp 'packages')

        $output.PackagePath | Should -BeExactly ([DriftBusterOfflineRunner.EnginePath]::Join((Join-Path $tmp 'packages'), 'collected.zip'))
        $output.FilesCollected | Should -Be 1
        $output.StagingDirectory | Should -BeNullOrEmpty
        $manifest = Read-ManifestFromPackage $output.PackagePath
        $manifest['config']['path'] | Should -BeExactly ([DriftBusterOfflineRunner.EnginePath]::Normalise($configPath))
        $manifest['secrets']['findings'].Count | Should -Be 1
        Read-ZipText -ZipPath $output.PackagePath -EntryName 'data/app-config/app.config' | Should -Match '\[SECRET\]'
    }
}
