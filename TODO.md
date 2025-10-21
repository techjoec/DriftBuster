# TODO

## Section 1 — Multi-Server Scan Flow Enhancements
- [x] Introduce multi-server selection surface in the GUI so users configure six hosts, scopes, and roots before scanning.
  - [x] Add `ServerSelectionView.axaml` + code-behind under `gui/DriftBuster.Gui/Views/` with layout for host cards, label editors, enable toggles, and scope chips.
  - [x] Create `ServerSelectionViewModel` and supporting models (`ServerSlot`, `RootEntry`, enums for `ScanScope`) in `gui/DriftBuster.Gui/ViewModels/` with validation mirrored from existing `DiffViewModel` patterns.
  - [x] Register the new view in `MainWindowViewModel` (`gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs`) with navigation commands and persisted selection state across tabs.
- [x] Implement hunt root management UX within the new view.
  - [x] Provide default roots (e.g., `C:\Program Files`) when slots activate and expose inline buttons to add/remove entries; persist transient validation results to avoid repeated filesystem hits via a small cache in the view model.
  - [x] Surface root validation badges (pending/ok/error) using new converters under `gui/DriftBuster.Gui/Converters/` and style resources that match Fluent theme tokens.
  - [x] Block scan execution while any active host lacks a valid root and show context-specific guidance in the form footer.
- [x] Extend the GUI service layer to drive batched runs with progress updates.
  - [x] Define a `ServerScanPlan` data contract and `ScanProgress` events in `gui/DriftBuster.Backend/Models/` to represent host metadata, root scopes, and timestamps.
  - [x] Update `IDriftbusterService` + `DriftbusterService` under `gui/DriftBuster.Gui/Services/` so the UI can submit `ServerScanPlan` collections and receive progress callbacks (likely via `IProgress<ScanProgress>` or async streams).
  - [x] Display progress in the selection view using an `ItemsControl` showing per-host state (`queued`, `running`, `succeeded`, `failed`, `skipped`) with retry controls for failed hosts only.
- [x] Preserve successful host results and enable selective re-runs.
  - [x] Cache run outputs by host/root signature within the view model and reuse them when the user reruns missing servers, updating only the stale entries.
  - [x] Offer “Run missing only” and “Re-run all” buttons wired into orchestrator calls with clear status messaging.
- [x] Persist session details without background writes.
  - [x] Introduce `SessionCacheService` under `gui/DriftBuster.Gui/Services/` storing JSON at `artifacts/cache/multi-server.json`, enabled via an opt-in toggle in the view.
  - [x] Load cached labels/roots on start, provide a `Clear history` command, and batch writes so they execute only when the user confirms save.
  - [x] Add headless UI tests in `gui/DriftBuster.Gui.Tests/Ui/` covering label editing, scope toggles, root validation, and persistence toggles.

## Section 2 — Results Catalog UI
- [x] Build a consolidated catalog view showing detection coverage and drift signals.
  - [x] Create `ResultsCatalogView.axaml` and `ResultsCatalogViewModel` presenting a grid with baseline dropdown, presence counts, drift totals, color-coded tags, and `Last updated` timestamps.
  - [x] Compose the grid using Avalonia controls with shared styling so status tags align with existing design tokens.
  - [x] Map catalog entries from orchestrator output (normalized config ids, per-host coverage, drift metrics) using new DTOs in `gui/DriftBuster.Backend/Models/`.
- [x] Add interactive filters and search.
  - [x] Track filter state (coverage, severity, format type) and search text in the view model, rebuilding filtered collections reactively.
  - [x] Support keyboard usage and visual indicators for active filters and search.
  - [x] Persist filter selections when the session cache toggle is enabled via the updated session cache pipeline.
- [x] Highlight missing artifacts with remediation hooks.
  - [x] Detect configs with partial coverage (e.g., `plugins.conf` 1/6) and render inline warnings plus “Investigate” links to the drilldown view.
  - [x] Offer “Re-scan affected servers” buttons that call the orchestrator with narrowed host lists while the catalog remains visible.
- [x] Expand GUI tests.
  - [x] Add catalog-specific headless tests verifying filter combinations, search-as-you-type, coverage warnings, and quick re-scan buttons.

## Section 3 — Drilldown Experience
- [ ] Create a drilldown pane for per-config inspection.
  - [ ] Implement `ConfigDrilldownView.axaml` with a master-detail layout: server table (presence badges, baseline selector, drift metrics) and a diff area with side-by-side/unified toggle.
  - [ ] Build `ConfigDrilldownViewModel` aggregating data from orchestrator responses, including redaction flags and diff stats.
  - [ ] Reuse or adapt diff rendering components from existing diff planner (e.g., `DiffComparisonView`) to display content without duplicating logic.
- [ ] Provide metadata sidebar and actionable notes.
  - [ ] Extend backend payloads to deliver provenance, scan times, masked token counts, and validation issues per config; expose them in the drilldown sidebar using badge templates.
  - [ ] Surface a prominent secrets exposure banner (e.g., red badge + tooltip) whenever unmasked credential hunks are detected, distinct from "masked tokens" counts.
  - [ ] Allow users to flag notes for follow-up, storing the selection in session cache when enabled.
- [ ] Enable exports and targeted re-runs.
  - [ ] Hook `Export` commands to the existing Python reporting helpers (HTML/JSON) via backend bridge methods; save under `artifacts/exports/` with timestamped filenames.
  - [ ] Implement drilldown-level `Re-run selected servers` action that dispatches a focused orchestrator call and refreshes only impacted entries on completion.
- [ ] Add coverage-focused tests exercising diff mode toggles, export commands, and selective re-run flows in headless UI suites.

## Section 4 — Backend/API Support
- [ ] Upgrade the .NET backend bridge to orchestrate multi-server scans.
  - [ ] Add new request/response models (`ServerScanPlan`, `ScanProgress`, `ServerScanResult`, `ConfigSnapshot`) in `gui/DriftBuster.Backend/Models/`.
  - [ ] Refactor `DriftbusterBackend` so `DiffAsync` stays available while introducing `ScanAsync(IEnumerable<ServerScanPlan> plans, ...)` that streams structured status (`found`, `not_found`, `in_progress`) with timestamps.
  - [ ] Update `DriftbusterService` to expose the new scan API to the GUI and adapt tests/fakes in `gui/DriftBuster.Gui.Tests/Services/`.
- [ ] Extend Python engine capabilities.
  - [ ] Add a consolidated multi-server entry point (e.g., `src/driftbuster/multi_server.py`) that accepts host metadata and root lists, normalizes config keys, and returns structured results.
  - [ ] Enhance detection pipeline to normalize config identifiers using detector-provided logical ids or relative paths (`src/driftbuster/core/...`).
  - [ ] Implement caching for diff artefacts/hunt hits keyed by host + config id + input hash; invalidate when roots or file hashes change.
- [ ] Bridge Python outputs back to .NET.
  - [ ] Serialize structured status/diff payloads via JSON that `DriftbusterBackend` deserializes into the new models.
  - [ ] Provide timestamps, provenance metadata (detector name, rule ids), and explicit secret exposure indicators needed by catalog/drilldown views.
- [ ] Add Python tests under `tests/` covering multi-root alignment, caching invalidation, and status transitions; ensure coverage stays ≥90% via existing coverage script.

## Section 5 — UX Feedback & Resilience
- [ ] Introduce consistent toast and inline feedback mechanisms.
  - [ ] Implement `ToastHost` service + view in `gui/DriftBuster.Gui/Services/` and `Views/Shared/` for success/warning/error notifications triggered by orchestrator events.
  - [ ] Map backend exceptions (e.g., permission denied, host unreachable) to user-friendly copy with retry/log links.
- [ ] Record session activity timeline.
  - [ ] Create an observable activity feed (timestamp + description) maintained by the server selection view model and displayed alongside catalog/drilldown views.
  - [ ] Include actions (root added, scan started, export complete) and allow copying entries for troubleshooting.
- [ ] Harden the GUI with tests around failure handling.
  - [ ] Extend headless tests to assert toast visibility, retry flows, and activity feed updates when scans fail or exports succeed.
  - [ ] Validate that coverage thresholds (≥90%) remain intact after new test additions.

## Section 6 — Documentation & Follow-Up
- [ ] Refresh multi-server documentation.
  - [ ] Rewrite `docs/multi-server-demo.md` with the new workflow (server selection UI, root validation badges, batched scans, catalog review, drilldown exports).
  - [ ] Capture annotated screenshots or textual callouts describing key UI elements.
- [ ] Expand README quickstart.
  - [ ] Add a “Multi-server quickstart” subsection detailing GUI steps, CLI equivalents, and links to the updated demo doc.
  - [ ] Mention the new export/rescan capabilities and where artifacts are saved.
- [ ] Capture future enhancements without implying external teams.
  - [ ] Summarize stretch goals (scheduled re-scans, alerting hooks, bulk exports) in `notes/future-iterations.md`, clarifying prerequisites and open questions.
  - [ ] Highlight technical dependencies (scheduling service, notification transport) without referencing organizational processes.
