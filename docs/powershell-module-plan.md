# PowerShell Module Plan

Purpose: Track the feature work for the Windows-first PowerShell module that
wraps the shared .NET backend.

## Objectives

- Ship a Windows-friendly `DriftBuster` module that exposes diff, hunt, and
  run-profile helpers backed by `DriftBuster.Backend`.
- Return strongly-typed .NET objects while also providing `RawJson` for piping
  into other tooling.
- Keep distribution simple: script module that loads the already-built backend
  assembly; later consider a compiled binary module.
- Enforce linting via `Invoke-ScriptAnalyzer` (exposed through
  `scripts/lint_powershell.ps1`) before packaging.

## Module Sketch

- Module name: `DriftBuster` (initial script module under `cli/`).
- Functions:
  - `Test-DriftBusterPing`
  - `Invoke-DriftBusterDiff -Versions <string[]>` or `-Left/-Right`
  - `Invoke-DriftBusterHunt -Directory <string> [-Pattern <string>]`
  - `Get-DriftBusterRunProfile [-BaseDir <string>]`
  - `Save-DriftBusterRunProfile -Profile <RunProfileDefinition> [-BaseDir <string>]`
  - `Invoke-DriftBusterRunProfile -Profile <RunProfileDefinition> [-BaseDir <string>] [-Timestamp <string>] [-NoSave]`
- Implementation details:
  - Load the newest `DriftBuster.Backend.dll` from `gui/DriftBuster.Backend/bin/...`.
  - Hold a module-scoped `DriftbusterBackend` instance; call `.GetAwaiter().GetResult()` for synchronous usage.
  - Emphasise JSON output (`RawJson`) for diff/hunt commands so workflows can
    continue to rely on structured payloads.

## Validation Checklist

- `Test-DriftBusterPing` returns `pong`.
- `Invoke-DriftBusterDiff` against sample config files returns expected plan
  metadata and JSON structure.
- `Invoke-DriftBusterHunt` over `fixtures/` produces hit counts matching the
  GUI workflow.
- Run-profile commands save profiles under `Profiles/` and emit `metadata.json`
  with SHA-256 hashes.
- Friendly error messaging when the backend assembly is missing (prompting
  users to run `dotnet build`).

## Packaging & Distribution Notes

- Script module lives at `cli/DriftBuster.PowerShell/` with `.psm1` + `.psd1`.
- Document usage in the README (build backend, `Import-Module`, sample diff).
- Consider a binary module later by targeting `Microsoft.PowerShell.SDK` and
  publishing to PSGallery once the API stabilises.

## Outstanding Questions

- Should we add helper cmdlets to construct `RunProfileDefinition` objects from
  hashtables or JSON payloads?
- Do we need additional guardrails when the backend adds new commands (version
  negotiation, capability discovery)?
- Is signed distribution required before wider sharing?
