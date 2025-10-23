param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot '..' 'DriftBuster.PowerShell' 'DriftBuster.psm1'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$publishDir = Join-Path $repoRoot 'gui' 'DriftBuster.Backend' 'bin' 'Debug' 'published'
$backendAssembly = Join-Path $publishDir 'DriftBuster.Backend.dll'

if (-not (Test-Path -LiteralPath $backendAssembly)) {
    $publishArgs = @(
        'publish',
        'gui/DriftBuster.Backend/DriftBuster.Backend.csproj',
        '-c', 'Debug',
        '-o', $publishDir
    )

    $publishOutput = & dotnet @publishArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE`n$publishOutput"
    }
}

Import-Module $modulePath -Force

Describe 'DriftBuster PowerShell module' {
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
        Get-Module | Where-Object { $_.Path -eq $modulePath } | ForEach-Object { Remove-Module $_.Name -Force }
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
            Set-Content -LiteralPath $filePath 'server=alpha01'

            $result = Invoke-DriftBusterHunt -Directory $root.FullName
            $result.count | Should -BeGreaterThan 0
            $result.hits | Should -Not -BeNullOrEmpty
            $hit = $result.hits | Select-Object -First 1
            $hit.rule.name | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Run profile workflows' {
        It 'saves profiles from JSON text' {
            $baseDir = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N')))
            $script:tempArtifacts += $baseDir.FullName

            $profileJson = @'
{
  "name": "ModuleProfile",
  "baseline": "baseline.txt",
  "sources": ["baseline.txt", "*.txt"],
  "options": {"key": "value"},
  "secret_scanner": {"ignore_rules": ["server-name"]}
}
'@

            $profilePath = Join-Path $baseDir.FullName 'baseline.txt'
            Set-Content -LiteralPath $profilePath 'baseline data'

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

            $profile = @{
                name = 'HashtableProfile'
                baseline = $profileBaseline
                sources = @($profileBaseline, (Join-Path $sourceDir.FullName '*.txt'))
            }

            $result = Invoke-DriftBusterRunProfile -Profile $profile -BaseDir $baseDir.FullName -NoSave -Confirm:$false
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

            $profile = @{
                name = 'RawProfile'
                baseline = $profileBaseline
                sources = @($profileBaseline)
            }

            $json = Invoke-DriftBusterRunProfile -Profile $profile -BaseDir $baseDir.FullName -NoSave -Raw -Confirm:$false
            $json | Should -BeOfType [string]
            ($json | ConvertFrom-Json).profile.name | Should -Be 'RawProfile'
        }
    }
}
