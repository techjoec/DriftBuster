# UX & Workflow Refinement Plan

## Executive Summary
- Deliver an intuitive multi-surface UX by tightening layout responsiveness, deepening accessibility coverage, and reducing orchestration friction across Avalonia UI and Python backends.
- Reinforce reliability with guardrails: deterministic drag/drop ordering, persisted filters, sanitised clipboard payloads, and virtualised lists to preserve responsiveness at scale.
- Back every surface with deterministic automation: Avalonia headless journeys, Python coverage additions in `tests/multi_server/`, and telemetry-style diagnostics so regressions surface immediately.
- Documentation, design references, and tmux hygiene stay in lock-step so contributors have a clear baseline and execution evidence for each shipped slice.

## Step-by-Step Execution Checklist

### Completed Foundations
- [x] P1 — Responsive server cards, focus outlines, and validation summaries (docs + tests updated).
- [x] P2 — Catalog sort persistence, drilldown metadata strip, and Copy JSON command delivered.
- [x] P3 — Toast overflow controls, timeline filter persistence, and supporting documentation/ref tests.
- [x] S3 — tmux hygiene script, benchmark template, and documentation added.

### Phase 1 — Multi-Server Guardrails (P4 + S2 docs + S1 tests)
- [x] Implement drag/drop reordering commands in `ServerSelectionViewModel` with concurrency guards around `RunAllCommand`.
- [x] Extend the session snapshot DTO to capture filters, timeline state, and active tab; persist via the new user-data helper with schema versioning.
- [x] Surface inline drilldown buttons in the execution summary grid with accessible labels and analytics hooks.
- [x] Align Python runner `_build_catalog_and_drilldown` logic and expand `tests/multi_server/test_multi_server.py` for offline host + mixed coverage scenarios.
- [x] Package/build smoke test to ensure caches and sessions survive restart + upgrades using the new storage path.
- [x] Update docs (`docs/multi-server-demo.md`, `docs/windows-gui-guide.md`, README) to describe drag/drop, persistence behaviour, and data-root locations.

### Phase 2 — Diff Planner Productivity (P5 + S2 + S1 coverage)
- [ ] Nail down MRU requirements with UX notes; add persisted per-user MRU lists in `DiffInput`/settings store.
- [ ] Update `DiffView.axaml` with dropdowns and surface sanitized JSON dual-pane viewer.
- [ ] Extend backend `DriftbusterBackend.DiffAsync` payload with raw + summarised JSON, updating contracts and tests.
- [ ] Add UI & view-model tests covering MRU reuse, JSON pane toggles, and clipboard operations; document workflow in `docs/windows-gui-guide.md`.

### Phase 3 — Performance & Async Stability (P6 + S1 perf harness)
- [ ] Capture timeline performance baselines via `Avalonia.Diagnostics`; log findings in `notes/status/gui-research.md`.
- [ ] Introduce virtualization (e.g. `ItemsRepeater` + `VirtualizingStackPanel`) with heuristics for enabling at high counts.
- [ ] Implement buffered dispatcher/queue for progress updates and add unit tests for flush cadence.
- [ ] Build stress harness (`gui/DriftBuster.Gui.Tests/Ui/PerformanceSmokeTests.cs`) and wire optional perf flag into `scripts/verify_coverage.sh`.

### Phase 4 — Theme & Responsiveness (P7 + S2 visuals)
- [ ] Design and implement Dark+/Light+ high-contrast palettes with migrations in settings storage.
- [ ] Add breakpoint spacing tokens and responsive triggers for key layouts at 1280/1600/1920 widths.
- [ ] Capture before/after assets for `docs/ux-refresh.md` and update release notes/guide screenshots.

### Phase 5 — Results Catalog Alignment (P8 + S2 migration note)
- [ ] Swap `ResultsCatalogView` to Avalonia 11.2-supported sorting APIs (`SortDescription`/column helpers) and remove deprecated types.
- [ ] Update toast converters to use Avalonia 11.2 resource lookup patterns with safe fallbacks.
- [ ] Add regression tests (view + headless UI) ensuring sort changes propagate and toast visuals resolve.
- [ ] Document the Avalonia 11.2 migration steps and troubleshooting tips in `docs/windows-gui-guide.md` (or new appendix).

### Phase 6 — Quality Sweep & Release Prep (S1/S2 wrap)
- [ ] Ensure Python + .NET coverage stays ≥90% by running `coverage run ...`, `dotnet test -p:Threshold=90`, and refreshing `scripts/verify_coverage.sh` output.
- [ ] Refresh screenshots/decision logs (`notes/status/gui-research.md`, `docs/ux-refresh.md`) after visual updates.
- [ ] Run full regression: `pytest`, `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj`, packaged smoke, and manual multi-server run.
- [ ] Update `CHANGELOG.md`, `docs/release-notes.md`, and collect validation evidence per workstream.

## Roadmap to Done
- **Phase 1 – Guardrails Ready**: finish P4 tasks, migrate caches/sessions, and align docs/tests.
- **Phase 2 – Diff Productivity**: deliver P5 MRU + JSON enhancements with supporting automation.
- **Phase 3 – Responsive & Fast**: complete P6 virtualization + buffered dispatcher, then P7 theme work and captured assets.
- **Phase 4 – Catalog Alignment**: unblock GUI builds/tests by refactoring Avalonia sorting + resource usage (P8).
- **Phase 5 – Quality & Story**: execute the quality sweep (S1/S2) and lock in release collateral.
