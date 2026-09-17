# CLAUDE.md

## Project Overview

DriftBuster detects and explains configuration drift across file trees with format-aware diffing, profiles, and hunt tooling. Everything runs on .NET 10:

- **Backend library** (`gui/DriftBuster.Backend/`) - Detection engine and catalog, diff, hunt and secret scanning, multi-server orchestration, profiles, scheduling, registry scan, SQL export, reporting and capture
- **Avalonia GUI** (`gui/DriftBuster.Gui/`) - Cross-platform desktop interface over the backend
- **Console tool** (`cli/DriftBuster.Cli/`) - The `driftbuster` executable; also the tool the build scripts call (`version`, `release`, `maint`)
- **PowerShell module** (`cli/DriftBuster.PowerShell/`) - pwsh 7.6 wrapper over the backend
- **Offline runner** (`scripts/driftbuster-offline-runner.ps1`) - Standalone collector for Windows PowerShell 5.1 with nothing to install

All automation checks remain **local-only** - never add GitHub Actions/workflows.

## Essential Commands

`dotnet` sits behind the login profile on this host: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet ...'`. Set `AVALONIA_TELEMETRY_OPTOUT=1` for anything that builds or runs Avalonia.

### Build
```bash
dotnet build DriftBuster.sln
```

### Testing & Coverage
```bash
# Backend, CLI and GUI tests with merged coverage and the 83% total line gate, plus Pester when pwsh is on PATH
./scripts/verify_coverage.sh

# Single projects
dotnet test gui/DriftBuster.Backend.Tests/DriftBuster.Backend.Tests.csproj
dotnet test cli/DriftBuster.Cli.Tests/DriftBuster.Cli.Tests.csproj
dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj
dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter "FullyQualifiedName~Handles"
```

`verify_coverage.sh` writes the merged report to `build/coverage/merged/` and reads the threshold from `DOTNET_THRESHOLD` (default 83).

### Linting & Formatting
```bash
# dotnet format over the solution plus PSScriptAnalyzer over cli/ and scripts/
./scripts/lint_all.sh

# The two halves on their own
dotnet format DriftBuster.sln --verify-no-changes
pwsh -NoProfile -File scripts/lint_powershell.ps1
```

Analyzers (Meziantou plus the built-in rules, `EnforceCodeStyleInBuild`) are configured in `Directory.Build.props`; builds are expected to finish with zero warnings.

### Running the Application
```bash
# Desktop GUI
dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj

# Console tool (a published build runs as `driftbuster <command>`)
dotnet run --project cli/DriftBuster.Cli -- --help
dotnet run --project cli/DriftBuster.Cli -- scan fixtures/config --glob "*.config"
dotnet run --project cli/DriftBuster.Cli -- scan fixtures/config --json
dotnet run --project cli/DriftBuster.Cli -- diff fixtures/config/web.config fixtures/config/web.Release.config
dotnet run --project cli/DriftBuster.Cli -- hunt fixtures/config
dotnet run --project cli/DriftBuster.Cli -- sql-export fixtures/sql/sample.sqlite \
  --mask-column accounts.secret --hash-column accounts.email --output-dir exports

# Multi-server orchestration: request on stdin, newline-delimited JSON progress and result on stdout
dotnet run --project cli/DriftBuster.Cli -- multi-server <<'JSON'
{
  "plans": [
    {"host_id": "server01", "label": "Baseline", "roots": ["fixtures/multi-server/server01"]},
    {"host_id": "server02", "label": "Drift", "roots": ["fixtures/multi-server/server02"]}
  ]
}
JSON
```

Commands: `scan`, `diff`, `hunt`, `multi-server`, `profile`, `detection-profile`, `schedule`, `registry-scan` (Windows only), `sql-export`, `report`, `capture`, `version`, `release`, `maint`. `--help` on any command lists its options.

### PowerShell Module
```powershell
# The module loads DriftBuster.Backend.dll from beside the psm1 or from gui/DriftBuster.Backend/bin
dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Debug -o gui/DriftBuster.Backend/bin/Debug/published

Import-Module ./cli/DriftBuster.PowerShell/DriftBuster.psd1
Invoke-DriftBusterDiff -Versions 'file1.json','file2.json'
Export-DriftBusterSqlSnapshot -Database fixtures/sql/sample.sqlite -MaskColumn accounts.secret

# Package for distribution (needs the Release publish folder)
dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Release -o gui/DriftBuster.Backend/bin/Release/published
pwsh ./scripts/package_powershell_module.ps1 -Configuration Release -SkipAnalyzer
```

### Offline Runner
```powershell
# Windows PowerShell 5.1 or pwsh; no module, runtime or package needed beside the script
.\scripts\driftbuster-offline-runner.ps1 -ConfigPath .\samples\offline_runner\configs\windows_offline.config.json
```

### Release Build
```bash
# From the repository root: tests, self-contained CLI and GUI publish for the runtime, Velopack installer
dotnet run --project cli/DriftBuster.Cli -- release --runtime win-x64 --release-notes notes/releases/<semver>.md --installer-rid win-x64

# Publish only, no installer
dotnet run --project cli/DriftBuster.Cli -- release --runtime win-x64 --no-installer
```

Publishes land in `build/artifacts/{cli,gui}/<rid>`; installers in `artifacts/velopack/releases/<rid>`. A publish with a runtime identifier is self-contained unless `--framework-dependent` is passed.

### Security & Compliance
```bash
# Secrets scanning (before commits)
gitleaks dir . -v
```

## Architecture

### Backend (`gui/DriftBuster.Backend/`)

`DriftbusterBackend` implements `IDriftbusterBackend` (the surface the GUI and the PowerShell module bind to) and delegates to services in these folders:

- `Detection/` - `Detector` (bounded sampling, 128 KiB per file by default, aggregate budget), `FormatRegistry`, `DefaultPlugins`, `Catalog/` (`DetectionCatalogData.cs` holds the catalog and its version)
- `Detection/Plugins/` - Format plugins implementing `IFormatPlugin`: registry-live, XML, Dockerfile, conf, HCL, YAML, TOML, INI, JSON, binary-hybrid, text
- `Diff/` - Canonicaliser, `LineDiff` (Myers line diff), `UnifiedDiffWriter`, redaction filter
- `Hunt/`, `Secrets/` - Hunt rules and engine; secret scanner with the embedded `Resources/secret_rules.json`
- `MultiServer/` - Multi-host runner, config identity, diff cache
- `Profiles/Run/`, `Profiles/Detection/` - Run profiles and offline collector configs; detection profile store with summary and diff
- `Scheduling/`, `Registry/`, `Sql/`, `Reporting/`, `Remote/` - Schedules, registry live scan, SQLite snapshot export, HTML/JSON lines reports, capture runner
- `Infrastructure/` - Repository root lookup, path and text helpers, file I/O

**Detection flow**:
1. **Sampling** - Bounded file reads so large trees stay cheap
2. **Plugin matching** - Plugins run in priority order; the first match wins
3. **Metadata enrichment** - Each hit carries format, variant, confidence, catalog keys and review flags
4. **Profile application** - Detection profiles filter and annotate results

**Data Root** (OS-specific, resolved by `DriftbusterPaths`):
- Windows: `%LOCALAPPDATA%/DriftBuster`
- Linux/Mac: `$XDG_DATA_HOME/DriftBuster`
- Override: `DRIFTBUSTER_DATA_ROOT` environment variable
- Contains: cached diffs, session state, the PowerShell module's backend cache

### Console tool (`cli/DriftBuster.Cli/`)

System.CommandLine commands under `Commands/`, one file per command. A parse error prints each error on stderr and exits 2. `version` and `release` locate the checkout by walking up to `DriftBuster.sln`.

### Avalonia GUI (`gui/DriftBuster.Gui/`)

- Target: .NET 10, nullable + implicit usings enabled; compiled bindings by default
- **ViewModels** (all implement `IDisposable` for proper cleanup; `ls gui/DriftBuster.Gui/ViewModels/` for the full list):
  - `MainWindowViewModel` - Top-level shell, tab navigation
  - `ServerSelectionViewModel` - Multi-server orchestration, drag/drop server management
  - `ConfigDrilldownViewModel` - Configuration detail exploration
  - `DiffViewModel` - Side-by-side comparison view
  - `HuntViewModel` / `SecretScannerSettingsViewModel` - Secret scanning
  - `RunProfilesViewModel` - Profile management and scheduling
  - `ResultsCatalogViewModel` - Catalog browsing with sort/filter
- **Multi-server orchestration** runs in process through the backend:
  - Drag-to-reorder host cards
  - Session caching: `sessions/multi-server.json` under the data root
  - Exports to `artifacts/exports/<config>-<timestamp>.{html,json}`
- **Theming**: Dark/Light toggle with accessibility support
- **State persistence**: Schedule cards persist to `Profiles/schedules.json`

### Tests

- `gui/DriftBuster.Backend.Tests/` - Backend tests, one folder per backend area; `RepoPaths` resolves fixture paths from `DriftBuster.sln`
- `cli/DriftBuster.Cli.Tests/` - Console command tests
- `gui/DriftBuster.Gui.Tests/` - Headless xUnit tests with `[AvaloniaFact]`; `MainWindowUserJourneyTests` runs before claiming GUI parity; helpers `InMemorySessionCacheService` and `FakeDriftbusterService`
- `cli/DriftBuster.PowerShell.Tests/` and `scripts/DriftBusterOfflineRunner.Tests.ps1` - Pester suites for the module and the offline runner
- Coverage requirement: ≥83% total line coverage over the merged Backend, CLI and GUI report
- Run long tests in tmux: `tmux new -s codexcli-<pid>-tests 'dotnet test ...'`

### Format Plugin Development

**Adding a New Format**:
1. Add the format to the catalog in `gui/DriftBuster.Backend/Detection/Catalog/DetectionCatalogData.cs`
2. Create `gui/DriftBuster.Backend/Detection/Plugins/<Name>Plugin.cs` implementing `IFormatPlugin`
3. Add it to `DefaultPlugins.CreateBuiltIns()` in priority order
4. Add tests in `gui/DriftBuster.Backend.Tests/Detection/Plugins/<Name>PluginTests.cs`
5. Follow the checklist in `docs/plugin-test-checklist.md`
6. Update `docs/format-support.md` and `docs/format-addition-guide.md`

**Plugin Contract**:
- `Detect(path, sample, text)` where `text` is null when the sample did not decode as text
- Return `DetectionMatch` or `null`
- Use bounded analysis (e.g., the JSON plugin analyses at most 200,000 characters)
- Combine filename/extension hints with structural signals; extensions never gate detection alone
- Start confidence at ~0.5, cap at 0.95
- Populate metadata with catalog-aligned keys (variant, type hints)
- Never throw on expected conditions (truncated sample, decode failures)

## Coding Standards

### .NET
- Target: net10.0, nullable enabled, implicit usings
- Formatting: `dotnet format DriftBuster.sln --verify-no-changes`
- Coverage: **≥83% total line coverage** over the merged report (`scripts/verify_coverage.sh`)
- Analyzer warnings must be resolved before commit

### PowerShell
- Zero PSScriptAnalyzer warnings (`scripts/lint_powershell.ps1`)
- The offline runner stays compatible with Windows PowerShell 5.1: its embedded C# compiles with the .NET Framework compiler that ships with Windows

### Provenance & Licensing
- All contributions: Apache 2.0 only
- **Never translate or adapt another project's source code** — a C# rewrite of someone else's code carries their copyright and licence with it
- Implement from public documentation and specifications (.NET API docs, published standards and algorithm papers, vendor format/protocol docs), and **prefer .NET built-ins** (`Regex`, `TimeZoneInfo`, `DateTimeOffset`, `System.IO.Path`, `System.IO.Enumeration`, LINQ ordering) over hand-written machinery
- Add provenance comments for code written from public behavior:
  ```csharp
  // Derived from publicly documented behavior, not vendor source.
  ```
- Forbidden: GPL/AGPL, decompiled code, proprietary sources, AI-generated verbatim excerpts
- Record every new dependency in `THIRD-PARTY-NOTICES.txt` with its licence text or SPDX identifier and copyright line; `NOTICE` points there
- See `CONTRIBUTING.md` and `docs/legal-safeguards.md` for details

### Review Flags
- Plugins mark oddities with `metadata.needs_review` and `review_reasons`
- Profiles suppress via `metadata.ignore_review_flags = true`
- Tests must cover flag emission and suppression

## Testing Strategy

**Coverage Policy** (HARD REQUIREMENT):
- .NET: ≥83% total line coverage over Backend, CLI and GUI
- PowerShell: Pester suites pass; zero PSScriptAnalyzer warnings
- New format plugins: focused tests for the primary variant, negative cases and sampling limits

**Running Tests**:
```bash
./scripts/verify_coverage.sh                        # Everything, with the coverage gate
dotnet test --filter MainWindowUserJourneyTests      # Test class
dotnet test --filter "FullyQualifiedName~Handles"    # Pattern match
```

**Test Stability** (Prevention strategies):
- Flaky tests passing standalone but failing under coverage? Check for race conditions
- Memory leaks from ViewModels? Implement `IDisposable` and unsubscribe from events
- Async test timeouts? Add polling with timeout instead of immediate assertions
- Cross-thread property access? Use `Volatile.Read/Write` semantics

**Sample Management**:
- Public fixtures in `fixtures/`, `samples/`
- Vendor samples require sanitization (see `docs/testing-strategy.md`)
- No proprietary/encrypted binaries in repo
- Store sample references/scripts externally, link in `notes/checklists/`

## Key Documentation

- `README.md` - Quick start, requirements, commands
- `docs/testing-strategy.md` - Coverage policy, vendor sample acquisition
- `docs/format-support.md` - Current detector coverage
- `docs/format-addition-guide.md` - New plugin development standard
- `docs/plugin-test-checklist.md` - Plugin test requirements
- `docs/configuration-profiles.md`, `docs/profile-usage.md` - Profile system
- `docs/customization.md` - Sampling and plugin ordering
- `docs/registry.md` - Windows Registry live scan
- `docs/encryption.md` - Offline runner package encryption
- `docs/versioning.md`, `docs/release-notes.md` - Versions and release notes
- `docs/legal-safeguards.md` - IP/provenance controls
- `CONTRIBUTING.md` - Contribution workflow, legal requirements

## Common Issues and Fixes

**dotnet Command Not Found**
- `__JOE_PROFILE_ENV` guard variable blocks `.profile` in child shells
- Fix: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet --version'`

**PowerShell module reports `DriftBusterBackendMissing`**
- Publish the backend (`dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Debug -o gui/DriftBuster.Backend/bin/Debug/published`) and re-import; the newest `DriftBuster.Backend.dll` under `gui/DriftBuster.Backend/bin` wins

**Test Passes Standalone, Fails with Coverage**
- Race condition: async operations slower under coverage instrumentation
- Fix: Poll with timeout instead of immediate assertions

**Memory Visibility in Concurrent Code**
- Cross-thread property changes not visible without volatile semantics
- Fix: `Volatile.Read/Write` for shared properties

**ViewModels Must Implement IDisposable**
- Event subscriptions create strong references causing memory leaks
- All ViewModels unsubscribe from events in `Dispose()`

**Avalonia Drag/Drop API**
- Uses the Avalonia 12 `DataTransfer`/`DataTransferItem`/`DataFormat` API (`DataObject` no longer exists)
- Custom formats: `DataFormat.CreateStringApplicationFormat("dot.separated.name")` — ASCII letters, digits, dots, hyphens only
- Read from `DragEventArgs`: iterate `e.DataTransfer.Items`, cast to `DataTransferItem`, call `TryGetRaw(format)`

**PowerShell Linting Gotchas**
- `$null` on left side of comparisons: `if ($null -ne $variable)`
- Avoid automatic variable names (`$profile` → `$profileDef`)
- `$using:` prefix for parent-scope variables in scriptblocks
- Singular nouns for cmdlet names

## Project-Specific Constraints

1. **No CI/CD**: All checks are local-only. Never add `.github/workflows/` or automation hooks.
2. **No telemetry**: No analytics without explicit user opt-in.
3. **Secrets scanning**: Run `gitleaks dir . -v` before commits.
4. **Version sync**: Update `versions.json` and run `dotnet run --project cli/DriftBuster.Cli -- version` when bumping component versions.
