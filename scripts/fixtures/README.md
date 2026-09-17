# Fixture sanitisation workflows

## Dotenv (`.env*`) scrub pipeline

1. Copy raw dotenv files into a temporary working directory outside the repo.
2. Run the sanitiser snippet below (PowerShell 7 or Windows PowerShell 5.1) to replace sensitive values with deterministic placeholders while retaining structural cues:

   ```powershell
   $source = if ($env:DOTENV_SOURCE) { $env:DOTENV_SOURCE } else { './incoming' }
   $target = if ($env:DOTENV_TARGET) { $env:DOTENV_TARGET } else { './sanitised' }
   New-Item -ItemType Directory -Force -Path $target | Out-Null

   # Checked in order: the first key pattern that matches decides the placeholder.
   $replacements = [ordered]@{
       '(?i)password|secret|token|key' = 'REDACTED_SECRET'
       '(?i)url'                       = 'https://redacted.local/service'
       '(?i)user|username'             = 'service_account'
   }

   Get-ChildItem -Path $source -Filter '*.env*' -File | ForEach-Object {
       $lines = Get-Content -LiteralPath $_.FullName -Encoding UTF8 | ForEach-Object {
           if ($_ -match '^\s*(export\s+)?([^=#\s]+)\s*=') {
               $key = $Matches[2]
               foreach ($pattern in $replacements.Keys) {
                   if ($key -match $pattern) { return ($_ -replace '=.*$', "=$($replacements[$pattern])") }
               }
           }
           $_
       }
       $destination = Join-Path $target $_.Name
       Set-Content -LiteralPath $destination -Value $lines -Encoding UTF8
       Write-Output "sanitised $($_.Name) -> $destination"
   }
   ```

3. Review the sanitised output for environment-specific details (hostnames, tenant IDs) and replace them with representative placeholders if required.
4. Place the scrubbed file under `fixtures/` and keep a short provenance note referencing this README.

## Audit linkage
- Detection metadata emits an `env-sanitisation-workflow` remediation entry pointing back to this README so reviewers can trace how shared fixtures were prepared.
- Commit messages should highlight when new fixtures are produced via this workflow.

## Binary adapters
- The SQLite, binary plist, and markdown front matter fixtures used by the
  binary format detector live in `fixtures/binary/`. When one changes, update its
  SHA-256 and size in `fixtures/binary/MANIFEST.json` in the same change for
  legal review.
