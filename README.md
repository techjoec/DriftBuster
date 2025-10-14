# DriftBuster

DriftBuster inspects configuration trees, recognises familiar formats, and
describes the differences so you can rein in infrastructure drift before it
becomes an outage.

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

The GUI consumes the Python engine through the local API server to show hunts,
diffs, and profile mismatches interactively.

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

## Running Tests

```sh
dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --configuration Release --no-build
python -m pytest
```

The GitHub workflow also performs secret scanning (`detect-secrets`) and license
audits (`pip-licenses`).

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
