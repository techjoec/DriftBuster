<#
  Pester 5 tests for driftbuster-offline-runner.ps1 (dot-sourced for its functions, run as a script for the entry point).

  Covers strict config and keyset reading, file and glob collection, the secret filter, SQL snapshots, registry scans (local and
  over WinRM, both with the registry calls mocked), package encryption and the script entry point. Tests tagged 'Windows' (live
  registry, DPAPI) are skipped elsewhere; SQL tests use winsqlite3 on Windows and libsqlite3.so.0 on Linux.

  Run: Invoke-Pester -Path scripts/DriftBusterOfflineRunner.Tests.ps1 -Output Detailed
#>

BeforeDiscovery {
    $script:OnWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'driftbuster-offline-runner.ps1') -ConfigPath 'dot-sourced'
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

    $script:RepoRoot = Split-Path -Path $PSScriptRoot -Parent
    $script:RunnerScript = Join-Path -Path $PSScriptRoot -ChildPath 'driftbuster-offline-runner.ps1'
    $script:ConfigSchema = 'https://driftbuster.dev/offline-runner/config/v1'
    $script:ManifestSchema = 'https://driftbuster.dev/offline-runner/manifest/v1'
    $script:KeysetSchema = 'https://driftbuster.dev/offline-runner/encryption/keyset/v1'
    $script:EncryptedSchema = 'https://driftbuster.dev/offline-runner/encryption/dpapi-aes/v1'

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

    function Write-TestText {
        param([string] $Path, [string] $Content)
        [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path))
        [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
    }

    function Write-TestJson {
        param([string] $Path, $Payload)
        Write-TestText -Path $Path -Content (ConvertTo-Json -InputObject $Payload -Depth 32)
        return $Path
    }

    # The config written as config.json in Directory and read back through the runner's strict reader.
    function Import-TestConfig {
        param([string] $Directory, $Payload, [string] $Name = 'config.json')
        return Import-DBConfig (Write-TestJson -Path (Join-Path $Directory $Name) -Payload $Payload)
    }

    # A config with the given sources that writes into <Directory>/out and keeps its staging directory.
    function Import-SourceConfig {
        param([string] $Directory, [object[]] $Sources, $Runner, $SecretScanner, [string] $Name = 'test run')
        if ($null -eq $Runner) {
            $Runner = [ordered]@{ output_directory = 'out'; compress = $false; cleanup_staging = $false }
        }

        $profilePayload = [ordered]@{ name = $Name; sources = $Sources }
        if ($null -ne $SecretScanner) {
            $profilePayload['secret_scanner'] = $SecretScanner
        }

        return Import-TestConfig -Directory $Directory -Payload ([ordered]@{ schema = $script:ConfigSchema; profile = $profilePayload; runner = $Runner })
    }

    function Read-JsonFile {
        param([string] $Path)
        return ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($Path))
    }

    # The message of the exception the script block throws.
    function Get-ThrownMessage {
        param([scriptblock] $Script)
        try {
            & $Script | Out-Null
        }
        catch {
            return $_.Exception.Message
        }

        throw 'expected an exception'
    }

    function Read-ZipText {
        param([string] $ZipPath, [string] $EntryName)
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
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

    function Get-ZipEntryName {
        param([byte[]] $Bytes)
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

    function Get-RepeatedByte {
        param([char] $Character, [int] $Count)
        return , [byte[]]([System.Text.Encoding]::ASCII.GetBytes([string]::new($Character, $Count)))
    }

    function Write-TestKeyset {
        param([string] $Path, [byte[]] $AesKey, [byte[]] $HmacKey)
        [void](Write-TestJson -Path $Path -Payload ([ordered]@{
                    schema   = $script:KeysetSchema
                    aes_key  = [ordered]@{ encoding = 'base64'; data = [System.Convert]::ToBase64String($AesKey) }
                    hmac_key = [ordered]@{ encoding = 'hex'; data = [System.BitConverter]::ToString($HmacKey).Replace('-', '') }
                }))
        return $Path
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

    # The encrypted package's zip entry names, after checking its MAC.
    function Read-EncryptedPackage {
        param([string] $Path, [byte[]] $AesKey, [byte[]] $HmacKey)
        $payload = Read-JsonFile $Path
        $payload.schema | Should -BeExactly $script:EncryptedSchema
        $payload.algorithm | Should -BeExactly 'aes-256-cbc+hmac-sha256'
        $iv = [System.Convert]::FromBase64String($payload.iv)
        $ciphertext = [System.Convert]::FromBase64String($payload.ciphertext)
        $payload.mac | Should -BeExactly ([System.Convert]::ToBase64String((Get-TestHmac -Key $HmacKey -Iv $iv -Ciphertext $ciphertext)))
        return , (Get-ZipEntryName -Bytes (Unprotect-TestCiphertext -AesKey $AesKey -Iv $iv -Ciphertext $ciphertext))
    }

    function Get-Sha256Hex {
        param([string] $Text)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [System.BitConverter]::ToString($sha.ComputeHash([System.Text.UTF8Encoding]::new($false).GetBytes($Text))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }

    # The sample accounts database fixtures/sql/README.md documents.
    function Initialize-SampleDatabase {
        param([string] $Path)
        [DriftBusterOfflineRunner.SqlSnapshot]::Execute($Path, [string[]]@(
                'CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL)',
                "INSERT INTO accounts (email, secret, balance) VALUES ('alice@example.com', 'token-1', 42.5)",
                "INSERT INTO accounts (email, secret, balance) VALUES ('bob@example.com', 'token-2', 13.75)"
            ))
        return $Path
    }
}

Describe 'config reading' {
    It 'fills every default' {
        $tmp = Get-TestDirectory
        $config = Import-TestConfig -Directory $tmp -Payload ([ordered]@{ profile = [ordered]@{ name = 'defaults'; sources = @(@{ path = 'app.config' }) } })

        $config.schema | Should -BeExactly $script:ConfigSchema
        $config.version | Should -BeExactly '1'
        $config.path | Should -BeExactly (Join-Path $tmp 'config.json')
        $source = $config.profile.sources[0]
        $source.kind | Should -BeExactly 'file'
        $source.alias | Should -BeNullOrEmpty
        $source.optional | Should -BeFalse
        $source.exclude.Count | Should -Be 0
        $config.profile.secret_scanner.ruleset | Should -BeNullOrEmpty
        $runner = $config.runner
        $runner.compress | Should -BeTrue
        $runner.include_config | Should -BeTrue
        $runner.include_logs | Should -BeTrue
        $runner.include_manifest | Should -BeTrue
        $runner.cleanup_staging | Should -BeTrue
        $runner.manifest_name | Should -BeExactly 'manifest.json'
        $runner.log_name | Should -BeExactly 'runner.log'
        $runner.data_directory_name | Should -BeExactly 'data'
        $runner.logs_directory_name | Should -BeExactly 'logs'
        $runner.max_total_bytes | Should -BeNullOrEmpty
        $runner.encryption | Should -BeNullOrEmpty
    }

    It 'reads registry scan, SQL snapshot and encryption settings' {
        $tmp = Get-TestDirectory
        $config = Import-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile = [ordered]@{
                    name    = 'mixed'
                    sources = @(
                        [ordered]@{
                            registry_scan = [ordered]@{
                                token        = ' VendorA '
                                keywords     = @('server')
                                patterns     = @('api\.')
                                roots        = @('HKLM/Software/VendorA, view=64', [ordered]@{ hive = 'hkcu'; path = 'Software\VendorA'; view = 'auto' })
                                remote       = 'app-01'
                                remote_batch = @('app-02', [ordered]@{ host = 'app-03'; port = 5986; use_ssl = $true; alias = 'third' })
                            }
                        },
                        [ordered]@{
                            alias        = 'db'
                            sql_snapshot = [ordered]@{ path = 'app.sqlite'; mask_columns = [ordered]@{ accounts = @('secret') }; limit = 5 }
                        })
                }
                runner  = [ordered]@{ encryption = [ordered]@{ keyset_path = 'keys.json'; output_extension = 'sealed' } }
            })

        $scan = $config.profile.sources[0]
        $scan.kind | Should -BeExactly 'registry_scan'
        $scan.token | Should -BeExactly 'VendorA'
        $scan.max_depth | Should -Be 12
        $scan.max_hits | Should -Be 200
        $scan.time_budget_s | Should -Be 10.0
        $scan.roots[0].hive | Should -BeExactly 'HKLM'
        $scan.roots[0].path | Should -BeExactly 'Software\VendorA'
        $scan.roots[0].view | Should -BeExactly '64'
        $scan.roots[1].hive | Should -BeExactly 'HKCU'
        $scan.roots[1].view | Should -BeNullOrEmpty
        @($scan.targets | ForEach-Object { $_.host }) | Should -Be @('app-01', 'app-02', 'app-03')
        $scan.targets[0].transport | Should -BeExactly 'winrm'
        $scan.targets[2].port | Should -Be 5986
        $scan.targets[2].use_ssl | Should -BeTrue
        $scan.targets[2].alias | Should -BeExactly 'third'

        $sql = $config.profile.sources[1]
        $sql.kind | Should -BeExactly 'sql_snapshot'
        $sql.dialect | Should -BeExactly 'sqlite'
        $sql.placeholder | Should -BeExactly '[REDACTED]'
        $sql.hash_salt | Should -BeExactly ''
        $sql.limit | Should -Be 5
        $sql.mask_columns['accounts'] | Should -Be @('secret')

        $config.runner.encryption.enabled | Should -BeTrue
        $config.runner.encryption.mode | Should -BeExactly 'dpapi-aes'
        $config.runner.encryption.output_extension | Should -BeExactly '.sealed'
        $config.runner.encryption.remove_plaintext | Should -BeTrue
    }

    It 'refuses <Case>' -ForEach @(
        @{ Case = 'an unknown top-level key'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}]}, "extra": 1}'; JsonPath = '$.extra'; Message = 'unknown key' }
        @{ Case = 'a misspelt source key'; Json = '{"profile": {"name": "p", "sources": [{"path": "a", "exlude": []}]}}'; JsonPath = '$.profile.sources[0].exlude'; Message = 'unknown key' }
        @{ Case = 'a key in the wrong case'; Json = '{"profile": {"name": "p", "sources": [{"Path": "a"}]}}'; JsonPath = '$.profile.sources[0].Path'; Message = 'unknown key' }
        @{ Case = 'a source given as a string'; Json = '{"profile": {"name": "p", "sources": ["a"]}}'; JsonPath = '$.profile.sources[0]'; Message = 'expected an object' }
        @{ Case = 'a profile without sources'; Json = '{"profile": {"name": "p", "sources": []}}'; JsonPath = '$.profile.sources'; Message = 'at least one source is required' }
        @{ Case = 'a missing profile'; Json = '{"runner": {}}'; JsonPath = '$.profile'; Message = 'required' }
        @{ Case = 'a blank profile name'; Json = '{"profile": {"name": " ", "sources": [{"path": "a"}]}}'; JsonPath = '$.profile.name'; Message = 'must not be blank' }
        @{ Case = 'another schema'; Json = '{"schema": "v2", "profile": {"name": "p", "sources": [{"path": "a"}]}}'; JsonPath = '$.schema'; Message = 'expected https://driftbuster.dev/offline-runner/config/v1' }
        @{ Case = 'a text flag'; Json = '{"profile": {"name": "p", "sources": [{"path": "a", "optional": "yes"}]}}'; JsonPath = '$.profile.sources[0].optional'; Message = 'expected true or false' }
        @{ Case = 'a non-string exclude'; Json = '{"profile": {"name": "p", "sources": [{"path": "a", "exclude": ["*.log", 3]}]}}'; JsonPath = '$.profile.sources[0].exclude[1]'; Message = 'expected a string' }
        @{ Case = 'a zero byte limit'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}]}, "runner": {"max_total_bytes": 0}}'; JsonPath = '$.runner.max_total_bytes'; Message = 'must be positive' }
        @{ Case = 'a text byte limit'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}]}, "runner": {"max_total_bytes": "10"}}'; JsonPath = '$.runner.max_total_bytes'; Message = 'expected a whole number' }
        @{ Case = 'a baseline that is not a source'; Json = '{"profile": {"name": "p", "baseline": "b", "sources": [{"path": "a"}]}}'; JsonPath = '$.profile.baseline'; Message = 'must be one of the source paths' }
        @{ Case = 'a non-string option'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}], "options": {"depth": 2}}}'; JsonPath = '$.profile.options.depth'; Message = 'expected a string' }
        @{ Case = 'metadata that is not an object'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}]}, "metadata": []}'; JsonPath = '$.metadata'; Message = 'expected an object' }
        @{ Case = 'encryption without compress'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}]}, "runner": {"compress": false, "encryption": {"keyset_path": "k"}}}'; JsonPath = '$.runner.encryption'; Message = 'encryption needs compress' }
        @{ Case = 'encryption without a keyset'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}]}, "runner": {"encryption": {}}}'; JsonPath = '$.runner.encryption.keyset_path'; Message = 'required when encryption is enabled' }
        @{ Case = 'another encryption mode'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}]}, "runner": {"encryption": {"mode": "gpg", "keyset_path": "k"}}}'; JsonPath = '$.runner.encryption.mode'; Message = "only 'dpapi-aes' is supported" }
        @{ Case = 'another SQL dialect'; Json = '{"profile": {"name": "p", "sources": [{"sql_snapshot": {"path": "a", "dialect": "postgres"}}]}}'; JsonPath = '$.profile.sources[0].sql_snapshot.dialect'; Message = "only 'sqlite' is supported" }
        @{ Case = 'a zero row limit'; Json = '{"profile": {"name": "p", "sources": [{"sql_snapshot": {"path": "a", "limit": 0}}]}}'; JsonPath = '$.profile.sources[0].sql_snapshot.limit'; Message = 'must be positive' }
        @{ Case = 'a column list that is not a list'; Json = '{"profile": {"name": "p", "sources": [{"sql_snapshot": {"path": "a", "mask_columns": {"accounts": "secret"}}}]}}'; JsonPath = '$.profile.sources[0].sql_snapshot.mask_columns.accounts'; Message = 'expected an array of strings' }
        @{ Case = 'a registry root in another hive'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "roots": ["HKCR\\x"]}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.roots[0]'; Message = 'expected HKLM\<path> or HKCU\<path>, optionally followed by ,view=32, ,view=64 or ,view=auto' }
        @{ Case = 'a registry view of 16'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "roots": [{"hive": "HKLM", "path": "x", "view": "16"}]}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.roots[0]'; Message = 'view must be 32, 64 or auto' }
        @{ Case = 'a registry scan without a token'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.token'; Message = 'required' }
        @{ Case = 'the ssh transport'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "remote": {"host": "h", "transport": "ssh"}}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.remote.transport'; Message = "'ssh' is not supported; use winrm" }
        @{ Case = 'an inline password'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "remote": {"host": "h", "username": "u", "password": "p"}}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.remote.password'; Message = 'unknown key' }
        @{ Case = 'password_env without a username'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "remote_batch": [{"host": "h", "password_env": "X"}]}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.remote_batch[0]'; Message = 'password_env needs a username' }
        @{ Case = 'a username without a password source'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "remote": {"host": "h", "username": "u"}}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.remote'; Message = 'username needs password_env or credential_profile' }
        @{ Case = 'both password sources'; Json = '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "remote": {"host": "h", "username": "u", "password_env": "X", "credential_profile": "c.xml"}}}]}}'; JsonPath = '$.profile.sources[0].registry_scan.remote'; Message = 'use password_env or credential_profile, not both' }
        @{ Case = 'a ruleset rule without a pattern'; Json = '{"profile": {"name": "p", "sources": [{"path": "a"}], "secret_scanner": {"ruleset": {"rules": [{"name": "r"}]}}}}'; JsonPath = '$.profile.secret_scanner.ruleset.rules[0].pattern'; Message = 'required' }
    ) {
        $path = Join-Path (Get-TestDirectory) 'config.json'
        Write-TestText -Path $path -Content $Json
        Get-ThrownMessage { Import-DBConfig $path } | Should -BeExactly "${path}: ${JsonPath}: $Message"
    }

    It 'names the file and the pattern that does not compile' {
        $path = Join-Path (Get-TestDirectory) 'config.json'
        Write-TestText -Path $path -Content '{"profile": {"name": "p", "sources": [{"registry_scan": {"token": "t", "patterns": ["ok", "(unclosed"]}}]}}'
        (Get-ThrownMessage { Import-DBConfig $path }).StartsWith("${path}: `$.profile.sources[0].registry_scan.patterns[1]: ", [System.StringComparison]::Ordinal) | Should -BeTrue
    }

    It 'names the file that is not JSON' {
        $path = Join-Path (Get-TestDirectory) 'config.json'
        Write-TestText -Path $path -Content '{"profile": '
        $caught = $null
        try {
            Import-DBConfig $path
        }
        catch {
            $caught = $_.Exception
        }

        $caught | Should -BeOfType ([System.IO.InvalidDataException])
        $caught.Message.StartsWith("${path}: `$: ", [System.StringComparison]::Ordinal) | Should -BeTrue
    }
}

Describe 'file collection' {
    It 'collects files and trees into alias folders with a manifest' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-TestPath $tmp @('app', 'web.config')) -Content "<configuration />`n"
        Write-TestText -Path (Join-TestPath $tmp @('app', 'conf', 'b.ini')) -Content "[b]`nkey=1`n"
        Write-TestText -Path (Join-TestPath $tmp @('app', 'conf', 'a.ini')) -Content "[a]`nkey=1`n"
        $config = Import-TestConfig -Directory $tmp -Payload ([ordered]@{
                profile  = [ordered]@{
                    name = 'web app'; description = 'demo'; baseline = 'app/web.config'; tags = @('web'); options = [ordered]@{ tier = 'prod' }
                    sources = @([ordered]@{ path = 'app/web.config' }, [ordered]@{ path = 'app/conf'; alias = 'my conf' })
                }
                runner   = [ordered]@{ output_directory = 'out'; compress = $false; cleanup_staging = $false }
                metadata = [ordered]@{ ticket = 'CHG-1' }
            })

        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $result.StagingDirectory | Should -BeExactly (Join-TestPath $tmp @('out', 'web-app-20250101T000000Z'))
        $result.PackagePath | Should -BeNullOrEmpty
        $result.FilesCollected | Should -Be 3
        Test-Path -LiteralPath (Join-TestPath $result.StagingDirectory @('data', 'source_00', 'web.config')) | Should -BeTrue
        Test-Path -LiteralPath (Join-TestPath $result.StagingDirectory @('data', 'my-conf', 'a.ini')) | Should -BeTrue
        Test-Path -LiteralPath (Join-TestPath $result.StagingDirectory @('config.json')) | Should -BeTrue
        Get-Content -LiteralPath $result.LogPath | Where-Object { $_ -like '*collected 2 items from app/conf' } | Should -Not -BeNullOrEmpty

        $manifest = Read-JsonFile $result.ManifestPath
        $manifest.schema | Should -BeExactly $script:ManifestSchema
        $manifest.timestamp | Should -BeExactly '20250101T000000Z'
        $manifest.profile.name | Should -BeExactly 'web app'
        $manifest.profile.baseline | Should -BeExactly 'app/web.config'
        $manifest.profile.options.tier | Should -BeExactly 'prod'
        $manifest.runner.schema | Should -BeExactly $script:ConfigSchema
        $manifest.config.sha256 | Should -BeExactly (Get-DBFileHash $config.path)
        $manifest.metadata.ticket | Should -BeExactly 'CHG-1'
        @($manifest.files | ForEach-Object { $_.relative_path }) | Should -Be @('source_00/web.config', 'my-conf/a.ini', 'my-conf/b.ini')
        $manifest.files[0].sha256 | Should -BeExactly (Get-DBFileHash (Join-TestPath $tmp @('app', 'web.config')))
        $manifest.sources[1].alias | Should -BeExactly 'my-conf'
        @($manifest.sources[1].matched) | Should -Be @('a.ini', 'b.ini')
        $manifest.sources[1].skipped | Should -BeFalse
        $manifest.secrets.rules_loaded | Should -BeTrue
        $manifest.secrets.ruleset_version | Should -BeExactly '2024-06-01'
        $manifest.package.compressed | Should -BeFalse
        $manifest.package.encryption.enabled | Should -BeFalse
    }

    It 'skips a missing optional source and stops on a missing required one' {
        $tmp = Get-TestDirectory
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'missing.txt'; optional = $true }, [ordered]@{ path = 'none/*.txt'; optional = $true })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'
        $manifest = Read-JsonFile $result.ManifestPath
        $manifest.sources[0].skipped | Should -BeTrue
        $manifest.sources[0].reason | Should -BeExactly 'missing'
        $manifest.sources[1].reason | Should -BeExactly 'no-matches'
        $result.FilesCollected | Should -Be 0

        $required = Import-SourceConfig $tmp @([ordered]@{ path = 'missing.txt' }) -Name 'required'
        { Invoke-DBOfflineRunner -Config $required -Timestamp '20250101T000000Z' } | Should -Throw 'Source does not exist: missing.txt'
    }

    It 'matches globs once each and honours exclude patterns' {
        $tmp = Get-TestDirectory
        foreach ($relative in @('logs/a.log', 'logs/keep.txt', 'logs/deep/b.log', 'logs/deep/c.txt')) {
            Write-TestText -Path (Join-TestPath $tmp $relative.Split('/')) -Content $relative
        }

        $config = Import-SourceConfig $tmp @(
            [ordered]@{ path = 'logs/**/*.txt'; alias = 'text' },
            [ordered]@{ path = 'logs'; alias = 'tree'; exclude = @('*.log', 'deep/c.txt') },
            [ordered]@{ path = 'log?'; alias = 'dirs' })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $manifest = Read-JsonFile $result.ManifestPath
        @($manifest.sources[0].matched) | Should -Be @('c.txt', 'keep.txt')
        @($manifest.sources[1].matched) | Should -Be @('keep.txt')
        @($manifest.sources[2].matched) | Should -Be @('a.log', 'deep/b.log', 'deep/c.txt', 'keep.txt')
    }

    It 'stops when the collection passes max_total_bytes' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'big.txt') -Content ('x' * 64)
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'big.txt' }) -Runner ([ordered]@{ output_directory = 'out'; max_total_bytes = 32 })
        { Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z' } | Should -Throw '*max_total_bytes*'
    }

    It 'skips a symbolic link' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'target.txt') -Content 'target'
        try {
            [void](New-Item -ItemType SymbolicLink -Path (Join-Path $tmp 'link.txt') -Target (Join-Path $tmp 'target.txt') -ErrorAction Stop)
        }
        catch {
            Set-ItResult -Skipped -Because "symbolic links cannot be created here: $($_.Exception.Message)"
            return
        }

        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'link.txt' })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'
        $result.FilesCollected | Should -Be 0
        Get-Content -LiteralPath $result.LogPath | Where-Object { $_ -like '*skipping symlink*' } | Should -Not -BeNullOrEmpty
    }

    It 'packages the staging directory and removes it' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'app.config') -Content "keep`n"
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'app.config'; alias = 'app' }) -Runner ([ordered]@{
                output_directory = 'out'; package_name = 'bundle'; include_config = $false })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $result.PackagePath | Should -BeExactly (Join-TestPath $tmp @('out', 'bundle.zip'))
        $result.UnencryptedPackagePath | Should -BeExactly $result.PackagePath
        $result.EncryptedPackagePath | Should -BeNullOrEmpty
        $result.StagingDirectory | Should -BeNullOrEmpty
        $result.ManifestPath | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-TestPath $tmp @('out', 'test-run-20250101T000000Z')) | Should -BeFalse
        $names = Get-ZipEntryName -Bytes ([System.IO.File]::ReadAllBytes($result.PackagePath))
        $names | Should -Be @('data/app/app.config', 'logs/runner.log', 'manifest.json')
        (ConvertFrom-Json (Read-ZipText $result.PackagePath 'manifest.json')).package.package_name | Should -BeExactly 'bundle.zip'
    }
}

Describe 'secret filter' {
    It 'keeps the embedded rules identical to the backend rules file' {
        $packaged = [System.IO.File]::ReadAllText((Join-TestPath $script:RepoRoot @('gui', 'DriftBuster.Backend', 'Resources', 'secret_rules.json')))
        (Get-DBEmbeddedSecretRuleText).Replace("`r`n", "`n").TrimEnd() | Should -BeExactly $packaged.Replace("`r`n", "`n").TrimEnd()
    }

    It 'redacts the secret samples and records each finding' {
        $tmp = Get-TestDirectory
        $fixture = Join-TestPath $script:RepoRoot @('fixtures', 'secret_samples', 'auth_secrets.txt')
        $config = Import-SourceConfig $tmp @([ordered]@{ path = $fixture; alias = 'secrets' })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $result.Findings | Should -Be 3
        $sanitised = [System.IO.File]::ReadAllText((Join-TestPath $result.StagingDirectory @('data', 'secrets', 'auth_secrets.txt')))
        $sanitised | Should -Match ([regex]::Escape('[SECRET]'))
        $sanitised | Should -Not -Match 'SuperSecret1234'
        $sanitised | Should -Not -Match 'ABCDEF1234567890ABCD'
        $sanitised | Should -Not -Match 'AKIA1234567890ABCDEF'
        $sanitised | Should -Match 'Sample credentials for integration tests only'

        $findings = (Read-JsonFile $result.ManifestPath).secrets.findings
        @($findings | ForEach-Object { $_.rule }) | Should -Be @('PasswordAssignment', 'GenericApiToken', 'AwsAccessKeyId')
        @($findings | ForEach-Object { $_.line }) | Should -Be @(2, 3, 4)
        $findings[0].path | Should -BeExactly 'secrets/auth_secrets.txt'
        $findings[0].snippet | Should -BeExactly '[SECRET]'
        @($findings | Where-Object { $_.snippet.Contains('SuperSecret1234') }).Count | Should -Be 0
        Get-Content -LiteralPath $result.LogPath | Where-Object { $_ -like '*scrubbed 3 potential secret line(s) from secrets/auth_secrets.txt' } | Should -Not -BeNullOrEmpty
    }

    It 'honours ignore_rules and ignore_patterns' {
        $tmp = Get-TestDirectory
        # The token line is assembled so secret scanners over this repository do not flag the sample.
        $token = 'AUTH_TOKEN=' + ('ABCDEF' + '1234567890ABCD') + ' # sample'
        Write-TestText -Path (Join-Path $tmp 'app.env') -Content "password=SuperSecret1234`n$token`naws=AKIA1234567890ABCDEF`n"
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'app.env'; alias = 'env' }) -SecretScanner ([ordered]@{
                ignore_rules = @('PasswordAssignment'); ignore_patterns = @('# sample$', '# sample$', '(unclosed') })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $lines = [System.IO.File]::ReadAllLines((Join-TestPath $result.StagingDirectory @('data', 'env', 'app.env')))
        $lines | Should -Be @('password=SuperSecret1234', $token, 'aws=[SECRET]')
        $secrets = (Read-JsonFile $result.ManifestPath).secrets
        @($secrets.ignored_rules) | Should -Be @('PasswordAssignment')
        @($secrets.ignored_patterns) | Should -Be @('# sample$', '(unclosed')
    }

    It 'uses the config ruleset in place of the packaged one' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'app.ini') -Content "password=SuperSecret1234`nlicense=CORP-1234`n"
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'app.ini'; alias = 'ini' }) -SecretScanner ([ordered]@{
                ruleset = [ordered]@{ version = 'custom-1'; rules = @([ordered]@{ name = 'License'; pattern = 'corp-\d+'; flags = 'i' }) } })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        [System.IO.File]::ReadAllLines((Join-TestPath $result.StagingDirectory @('data', 'ini', 'app.ini'))) | Should -Be @('password=SuperSecret1234', 'license=[SECRET]')
        $secrets = (Read-JsonFile $result.ManifestPath).secrets
        $secrets.ruleset_version | Should -BeExactly 'custom-1'
        $secrets.findings[0].rule | Should -BeExactly 'License'
    }

    It 'copies binary files verbatim' {
        $tmp = Get-TestDirectory
        $bytes = [byte[]](@(0, 1, 2, 0) + [System.Text.Encoding]::ASCII.GetBytes('password=SuperSecret1234'))
        [System.IO.File]::WriteAllBytes((Join-Path $tmp 'blob.bin'), $bytes)
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'blob.bin'; alias = 'bin' })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $result.Findings | Should -Be 0
        [System.IO.File]::ReadAllBytes((Join-TestPath $result.StagingDirectory @('data', 'bin', 'blob.bin'))) | Should -Be $bytes
    }
}

Describe 'SQL snapshots' {
    It 'masks and hashes columns as the backend does' {
        $database = Initialize-SampleDatabase (Join-Path (Get-TestDirectory) 'sample.sqlite')
        $snapshot = [DriftBusterOfflineRunner.SqlSnapshot]::Build(
            $database, [string[]]@(), [string[]]@(), @{ accounts = [string[]]@('secret') }, @{ accounts = [string[]]@('email') }, 0, '[MASK]', 'pepper')

        $snapshot['database'] | Should -BeExactly 'sample.sqlite'
        $snapshot['dialect'] | Should -BeExactly 'sqlite'
        $accounts = $snapshot['tables'][0]
        $accounts['name'] | Should -BeExactly 'accounts'
        $accounts['row_count'] | Should -Be 2
        @($accounts['columns']) | Should -Be @('id', 'email', 'secret', 'balance')
        $accounts['rows'][0]['secret'] | Should -BeExactly '[MASK]'
        $accounts['rows'][0]['email'] | Should -BeExactly ('sha256:' + (Get-Sha256Hex 'accounts.email:pepper"alice@example.com"'))
        $accounts['rows'][0]['balance'] | Should -Be 42.5
        $accounts['rows'][1]['id'] | Should -Be 2
    }

    It 'escapes hashed text the way System.Text.Json writes it' {
        $text = 'a<b>&''+"' + [char]0xE9 + "`n"
        $escaped = 's"a|u003Cb|u003E|u0026|u0027|u002B|u0022|u00E9|n"'.Replace('|', [string][char]92)
        [DriftBusterOfflineRunner.SqlSnapshot]::HashValue($text, 's') | Should -BeExactly ('sha256:' + (Get-Sha256Hex $escaped))
        [DriftBusterOfflineRunner.SqlSnapshot]::HashValue($null, '') | Should -BeExactly ('sha256:' + (Get-Sha256Hex 'null'))
    }

    It 'limits rows, picks tables and writes BLOBs as base64' {
        $database = Initialize-SampleDatabase (Join-Path (Get-TestDirectory) 'limited.sqlite')
        [DriftBusterOfflineRunner.SqlSnapshot]::Execute($database, [string[]]@(
                'CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB)',
                "INSERT INTO audit (payload) VALUES (X'6175646974')"))

        $limited = [DriftBusterOfflineRunner.SqlSnapshot]::Build($database, [string[]]@('accounts', 'audit'), [string[]]@('audit'), @{}, @{}, 1, '[REDACTED]', '')
        @($limited['tables']).Count | Should -Be 1
        $limited['tables'][0]['row_count'] | Should -Be 2
        @($limited['tables'][0]['rows']).Count | Should -Be 1

        $audit = [DriftBusterOfflineRunner.SqlSnapshot]::Build($database, [string[]]@('audit'), [string[]]@(), @{}, @{}, 0, '[REDACTED]', '')
        $audit['tables'][0]['rows'][0]['payload']['type'] | Should -BeExactly 'base64'
        $audit['tables'][0]['rows'][0]['payload']['value'] | Should -BeExactly 'YXVkaXQ='
    }

    It 'collects a snapshot source into the manifest' {
        $tmp = Get-TestDirectory
        [void](Initialize-SampleDatabase (Join-Path $tmp 'runner.sqlite'))
        $config = Import-SourceConfig $tmp @([ordered]@{
                alias        = 'accounts-db'
                sql_snapshot = [ordered]@{
                    path = 'runner.sqlite'; mask_columns = [ordered]@{ accounts = @('secret') }; hash_columns = [ordered]@{ accounts = @('email') }
                    placeholder = '[MASK]'; hash_salt = 'pepper'
                }
            })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $result.FilesCollected | Should -Be 1
        $exported = Read-JsonFile (Join-TestPath $result.StagingDirectory @('data', 'accounts-db', 'sql-snapshot.json'))
        $exported.tables[0].rows[0].secret | Should -BeExactly '[MASK]'
        $manifest = Read-JsonFile $result.ManifestPath
        $summary = $manifest.sources[0]
        $summary.type | Should -BeExactly 'sql_snapshot'
        @($summary.tables) | Should -Be @('accounts')
        $summary.row_counts.accounts | Should -Be 2
        $export = $manifest.sql_exports[0]
        $export.alias | Should -BeExactly 'accounts-db'
        @($export.masked_columns.accounts) | Should -Be @('secret')
        @($export.hashed_columns.accounts) | Should -Be @('email')
        $export.placeholder | Should -BeExactly '[MASK]'
        $manifest.files[0].source | Should -BeExactly 'sql:sqlite'
    }

    It 'skips a missing optional database and stops on a missing required one' {
        $tmp = Get-TestDirectory
        $config = Import-SourceConfig $tmp @([ordered]@{ sql_snapshot = [ordered]@{ path = 'missing.sqlite' }; optional = $true })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'
        $summary = (Read-JsonFile $result.ManifestPath).sources[0]
        $summary.skipped | Should -BeTrue
        $summary.reason | Should -BeExactly 'missing'

        $required = Import-SourceConfig $tmp @([ordered]@{ sql_snapshot = [ordered]@{ path = 'missing.sqlite' } }) -Name 'required'
        { Invoke-DBOfflineRunner -Config $required -Timestamp '20250101T000000Z' } | Should -Throw 'SQL snapshot source not found: missing.sqlite'
    }
}

Describe 'registry scans' {
    BeforeAll {
        function Import-RegistryConfig {
            param([string] $Directory, $RegistryScan)
            return Import-SourceConfig $Directory @([ordered]@{ registry_scan = $RegistryScan })
        }

        function Read-RegistryResult {
            param($Result, [string] $Name)
            return Read-JsonFile (Join-TestPath $Result.StagingDirectory @('data', 'source_00', $Name))
        }

        # A remote read as the WinRM endpoint returns it: hashtables with kinds as RegistryValueKind names.
        function Get-RemoteTestNode {
            param([string] $Hive, [string] $Path, $View, [string[]] $Subkeys, [hashtable[]] $Values)
            return @{ hive = $Hive; path = $Path; view = $View; subkeys = $Subkeys; values = $Values }
        }
    }

    It 'is skipped off Windows' {
        Mock Test-DBWindowsPlatform { $false }
        $config = Import-RegistryConfig (Get-TestDirectory) ([ordered]@{ token = 'VendorA' })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'
        $summary = (Read-JsonFile $result.ManifestPath).sources[0]
        $summary.skipped | Should -BeTrue
        $summary.reason | Should -BeExactly 'not-windows'
    }

    It 'searches the explicit roots without discovering applications' {
        Mock Test-DBWindowsPlatform { $true }
        Mock Get-DBInstalledApp { throw 'installed applications should not be read when roots are given' }
        Mock Search-DBRegistry { , @([pscustomobject]@{ hive = 'HKLM'; path = 'Software\VendorA'; value_name = 'Server'; data_preview = 'api.internal'; reason = 'keyword' }) }
        $config = Import-RegistryConfig (Get-TestDirectory) ([ordered]@{ token = 'VendorA'; roots = @([ordered]@{ hive = 'HKLM'; path = 'Software\VendorA'; view = '64' }) })

        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        Should -Invoke Search-DBRegistry -Times 1 -Exactly -ParameterFilter { @($Roots).Count -eq 1 -and $Roots[0].view -ceq '64' }
        $summary = (Read-JsonFile $result.ManifestPath).sources[0]
        $summary.type | Should -BeExactly 'registry_scan'
        $summary.hits | Should -Be 1
        @($summary.roots) | Should -Be @('HKLM \ Software\VendorA')
        @($summary.requested_roots) | Should -Be @('HKLM \ Software\VendorA (view 64)')
        $payload = Read-RegistryResult $result 'registry_scan.json'
        $payload.hits[0].data_preview | Should -BeExactly 'api.internal'
        $payload.requested_roots[0].view | Should -BeExactly '64'
    }

    Context 'over WinRM' {
        BeforeEach {
            Mock Test-DBWindowsPlatform { $true }
            Mock Close-DBRemoteRegistrySession { }
        }

        It 'scans each remote host over its own session and writes one result per host' {
            $config = Import-RegistryConfig (Get-TestDirectory) ([ordered]@{
                    token        = 'VendorA'
                    keywords     = @('api')
                    roots        = @([ordered]@{ hive = 'HKLM'; path = 'Software\VendorA'; view = '64' })
                    remote       = [ordered]@{ host = 'app-01.corp.local'; alias = 'app 01' }
                    remote_batch = @('app-02.corp.local')
                })
            Mock Open-DBRemoteRegistrySession { [pscustomobject]@{ ComputerName = $Target.host } }
            Mock Invoke-DBRemoteRegistryDump {
                $Request.roots.Count | Should -Be 1
                $Request.max_depth | Should -Be 12
                $server = $(if ($Session.ComputerName -ceq 'app-01.corp.local') { 'api.one' } else { 'api.two' })
                return @{
                    truncated = $false
                    nodes     = @(
                        (Get-RemoteTestNode 'HKLM' 'Software\VendorA' '64' @('Child') @(@{ name = 'Server'; kind = 'String'; data = $server }, @{ name = 'Port'; kind = 'DWord'; data = 443 })),
                        (Get-RemoteTestNode 'HKLM' 'Software\VendorA\Child' '64' @() @(@{ name = 'Hosts'; kind = 'MultiString'; data = @('api.a', 'db.b') }))
                    )
                }
            }

            $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

            Should -Invoke Open-DBRemoteRegistrySession -Times 2 -Exactly
            Should -Invoke Close-DBRemoteRegistrySession -Times 2 -Exactly
            $summary = (Read-JsonFile $result.ManifestPath).sources[0]
            $summary.hits | Should -Be 4
            @($summary.targets).Count | Should -Be 2
            $summary.targets[0].host | Should -BeExactly 'app-01.corp.local'
            $summary.targets[0].hits | Should -Be 2
            $summary.targets[0].output | Should -BeLike '*registry_scan-app_01.json'
            $summary.targets[1].output | Should -BeLike '*registry_scan-app-02.corp.local.json'

            $first = Read-RegistryResult $result 'registry_scan-app_01.json'
            $first.host | Should -BeExactly 'app-01.corp.local'
            $first.alias | Should -BeExactly 'app 01'
            @($first.hits | ForEach-Object { $_.value_name }) | Should -Be @('Server', 'Hosts')
            $first.hits[0].data_preview | Should -BeExactly 'api.one'
            $first.hits[1].data_preview | Should -BeExactly 'api.a, db.b'
            (Read-RegistryResult $result 'registry_scan-app-02.corp.local.json').hits[0].data_preview | Should -BeExactly 'api.two'
        }

        It 'discovers roots from the remote host installed applications' {
            $config = Import-RegistryConfig (Get-TestDirectory) ([ordered]@{ token = 'VendorA'; remote = 'app-01.corp.local' })
            $uninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
            Mock Open-DBRemoteRegistrySession { [pscustomobject]@{ ComputerName = $Target.host } }
            Mock Invoke-DBRemoteRegistryDump {
                if ($Request.roots[0].path -ceq $uninstall) {
                    $Request.max_depth | Should -Be 1
                    return @{
                        truncated = $false
                        nodes     = @(
                            (Get-RemoteTestNode 'HKLM' $uninstall '64' @('{A}') @()),
                            (Get-RemoteTestNode 'HKLM' "$uninstall\{A}" '64' @() @(@{ name = 'DisplayName'; kind = 'String'; data = 'VendorA Suite' }, @{ name = 'Publisher'; kind = 'String'; data = 'VendorA' }))
                        )
                    }
                }

                return @{
                    truncated = $true
                    nodes     = @((Get-RemoteTestNode 'HKLM' 'Software\VendorA\Suite' '64' @() @(@{ name = 'Url'; kind = 'String'; data = 'https://vendor' })))
                }
            }

            $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

            Should -Invoke Invoke-DBRemoteRegistryDump -Times 2 -Exactly
            $target = (Read-JsonFile $result.ManifestPath).sources[0].targets[0]
            $target.hits | Should -Be 1
            $target.truncated | Should -BeTrue
            @($target.roots) | Should -Contain 'HKLM \ Software\VendorA\Suite'
        }

        It 'records a host that fails and still scans the rest' {
            $config = Import-RegistryConfig (Get-TestDirectory) ([ordered]@{
                    token        = 'VendorA'
                    roots        = @([ordered]@{ hive = 'HKLM'; path = 'Software\VendorA' })
                    remote_batch = @('down.corp.local', 'up.corp.local')
                })
            Mock Open-DBRemoteRegistrySession {
                if ($Target.host -ceq 'down.corp.local') { throw 'WinRM cannot complete the operation.' }
                [pscustomobject]@{ ComputerName = $Target.host }
            }
            Mock Invoke-DBRemoteRegistryDump { @{ truncated = $false; nodes = @((Get-RemoteTestNode 'HKLM' 'Software\VendorA' $null @() @(@{ name = 'A'; kind = 'String'; data = 'x' }))) } }

            $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

            $summary = (Read-JsonFile $result.ManifestPath).sources[0]
            $summary.targets[0].error | Should -BeExactly 'WinRM cannot complete the operation.'
            $summary.targets[0].PSObject.Properties['output'] | Should -BeNullOrEmpty
            $summary.targets[1].hits | Should -Be 1
            $summary.hits | Should -Be 1
            @($summary.requested_roots) | Should -Be @('HKLM \ Software\VendorA')
        }
    }

    It 'reports a key reached through two views once' {
        $snapshot = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
        foreach ($view in @('64', $null)) {
            $snapshot[(Get-DBRegistrySnapshotKey -Hive 'HKLM' -Path 'Software\VendorA\Suite' -View $view)] = [pscustomobject]@{
                subkeys = [string[]]@(); values = @([pscustomobject]@{ Name = 'Server'; Data = 'api.one' })
            }
        }

        $snapshot[(Get-DBRegistrySnapshotKey -Hive 'HKLM' -Path 'Software\VendorA' -View $null)] = [pscustomobject]@{ subkeys = [string[]]@('Suite'); values = @() }
        $script:DBRegistrySnapshot = $snapshot
        try {
            $roots = @([pscustomobject]@{ hive = 'HKLM'; path = 'Software\VendorA\Suite'; view = '64' }, [pscustomobject]@{ hive = 'HKLM'; path = 'Software\VendorA'; view = $null })
            $hits = Search-DBRegistry -Roots $roots -Spec ([pscustomobject]@{ keywords = @('api'); patterns = @(); max_depth = 12; max_hits = 200; time_budget_s = 10.0 })
        }
        finally {
            $script:DBRegistrySnapshot = $null
        }

        @($hits).Count | Should -Be 1
    }

    It 'only speaks WinRM' {
        { Open-DBRemoteRegistrySession -Target ([pscustomobject]@{ host = 'h'; transport = 'ssh' }) -BaseDir $TestDrive } |
            Should -Throw "*transport 'ssh' is not supported*"
    }

    It 'builds the credential from username and password_env' {
        $env:DRIFTBUSTER_TEST_REMOTE_PASS = 's3cret'
        try {
            $credential = Get-DBRemoteRegistryCredential -Target ([pscustomobject]@{ host = 'h'; username = 'CORP\collector'; password_env = 'DRIFTBUSTER_TEST_REMOTE_PASS'; credential_profile = $null }) -BaseDir $TestDrive
            $credential.UserName | Should -BeExactly 'CORP\collector'
            $credential.GetNetworkCredential().Password | Should -BeExactly 's3cret'
        }
        finally {
            Remove-Item Env:\DRIFTBUSTER_TEST_REMOTE_PASS
        }
    }

    It 'stops on an unset password variable' {
        $target = [pscustomobject]@{ host = 'h'; username = 'u'; password_env = 'DRIFTBUSTER_TEST_UNSET_VARIABLE'; credential_profile = $null }
        { Get-DBRemoteRegistryCredential -Target $target -BaseDir $TestDrive } | Should -Throw '*DRIFTBUSTER_TEST_UNSET_VARIABLE is not set*'
    }

    It 'reads credential_profile relative to the base directory and requires a PSCredential' {
        'not a credential' | Export-Clixml -LiteralPath (Join-Path $TestDrive 'profile.xml')
        $target = [pscustomobject]@{ host = 'h'; username = $null; password_env = $null; credential_profile = 'profile.xml' }
        { Get-DBRemoteRegistryCredential -Target $target -BaseDir $TestDrive } | Should -Throw '*does not hold a PSCredential*'
    }

    It 'connects as the current user when no credential is given' {
        $target = [pscustomobject]@{ host = 'h'; username = $null; password_env = $null; credential_profile = $null }
        Get-DBRemoteRegistryCredential -Target $target -BaseDir $null | Should -BeNullOrEmpty
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

        $config = Import-SourceConfig (Get-TestDirectory) @([ordered]@{
                registry_scan = [ordered]@{ token = 'DriftBusterOfflineRunnerTest'; keywords = @('server'); roots = @("HKCU\$($script:RegistryTestKey)") }
            })
        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $scan = Read-JsonFile (Join-TestPath $result.StagingDirectory @('data', 'source_00', 'registry_scan.json'))
        @($scan.hits | ForEach-Object { $_.value_name }) | Should -Be @('Server', 'Blob', 'BackupServer')
        @($scan.hits | ForEach-Object { $_.data_preview }) | Should -Be @('api.internal', 'server-blob', 'backup.internal')
        $scan.hits[2].path | Should -BeExactly "$($script:RegistryTestKey)\Nested"
        $scan.requested_roots[0].view | Should -BeNullOrEmpty

        $values = Get-DBRegistryValue -Hive 'HKCU' -Path "$($script:RegistryTestKey)\Nested" -View $null
        ($values | Where-Object { $_.Name -eq 'Negative' }).Data | Should -Be 4294967295
        $rootValues = Get-DBRegistryValue -Hive 'HKCU' -Path $script:RegistryTestKey -View $null
        Get-DBRegistryValueText ($rootValues | Where-Object { $_.Name -eq 'Flags' }).Data | Should -BeExactly 'alpha, beta'

        $patterned = Search-DBRegistry -Roots @([pscustomobject]@{ hive = 'HKCU'; path = $script:RegistryTestKey; view = $null }) -Spec ([pscustomobject]@{
                keywords = @(); patterns = @([regex]::new('^\d+$')); max_depth = 0; max_hits = 200; time_budget_s = 10.0
            })
        @($patterned | ForEach-Object { $_.value_name }) | Should -Be @('Port')
    }

    It 'reads value types RegistryKey.GetValue leaves null' -Skip:(-not $script:OnWindows) {
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
        $hits = Search-DBRegistry -Roots @([pscustomobject]@{ hive = 'HKCU'; path = $keyPath; view = $null }) -Spec ([pscustomobject]@{
                keywords = @('server'); patterns = @(); max_depth = 0; max_hits = 200; time_budget_s = 10.0
            })
        @($hits | ForEach-Object { $_.value_name }) | Should -Be @('ResList', 'FullRes', 'ReqList', 'Link', 'Custom', 'OddSz')
        @($hits | ForEach-Object { $_.data_preview }) | Should -Be @(
            "server-$([char]1)$([char]0)$($r * 3)", "server-$($r * 2)A", "server-$($r * 4)", 'link-server', "server-$r", 'server-odd')

        $read = Get-DBRegistryValue -Hive 'HKCU' -Path $keyPath -View $null
        ($read | Where-Object { $_.Name -eq 'server-empty-list' }).Data | Should -BeNullOrEmpty
    }
}

Describe 'package encryption' {
    It 'encrypts the package with a base64 and hex keyset and removes the plaintext' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'app.config') -Content "keep`n"
        $aesKey = Get-RepeatedByte 'A' 32
        $hmacKey = Get-RepeatedByte 'B' 48
        [void](Write-TestKeyset -Path (Join-TestPath $tmp @('keys', 'keyset.json')) -AesKey $aesKey -HmacKey $hmacKey)
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'app.config'; alias = 'app' }) -Runner ([ordered]@{
                output_directory = 'out'; encryption = [ordered]@{ keyset_path = 'keys/keyset.json' } })

        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $result.PackagePath | Should -BeExactly (Join-TestPath $tmp @('out', 'test-run-20250101T000000Z.zip.enc'))
        $result.EncryptedPackagePath | Should -BeExactly $result.PackagePath
        $result.UnencryptedPackagePath | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-TestPath $tmp @('out', 'test-run-20250101T000000Z.zip')) | Should -BeFalse
        $payload = Read-JsonFile $result.PackagePath
        $payload.package.original_name | Should -BeExactly 'test-run-20250101T000000Z.zip'
        $names = Read-EncryptedPackage -Path $result.PackagePath -AesKey $aesKey -HmacKey $hmacKey
        $names | Should -Contain 'data/app/app.config'
        $names | Should -Contain 'manifest.json'
    }

    It 'keeps the plaintext package when asked and records the encryption in the manifest' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'app.config') -Content "keep`n"
        [void](Write-TestKeyset -Path (Join-Path $tmp 'keyset.json') -AesKey (Get-RepeatedByte 'C' 32) -HmacKey (Get-RepeatedByte 'D' 32))
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'app.config' }) -Runner ([ordered]@{
                output_directory = 'out'; cleanup_staging = $false
                encryption = [ordered]@{ keyset_path = 'keyset.json'; output_extension = '.sealed'; remove_plaintext = $false } })

        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $result.PackagePath | Should -BeLike '*.zip.sealed'
        Test-Path -LiteralPath $result.UnencryptedPackagePath | Should -BeTrue
        $details = (Read-JsonFile $result.ManifestPath).package.encryption
        $details.enabled | Should -BeTrue
        $details.schema | Should -BeExactly $script:EncryptedSchema
        $details.remove_plaintext | Should -BeFalse
        $details.removed_plaintext | Should -BeFalse
        $details.output_name | Should -BeExactly 'test-run-20250101T000000Z.zip.sealed'
        $details.sha256 | Should -BeExactly (Get-DBFileHash $result.PackagePath)
        Get-Content -LiteralPath $result.LogPath | Where-Object { $_ -like '*encrypted package -> test-run-20250101T000000Z.zip.sealed' } | Should -Not -BeNullOrEmpty
    }

    It 'refuses a keyset with <Case>' -ForEach @(
        @{ Case = 'a short AES key'; Json = '{"aes_key": {"data": "AAAAAAAAAAAAAAAAAAAAAA=="}, "hmac_key": {"encoding": "hex", "data": "' + ('00' * 32) + '"}}'; JsonPath = '$.aes_key'; Message = 'AES-256 needs a 32-byte key' }
        @{ Case = 'a short HMAC key'; Json = '{"aes_key": {"encoding": "hex", "data": "' + ('00' * 32) + '"}, "hmac_key": {"encoding": "hex", "data": "0000"}}'; JsonPath = '$.hmac_key'; Message = 'the HMAC key needs at least 32 bytes' }
        @{ Case = 'an unknown key'; Json = '{"aes_key": {"data": "x", "min_length": 32}, "hmac_key": {"data": "x"}}'; JsonPath = '$.aes_key.min_length'; Message = 'unknown key' }
        @{ Case = 'a missing HMAC key'; Json = '{"aes_key": {"data": "x"}}'; JsonPath = '$.hmac_key'; Message = 'required' }
        @{ Case = 'another encoding'; Json = '{"aes_key": {"encoding": "raw", "data": "x"}, "hmac_key": {"data": "x"}}'; JsonPath = '$.aes_key.encoding'; Message = 'expected base64, hex or dpapi' }
        @{ Case = 'odd hex'; Json = '{"aes_key": {"encoding": "hex", "data": "abc"}, "hmac_key": {"data": "x"}}'; JsonPath = '$.aes_key.data'; Message = 'expected an even number of hexadecimal digits' }
        @{ Case = 'another schema'; Json = '{"schema": "v2", "aes_key": {"data": "x"}, "hmac_key": {"data": "x"}}'; JsonPath = '$.schema'; Message = 'expected https://driftbuster.dev/offline-runner/encryption/keyset/v1' }
    ) {
        $path = Join-Path (Get-TestDirectory) 'keyset.json'
        Write-TestText -Path $path -Content $Json
        Get-ThrownMessage { Import-DBKeyset $path } | Should -BeExactly "${path}: ${JsonPath}: $Message"
    }

    It 'names the base64 key that does not decode' {
        $path = Join-Path (Get-TestDirectory) 'keyset.json'
        Write-TestText -Path $path -Content '{"aes_key": {"data": "not base64!"}, "hmac_key": {"data": "x"}}'
        Get-ThrownMessage { Import-DBKeyset $path } | Should -BeLike "${path}: `$.aes_key.data: *"
    }

    It 'refuses DPAPI keys off Windows' -Skip:$script:OnWindows {
        $path = Join-Path (Get-TestDirectory) 'keyset.json'
        Write-TestText -Path $path -Content '{"aes_key": {"encoding": "dpapi", "data": "AAAA"}, "hmac_key": {"data": "x"}}'
        { Import-DBKeyset $path } | Should -Throw 'DPAPI keys can only be read on Windows.'
    }

    It 'decrypts DPAPI key entries for the current user and the machine' -Tag 'Windows' -Skip:(-not $script:OnWindows) {
        Add-Type -AssemblyName System.Security
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-Path $tmp 'secrets.txt') -Content 'token-789'
        $aesKey = Get-RepeatedByte 'E' 32
        $hmacKey = Get-RepeatedByte 'F' 40
        $aesBlob = [System.Security.Cryptography.ProtectedData]::Protect($aesKey, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $hmacBlob = [System.Security.Cryptography.ProtectedData]::Protect($hmacKey, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        [void](Write-TestJson -Path (Join-Path $tmp 'keyset.json') -Payload ([ordered]@{
                    schema   = $script:KeysetSchema
                    aes_key  = [ordered]@{ encoding = 'dpapi'; data = [System.Convert]::ToBase64String($aesBlob) }
                    hmac_key = [ordered]@{ encoding = 'dpapi'; scope = 'local_machine'; data = [System.Convert]::ToBase64String($hmacBlob) }
                }))
        $config = Import-SourceConfig $tmp @([ordered]@{ path = 'secrets.txt'; alias = 'secrets' }) -Runner ([ordered]@{
                output_directory = 'out'; encryption = [ordered]@{ keyset_path = 'keyset.json' } })

        $result = Invoke-DBOfflineRunner -Config $config -Timestamp '20250101T000000Z'

        $names = Read-EncryptedPackage -Path $result.PackagePath -AesKey $aesKey -HmacKey $hmacKey
        $names | Should -Contain 'data/secrets/secrets.txt'
    }
}

Describe 'driftbuster-offline-runner.ps1' {
    It 'runs a config file and reports the package' {
        $tmp = Get-TestDirectory
        Write-TestText -Path (Join-TestPath $tmp @('bundle', 'app.config')) -Content "password = SUPERSECRET123456`nkeep`n"
        $configPath = Write-TestJson -Path (Join-TestPath $tmp @('bundle', 'config.json')) -Payload ([ordered]@{
                profile = [ordered]@{ name = 'script entry'; sources = @([ordered]@{ path = 'app.config'; alias = 'app' }) }
                runner  = [ordered]@{ package_name = 'collected' }
            })

        $output = & $script:RunnerScript -ConfigPath $configPath -OutputDirectory (Join-Path $tmp 'packages')

        $output.PackagePath | Should -BeExactly (Join-TestPath $tmp @('packages', 'collected.zip'))
        $output.FilesCollected | Should -Be 1
        $output.Findings | Should -Be 1
        $output.StagingDirectory | Should -BeNullOrEmpty
        $manifest = ConvertFrom-Json (Read-ZipText $output.PackagePath 'manifest.json')
        $manifest.config.path | Should -BeExactly $configPath
        Read-ZipText $output.PackagePath 'data/app/app.config' | Should -Match '\[SECRET\]'
        Read-ZipText $output.PackagePath 'config.json' | Should -Not -BeNullOrEmpty
    }

    It 'stops with the strict reader error' {
        $tmp = Get-TestDirectory
        $configPath = Join-Path $tmp 'config.json'
        Write-TestText -Path $configPath -Content '{"profile": {"name": "p", "sources": [{"path": "a", "exlude": []}]}}'
        Get-ThrownMessage { & $script:RunnerScript -ConfigPath $configPath } | Should -BeExactly "${configPath}: `$.profile.sources[0].exlude: unknown key"
    }
}
