# DriftBuster Overview
- DriftBuster detects and explains configuration drift across large file trees, providing format-aware diffing, profiles, and hunt tooling.
- Python 3.12+ engine (package under `src/driftbuster`) drives CLI, offline runner, and developer tooling; .NET 8 Avalonia GUI (`gui/`) and PowerShell module sit on top of the shared backend.
- Key areas: `src/driftbuster/` (catalog, detectors, CLI entrypoints), `tests/` (unit suites mirroring modules), `scripts/` (release, coverage, lint helpers), `gui/` (Avalonia app + backend), `cli/` (PowerShell module), `docs/` (how-to guides, format support, testing strategy).
- Artifacts and fixtures live under `artifacts/`, `fixtures/`, and `samples/`; automation checks remain local-only (no CI workflows).
- Primary docs: `README.md` for quick start, `docs/testing-strategy.md` for coverage policy, `docs/format-support.md` and `docs/format-addition-guide.md` for plugin work.