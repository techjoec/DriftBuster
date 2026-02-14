# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DriftBuster detects and explains configuration drift across file trees with format-aware diffing, profiles, and hunt tooling. The project consists of:

- **Python 3.12+ engine** (`src/driftbuster/`) - Core detection, CLI, offline runner, multi-server orchestration
- **.NET 10 Avalonia GUI** (`gui/`) - Cross-platform desktop interface using shared backend
- **PowerShell module** (`cli/DriftBuster.PowerShell/`) - Windows automation layer

All automation checks remain **local-only** - never add GitHub Actions/workflows.

## Essential Commands

### Development Setup
```bash
# Install editable package
python -m pip install -e .

# Install optional compliance tooling
python -m pip install pip-licenses
# Secrets scanning (gitleaks binary, see https://github.com/gitleaks/gitleaks)
# Install via: brew install gitleaks  OR  download from GitHub releases
```

### Testing & Coverage
```bash
# Python tests with 90% coverage gate (required)
coverage run --source=src/driftbuster -m pytest -q && coverage report --fail-under=90

# .NET GUI/backend tests with 90% threshold (required)
dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj

# Combined coverage verification (all-in-one)
./scripts/verify_coverage.sh
# OR cross-platform:
python -m scripts.verify_coverage

# Generate coverage reports
coverage json -o coverage.json
coverage html  # open htmlcov/index.html
python -m scripts.coverage_report  # repo-wide summary
```

### Linting & Formatting
```bash
# Python style (140-character limit, ruff linter)
ruff check src

# .NET formatting validation (run for all three projects)
dotnet format gui/DriftBuster.Backend/DriftBuster.Backend.csproj --verify-no-changes
dotnet format gui/DriftBuster.Gui/DriftBuster.Gui.csproj --verify-no-changes
dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --verify-no-changes

# PowerShell linting
pwsh scripts/lint_powershell.ps1

# Syntax compilation check
python -m compileall src
```

### Running the Application
```bash
# CLI scan
python -m driftbuster.cli fixtures/config --glob "*.config"
python -m driftbuster.cli <path> --json  # machine-readable output

# Multi-server orchestration (CLI)
python -m driftbuster.multi_server <<'JSON'
{
  "plans": [
    {"host_id": "server01", "label": "Baseline", "roots": ["path/to/server01"]},
    {"host_id": "server02", "label": "Drift", "roots": ["path/to/server02"]}
  ]
}
JSON

# SQL export with masking/hashing
driftbuster-export-sql fixtures/sqlite/sample.sqlite \
  --mask-column accounts.secret \
  --hash-column accounts.email \
  --output-dir exports

# Desktop GUI
dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj

# Offline runner
python -m driftbuster.offline_runner --input snapshot.json
```

### PowerShell Module
```powershell
# Build backend first
dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj

# Import and use module
Import-Module ./cli/DriftBuster.PowerShell/DriftBuster.psd1
Invoke-DriftBusterDiff -Versions 'file1.json','file2.json'
Export-DriftBusterSqlSnapshot -Database sample.sqlite -MaskColumn accounts.secret

# Package for distribution
dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Release
pwsh ./scripts/package_powershell_module.ps1 -Configuration Release -SkipAnalyzer
```

### Release Build
```bash
# Full build with installer (Windows)
python scripts/release_build.py --release-notes notes/releases/<semver>.md --installer-rid win-x64

# Portable GUI only (no installer)
python scripts/release_build.py --no-installer
```

### Security & Compliance
```bash
# Secrets scanning (before commits)
gitleaks dir . -v

# License audit
pip-licenses
```

## Architecture

### Python Core (`src/driftbuster/`)

**Catalog System** (`catalog.py`):
- `DETECTION_CATALOG`: Central registry of format capabilities, sampling rules, metadata schemas
- `FORMAT_SURVEY`: Format extension mappings, usage context, variant definitions
- Dataclasses: `DetectionCatalog`, `FormatClass`, `ContentSignature`, `RemediationHint`

**Detection Flow**:
1. **Sampling** - Bounded file reads (default 128 KiB window) to handle large trees
2. **Plugin matching** - Format plugins run in priority order, accumulating confidence signals
3. **Metadata enrichment** - Each hit includes format, variant, confidence, review flags
4. **Profile application** - YAML-defined expectations filter/annotate results

**Core Modules** (`core/`):
- `detector.py` - Orchestration, plugin matching, bounded sampling
- `profiles.py` - YAML-defined expectations that filter/annotate results
- `diffing.py` - Diff logic for comparing configurations
- `run_profiles.py` - Profile execution and scheduling
- `types.py` - Core type definitions

**Format Plugins** (`formats/`):
- Pluggable format detectors (JSON, XML, YAML, TOML, INI, HCL, Dockerfile, text, binary, Registry)
- Each plugin: `formats/<slug>/plugin.py` implementing `FormatPlugin` protocol
- Register via `driftbuster.formats.register()` at import time
- Each returns `DetectionMatch` or `None` with confidence scoring (0.5 start, 0.95 cap)

**Additional Modules**:
- `reporting/` - JSON/text/HTML output adapters
- `hunt.py` - Secret/identifier scanning with regex rules
- `offline_runner.py` - Process pre-captured snapshots
- `multi_server.py` - Multi-host orchestration with dataclass-based plan definitions
- `profile_cli.py`, `run_profiles_cli.py` - Profile generation, diff, scheduling
- `registry_cli.py`, `registry/` - Windows Registry live scan support
- `sql/` - SQLite export with column masking/hashing
- `notifications/` - Slack, Teams, SMTP alerting

**Data Root** (OS-specific):
- Windows: `%LOCALAPPDATA%/DriftBuster`
- Linux/Mac: `$XDG_DATA_HOME/DriftBuster`
- Override: `DRIFTBUSTER_DATA_ROOT` environment variable
- Contains: cached diffs, session state, schedules
- Helper: `DriftbusterPaths` in Backend resolves paths with legacy migration support

### .NET GUI & Backend (`gui/`)

**Backend Library** (`DriftBuster.Backend/`):
- Shared C# bridge used by both GUI and PowerShell module
- Provides diff, hunt, profile, and multi-server orchestration APIs
- Must be published/built before PowerShell import works
- Contains `DriftbusterPaths` helper for OS-specific data directory resolution

**Avalonia GUI** (`DriftBuster.Gui/`):
- Target: .NET 10, nullable + implicit usings enabled
- **ViewModels** (all implement `IDisposable` for proper cleanup):
  - `ServerSelectionViewModel` - Main orchestration, drag/drop server management
  - `ConfigDrilldownViewModel` - Configuration detail exploration
  - `DiffViewModel` - Side-by-side comparison view
  - `HuntViewModel` - Secret scanning results
  - `RunProfilesViewModel` - Profile management and scheduling
- **Tabs**: Catalog, Drilldown, Hunt, Profiles, Multi-server
- **Multi-server orchestration**:
  - Drag-to-reorder host cards
  - Session caching: `sessions/multi-server.json` under data root
  - Exports to `artifacts/exports/<config>-<timestamp>.{html,json}`
- **Theming**: Dark/Light toggle with accessibility support
- **State persistence**: Schedule cards persist to `Profiles/schedules.json`

**Tests** (`DriftBuster.Gui.Tests/`):
- Headless xUnit tests with `[AvaloniaFact]` attributes
- User journey tests: `MainWindowUserJourneyTests` (run before claiming GUI parity)
- Coverage requirement: ≥90% line coverage
- Test helpers: `InMemorySessionCacheService`, `FakeDriftbusterService`
- Run in tmux for long tests: `tmux new -s codexcli-<pid>-tests 'dotnet test ...'`

### Format Plugin Development

**Adding a New Format**:
1. Update `catalog.py` with format metadata (`DETECTION_CATALOG`, `FORMAT_SURVEY`)
2. Create `src/driftbuster/formats/<slug>/plugin.py` implementing `FormatPlugin` protocol
3. Register in `src/driftbuster/formats/__init__.py`: `register(MyPlugin())`
4. Add tests in `tests/formats/test_<format>_plugin.py` (≥90% coverage required)
5. Follow checklist in `docs/plugin-test-checklist.md`
6. Update `docs/format-support.md` and `docs/format-addition-guide.md`

**Plugin Contract**:
- Accept `(path, sample, text)` tuple
- Return `DetectionMatch` or `None`
- Use bounded analysis (e.g., 200 KiB limit for JSON plugin)
- Combine filename/extension + structural signals
- Start confidence at ~0.5, cap at 0.95
- Populate metadata with catalog-aligned keys (variant, type hints)
- Never raise on expected errors (truncated sample, decode failures)

## Coding Standards

### Python
- Style: `ruff` with **140-character line limit** (E/W/F rules)
- Coverage: **≥90% line coverage** for all touched modules (enforced locally)
- Functional blocks prioritized, sparse commenting for non-obvious logic
- Follow existing plugin patterns for consistency

### .NET
- Target: net10.0, nullable enabled, implicit usings
- Formatting: `dotnet format --verify-no-changes` for Backend, GUI, Tests
- Coverage: **≥90% line coverage** (enforced via `-p:Threshold=90`)
- Analyzer warnings must be resolved before commit

### Provenance & Licensing
- All contributions: Apache 2.0 only
- Add provenance comments for code derived from public behavior:
  ```csharp
  // Derived from publicly documented behavior, not vendor source.
  ```
- Forbidden: GPL/AGPL, decompiled code, proprietary sources, AI-generated verbatim excerpts
- Update `NOTICE` when incorporating permissively licensed material
- See `CONTRIBUTING.md` and `docs/legal-safeguards.md` for details

### Review Flags
- Plugins mark oddities with `metadata.needs_review` and `review_reasons`
- Profiles suppress via `metadata.ignore_review_flags = true`
- Tests must cover flag emission and suppression

## Testing Strategy

**Coverage Policy** (HARD REQUIREMENT):
- Python: ≥90% for all modules under `src/driftbuster/`
- .NET: ≥90% total line coverage for GUI + Backend
- PowerShell: Tests skipped when pwsh runtime < .NET 10; zero PSScriptAnalyzer warnings
- New format plugins: ≥90% per-file coverage with focused tests

**Test Organization**:
- Python: `tests/` mirrors `src/driftbuster/` structure
- `tests/formats/` contains plugin-specific suites
- `tests/multi_server/` validates orchestration logic
- `tests/registry/` Windows Registry tests
- `tests/powershell/` PowerShell module integration
- .NET: `gui/DriftBuster.Gui.Tests/` with headless Avalonia tests

**Running Tests**:
```bash
# All tests with coverage verification
./scripts/verify_coverage.sh

# Python
pytest -q                                    # Quick run
pytest tests/formats/ -v                     # Specific directory
pytest tests/formats/test_json_plugin.py::test_name -v  # Single test

# .NET
dotnet test --verbosity minimal              # All tests
dotnet test --filter MainWindowUserJourneyTests    # Test class
dotnet test --filter "FullyQualifiedName~Handles"  # Pattern match

# PowerShell
unset __JOE_PROFILE_ENV && bash --login -c 'python -m pytest tests/powershell/ -v'
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

- `README.md` - Quick start, requirements, installation
- `docs/testing-strategy.md` - Coverage policy, vendor sample acquisition
- `docs/format-support.md` - Current detector coverage
- `docs/format-addition-guide.md` - New plugin development standard
- `docs/plugin-test-checklist.md` - Plugin test requirements
- `docs/configuration-profiles.md`, `docs/profile-usage.md` - Profile system
- `docs/customization.md` - Config flags, sampling tweaks
- `docs/registry.md` - Windows Registry live scan API
- `docs/legal-safeguards.md` - IP/provenance controls
- `CONTRIBUTING.md` - Contribution workflow, legal requirements

## Common Issues and Fixes

**dotnet Command Not Found**
- `__JOE_PROFILE_ENV` guard variable blocks `.profile` in child shells
- Fix: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet --version'`

**Resource Loading Failure in Editable Install**
- `secret_rules.json` not found — `importlib.resources` doesn't follow compatibility shim
- Fix: Fallback to filesystem loading (see `offline_runner.py`, `secret_scanning.py`)

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
- Uses Avalonia 11.x `DataTransfer`/`DataTransferItem`/`DataFormat` API (not deprecated `DataObject`)
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
4. **Version sync**: Update `versions.json` and run `python scripts/sync_versions.py` when bumping component versions.
