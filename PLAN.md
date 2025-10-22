# UX & Workflow Refinement Plan

## Executive Summary
- Deliver an intuitive multi-surface UX by tightening layout responsiveness, deepening accessibility coverage, and reducing orchestration friction across Avalonia UI and Python backends.
- Reinforce reliability with guardrails: deterministic drag/drop ordering, persisted filters, sanitised clipboard payloads, and virtualised lists to preserve responsiveness at scale.
- Back every surface with deterministic automation: Avalonia headless journeys, Python coverage additions in `tests/multi_server/`, and telemetry-style diagnostics so regressions surface immediately.
- Documentation, design references, and tmux hygiene stay in lock-step so contributors have a clear baseline and execution evidence for each shipped slice.

## Status Snapshot (2025-02-14)
- [x] P1 — Responsive server cards, focus outlines, and validation summaries shipped; docs + tests updated.
- [x] P2 — Catalog sort persistence, drilldown metadata strip, and Copy JSON command delivered; docs refreshed.
- [x] P3 — Toast overflow controls, timeline filter persistence, and documentation/tests updated.
- [ ] P4 — Drag/drop reordering and expanded session payloads queued; dependency on P3 diagnostics review.
- [ ] P5 — Diff planner MRU lists and JSON dual-pane still in discovery; capture UX requirements.
- [ ] P6 — Virtualisation and buffered dispatcher work blocked on telemetry perf baselines.
- [ ] P7 — Theme palette expansion and breakpoint tokens deferred until post-P6 metrics.
- [ ] S1 — Coverage gaps to close once P3/P4 land; add virtualization smoke harness.
- [ ] S2 — Screenshot/decision log refresh scheduled after P3 visual updates.
- [x] S3 — tmux hygiene script, benchmark template, and documentation added.

## Workstream Index
| ID | Scope | Drivers | Key Artifacts |
|----|-------|---------|---------------|
| P4 | Multi-server orchestration | `ServerSelectionViewModel.cs`, `src/driftbuster/multi_server.py` | Drag/drop guardrails, filter/session persistence, inline drilldown |
| P5 | Diff planner productivity | `DiffViewModel.cs`, backend diff payload | MRU browse history, JSON dual-pane |
| P6 | Performance & async | `ServerSelectionView.axaml`, dispatcher services | Virtualization, buffered progress queue |
| P7 | Theme & responsiveness | `App.axaml`, shared resources, settings storage | High-contrast themes, breakpoint tokens |
| S1 | Testing & coverage | `tests/**`, `gui/DriftBuster.Gui.Tests/**`, `scripts/verify_coverage.sh` | New cases, coverage gates, perf diagnostics |
| S2 | Docs & design references | `docs/windows-gui-guide.md`, `docs/ux-refresh.md`, `notes/status/gui-research.md` | Screenshot refresh, decision logs |
| S3 | Execution hygiene | tmux baseline, scripts | Session lifecycle, telemetry logging |

---

## P4. Multi-Server Orchestration Guardrails
**Goal:** Ensure drag/drop reordering, session persistence, and inline drilldown actions work safely during active runs.
- **Observations**: `ServerSelectionViewModel` lacks drag/drop, session persistence limited to toggle, inline actions accessible only from catalogs.
- **Solution Outline**:
  1. **Drag/drop**: Use `Avalonia.Input.DragDrop`; update `ObservableCollection` with atomic reordering and guard while `IsBusy` to avoid concurrency conflicts.
  2. **Persistence**: Expand session storage payload to include filter selections, last active tab; add versioning to avoid migration bugs.
  3. **Inline drilldown**: Add buttons to execution summary grid for quick drilldown navigation post-run.
- **Engineering Tasks**:
  - Introduce reordering commands with locking in `ServerSelectionViewModel`; ensure `RunAllCommand` checks for reordering state.
  - Compose new session DTO covering filters + timeline option; store under `artifacts/cache/session.json` (or existing location).
  - Update UI to surface drilldown button with accessible text and analytics hook.
  - Extend `tests/multi_server/test_multi_server.py` to cover new `_build_catalog_and_drilldown` branches (offline hosts, mixed coverage) ensuring Python backend keeps up.
- **Validation**: Avalonia drag/drop tests, concurrency test running reorder while `RunAllCommand` active, manual smoke verifying persistence after restart.

## P5. Diff Planner Productivity
**Goal:** Minimise re-entry friction and surface JSON parity without context switching.
- **Observations**: No MRU history for file pickers; JSON plan requires leaving view.
- **Solution Outline**:
  1. **MRU dropdowns**: Track successful baseline/compare paths in `DiffViewModel`, persist per-user via local settings store.
  2. **JSON dual pane**: Build side-by-side viewer using `JsonTreeView` or custom diff view; support lazy load >1 MB via spinner.
  3. **Clipboard actions**: Provide copy buttons for individual JSON sections, share sanitized payload.
- **Engineering Tasks**:
  - Extend `DiffInput` to fetch MRU entries; update `DiffView.axaml` to include dropdown.
  - Modify backend (`DriftbusterBackend.DiffAsync`) to include both raw/summarised JSON; update contracts and tests.
  - Add UI tests verifying MRU persistence, JSON panel visibility, clipboard operations.
- **Validation**: Headless test covering MRU reuse, manual verification with large JSON, update `docs/windows-gui-guide.md` instructions.

## P6. Performance & Async Stability
**Goal:** Keep UI responsive (target <100 ms input latency) even with 200+ timeline entries and bursty progress updates.
- **Problem Signals**: `ItemsControl` lacks virtualization; progress callbacks synchronous.
- **Solution Outline**:
  1. **Virtualization**: Replace timeline `ItemsControl` with `ListBox` + `VirtualizingStackPanel` or `ItemsRepeater`; add heuristics for virtualization threshold.
  2. **Buffered dispatcher**: Introduce queue for progress updates in `ProgressDispatcherService`; flush at 50–100 ms intervals.
  3. **Diagnostics**: Add developer flag to show queue depth/FPS logs.
- **Engineering Tasks**:
  - Profile current timeline via `Avalonia.Diagnostics`; capture baseline metrics in `notes/status/gui-research.md`.
  - Implement virtualization, ensuring data templates compatible; update tests to validate virtualization toggles.
  - Add buffered queue class with unit tests, integrate in server scan progress path.
  - Create stress test harness in `gui/DriftBuster.Gui.Tests/Ui/PerformanceSmokeTests.cs`.
- **Validation**: Benchmark before/after (document results), automated queue flush tests, manual soak with 200 entries.

## P7. Theme & Responsiveness
**Goal:** Provide high-contrast themes and consistent spacing across breakpoints.
- **Solution Outline**:
  1. **High-contrast palettes**: Define Dark+/Light+ resources, integrate into settings view model with persistence.
  2. **Breakpoint tokens**: Create style tokens for header/spacing adjustments at 1280/1600/1920 widths.
  3. **Changelog & screenshots**: Update `CHANGELOG.md` under “Changed/Added”, refresh docs.
- **Engineering Tasks**:
  - Extend settings storage schema; include migration path for older versions.
  - Apply responsive triggers using `AdaptiveTriggerBehavior` or width converters.
  - Capture before/after assets in `docs/ux-refresh.md`.
- **Validation**: Unit test verifying settings persistence, manual check with Windows high-contrast, screenshot diffs (light/dark + high-contrast).

---

## S1. Testing & Coverage Expansion
- **Python**: Add scenarios in `tests/multi_server/test_multi_server.py` for offline host mix, redacted drift, and cache invalidation; ensure coverage ≥90% after expansion.
- **Avalonia**: Extend `gui/DriftBuster.Gui.Tests/Ui/` with new suites (`ToastOverflowTests`, `DiffJsonPaneTests`, `ServerReorderTests`, `VirtualizationSmokeTests`).
- **Scripts**: Update `scripts/verify_coverage.sh` to run new perf harness (optional flag) and ensure thresholds remain enforced.
- **Telemetry Diagnostics**: Consider optional `--stress` flag hooking into virtualization/perf tests.

## S2. Documentation & Design Artifacts
- `docs/windows-gui-guide.md`: Insert MRU instructions, JSON pane walkthrough, high-contrast theme steps, drag/drop how-to.
- `docs/ux-refresh.md`: Create (if absent) with component snapshots, spacing specs, colour palettes, typography tokens.
- `docs/testing-strategy.md`: Document new test suites and virtualization/perf approach.
- `notes/status/gui-research.md`: Log profiling metrics, assumptions, open questions; link to before/after assets.
- `CHANGELOG.md`: Add entries under upcoming release section summarising UX improvements and performance updates.

## S3. Execution Hygiene
- Always `tmux ls` before creating new sessions; retire stale `codexcli-<pid>-*` sessions via `tmux kill-session` once tasks finish.
- Capture benchmark logs in `artifacts/benchmarks/<timestamp>.md`; cross-reference from notes.
- Maintain provenance of UI assets/screenshots (source resolution, theme) within `docs/ux-refresh.md` to avoid drift.

## Delivery & Tracking Cadence
- Track work via section IDs (P1–P7, S1–S3); annotate completion dates and PR links inline.
- For each completed slice, record assumptions, validation evidence, residual risks in subsections.
- Re-run relevant automated suites plus `scripts/verify_coverage.sh` at phase close and log outcomes in `notes/status/gui-research.md`.
