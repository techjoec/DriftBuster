# DriftBuster

DriftBuster inspects configuration trees, recognises familiar formats, and
describes the differences so you can rein in infrastructure drift before it
becomes an outage.

Note: The primary experience is a .NET 8 Windows GUI backed by a shared
backend. The Python engine provides the detection core, offline runner, and
developer tooling used by the GUI and PowerShell module.

## Highlights

- **Precise format detection** – pluggable analyzers use bounded sampling to
  process large trees without overwhelming the collector.
- **Explainable results** – each hit includes format, variant, and the reason
  the detector fired so you can audit decisions instead of trusting a black box.
- **Profiles and hunt mode** – codify expectations, ignore volatile values, and
  alert when a snapshot slips outside your guardrails.
- **First-class diff reporting** – the `driftbuster.reporting` helpers produce
  JSON/text/HTML ready for hand-off or automation, with optional redaction.
- **Cross-platform UI** – the Avalonia desktop front-end ships alongside the
  Python engine for quick triage and demo flows.
 - **Windows Registry live scans** – enumerate apps, suggest likely registry
   roots, and search values by keyword/regex (see `docs/registry.md`).

## Requirements

- Python 3.12 or newer
- `dotnet` 8.0 SDK (only for the GUI or .NET build pipeline)

## Installation

Clone the repository and install the Python package in editable mode:

```sh
git clone https://github.com/techjoec/DriftBuster.git
cd DriftBuster
python -m pip install -e .
```

Install optional tooling used by the compliance workflow:

```sh
python -m pip install detect-secrets pip-licenses
```

## Quick Start

### Scan a directory

```sh
python -m driftbuster.cli fixtures/config --glob "*.config"
```

- Add `--json` to capture machine-readable results.
- Use `--max-sample` to override the default 128 KiB sampling window.
- Pass `--profile <path>` to apply a configuration profile while scanning.

### Use the library

```python
from driftbuster import scan_path, registry_summary

summary = registry_summary()
results = scan_path("fixtures/config")
```

`results` yields `ProfiledDetection` objects ready for further filtering,
diffing, or reporting.

### Launch the desktop preview

```sh
dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj
```

The GUI uses the shared .NET backend library to show hunts, diffs, and profile
mismatches interactively.

Tips:
- Use the header theme toggle to switch Dark/Light.
- Click “Check core” to verify backend health (status dot shows green/red).
- Primary actions are accent-filled; secondary are outline for quick scanning.

### Release Build

- Python + .NET installer (default):
  - `python scripts/release_build.py --release-notes notes/releases/<semver>.md --installer-rid win-x64`
  - Installer artifacts: `artifacts/velopack/releases/<rid>`
- Portable GUI publish only: `python scripts/release_build.py --no-installer`

### Windows PowerShell module

```powershell
dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj
pwsh scripts/lint_powershell.ps1
Import-Module ./cli/DriftBuster.PowerShell/DriftBuster.psd1
Invoke-DriftBusterDiff -Versions 'fixtures/config/appsettings.json','fixtures/config/web.config'
```

The PowerShell module uses the shared `DriftBuster.Backend` library, giving the
CLI and GUI identical diff, hunt, and run-profile behaviour.

## Key Concepts

- **Catalog (`src/driftbuster/catalog.py`)** – central listing of detection
  capabilities, metadata, and sampling rules.
- **Plugins (`src/driftbuster/formats/`)** – individual format detectors.
  Register new plugins with `driftbuster.formats.register`.
- **Profiles (`docs/configuration-profiles.md`)** – YAML definitions of expected
  values; use `driftbuster.profile_cli` to generate, diff, and apply them.
- **Hunt rules (`src/driftbuster/hunt.py`)** – skim snapshots for high-priority
  strings such as secrets or machine identifiers.

Check `docs/` for deeper dives:

- `docs/profile-usage.md` – practical walkthrough of profiles and hunts.
- `docs/format-support.md` – current detector coverage.
- `docs/customization.md` – configuration flags, sampling tweaks, and plugin
  lifecycles.
- `docs/testing-strategy.md` – how we validate detectors and reporting.
- `docs/multi-server-demo.md` – sample workflow scanning 10 servers, hunting drift, and generating an HTML report.
- `docs/day0-baseline.md` – create a Day 0 baseline across many servers without an existing reference.
- `docs/DEMO.md` – GUI walkthrough using the bundled demo data.
- `docs/versioning.md` – component version workflow and sync tooling.
- `docs/registry.md` – Windows Registry live scan overview and API usage.

## Running Tests

```sh
dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --configuration Release --no-build
python -m pytest
```

Optional local checks:
- Secret scanning: `detect-secrets scan`
- License audit: `pip-licenses`

### Coverage Policy

- Maintain ≥ 90% line coverage for Python sources under `src/` and for the .NET
  surface (GUI + backend). Enforce locally with:
  - Python: `coverage run --source=src/driftbuster -m pytest -q && coverage report --fail-under=90`
  - .NET: `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj`

### Coverage Enforcement

- Quick all-in-one: `./scripts/verify_coverage.sh`
  - Runs Python tests with `coverage report --fail-under=90`
  - Runs .NET tests with `-p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total`
  - Prints a combined summary via `python -m scripts.coverage_report`

### Test Coverage

Two coverage surfaces exist: Python (engine, detectors, reporting) and .NET (GUI + backend).

- Python
  - Quick: `coverage run --source=src/driftbuster -m pytest -q && coverage report -m`
  - JSON: `coverage json -o coverage.json`
  - HTML (optional): `coverage html` → open `htmlcov/index.html`
- .NET GUI
  - Cobertura XML: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --collect:"XPlat Code Coverage" --results-directory artifacts/coverage-dotnet`
  - The XML lands under `artifacts/coverage-dotnet/<run-id>/coverage.cobertura.xml`.

Repo‑wide summary:

```sh
python -m scripts.coverage_report
```

### New Format Plugins

- Follow the checklist in `docs/plugin-test-checklist.md`.
- Add plugin tests under `tests/formats/` and keep the plugin module’s per-file coverage ≥ 90%.

This prints Python percent, .NET Cobertura percent, and the most under‑covered GUI classes to guide test additions.

## Project Layout

```
src/driftbuster/
├─ core/            # Detector orchestration, profiles, and diffing
├─ formats/         # Built-in format plugins
├─ reporting/       # Emit JSON/text/HTML reports
└─ …                # CLI entrypoints and hunt utilities

gui/                # Avalonia desktop app (C# / .NET 8)
tests/              # Python unit tests covering detectors and CLI
docs/               # Developer guides, roadmaps, and playbooks
scripts/            # Release helpers and capture tooling
```

## Contributing

1. Fork and branch from `main`.
2. Run the Python and .NET test suites before opening a pull request.
3. Document provenance in the PR template and update relevant guides.

See `CONTRIBUTING.md`, `docs/legal-safeguards.md`, and
`docs/reviewer-checklist.md` for detailed expectations.

## License

DriftBuster is licensed under the Apache License 2.0 (`LICENSE`). Related legal
documents live in `NOTICE`, `CLA/INDIVIDUAL.md`, and `CLA/ENTITY.md`.
