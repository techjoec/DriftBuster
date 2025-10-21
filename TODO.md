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
- [x] Create a drilldown pane for per-config inspection.
  - [x] Implemented `ConfigDrilldownView.axaml` with master-detail layout: server checklist, baseline badges, drift metrics, and side-by-side/unified diff toggle.
  - [x] Built `ConfigDrilldownViewModel` aggregating orchestrator payloads with redaction flags, diff snippets, and selection state.
  - [x] Presented diff previews without duplicating planner logic, providing unified and side-by-side renderings.
- [x] Provide metadata sidebar and actionable notes.
  - [x] Backend payloads now include provenance, scan timestamps, masked token counts, validation issues, and secrets exposure flags surfaced in the sidebar.
  - [x] Secrets exposure banner (distinct from masked-token count) highlights unredacted credentials.
  - [x] Notes persist via session cache when the user opts in.
- [x] Enable exports and targeted re-runs.
  - [x] Added HTML/JSON export commands writing snapshots to `artifacts/exports/<config>-<timestamp>.{html,json}`.
  - [x] Drilldown-level re-run action triggers scoped orchestrations for selected servers while retaining the current view.
- [x] Add coverage-focused tests exercising diff mode toggles, export commands, and selective re-run flows in headless UI suites.

## Section 4 — Backend/API Support
- [ ] Upgrade the .NET backend bridge to orchestrate multi-server scans end-to-end.
  - [ ] Replace the simulated scan pipeline with a concrete implementation that shells out to the Python engine, streams progress updates, and respects cancellation tokens.
  - [ ] Introduce persisted diff caching (per host/config/root hash) in a temp store (`artifacts/cache/diffs/`) with invalidation hooks when roots or file hashes change.
  - [ ] Extend `ServerScanPlan` to carry baseline preferences, export toggles, and per-host throttling; mirror updates through `DriftbusterService` and update tests/fakes accordingly.
  - [ ] Add structured status enums (`found`, `not_found`, `permission_denied`, `offline`) and wire them through the bridge so the UI can differentiate failure modes.
- [ ] Extend Python engine capabilities for multi-server orchestration.
  - [ ] Create `src/driftbuster/multi_server.py` exposing a function that accepts server metadata + roots, invokes existing detection/diff routines, and returns normalized catalog/drilldown records.
  - [ ] Implement config key normalization using detector logical identifiers first, falling back to relative paths and hashed fallbacks; add unit tests to guarantee determinism.
  - [ ] Add intelligent diff caching leveraging file mtimes + content hashes; ensure cache invalidates when scan scope or baselines change.
- [ ] Bridge Python outputs back to .NET via JSON contracts.
  - [ ] Define JSON schemas for catalog/drilldown/summary payloads with versioning; validate incoming payloads and surface descriptive errors.
  - [ ] Include provenance metadata (detector name, rule IDs), timestamps, and explicit secret exposure indicators so the UI can render badges and audit trails.
- [ ] Strengthen Python test coverage.
  - [ ] Add multi-host regression tests under `tests/` covering full coverage, partial coverage, missing files, permission errors, and cached reruns.
  - [ ] Ensure `coverage report` maintains ≥90% by adding targeted tests for new orchestration and normalization helpers.
  - [ ] Document local test recipes in `docs/testing-strategy.md` for multi-server flows.

## Section 5 — UX Feedback & Resilience
- [ ] Introduce consistent toast and inline feedback mechanisms.
  - [ ] Design a reusable `ToastHost` component with queueing, severity styling, and auto-dismiss; add to main window overlay.
  - [ ] Map backend exceptions (permission denied, host unreachable, authentication failure) to friendly messages with retry, copy logs, and drilldown shortcuts.
  - [ ] Provide inline guidance blocks (e.g., on drilldown, catalog) that surface contextual remediation steps alongside toast notifications.
- [ ] Record session activity timeline for auditability.
  - [ ] Implement an observable timeline model capturing events (root added, scan started/completed, exports, errors) with timestamps and metadata.
  - [ ] Render the feed in the results pane sidebar with filtering (all events vs errors); allow copying entries for troubleshooting.
  - [ ] Persist recent activity in the session cache when opted-in, with a “clear history” action.
- [ ] Harden the GUI with resilience tests.
  - [ ] Add headless tests verifying toast display logic, retry flows (e.g., permission failures raising toasts and allowing rerun), and activity feed updates.
  - [ ] Stress test cancellation + rerun cycles to ensure the UI resets progress states cleanly.
  - [ ] Maintain ≥90% coverage for touched modules (`ServerSelectionViewModel`, toast services, activity feed viewmodel) by adding focused unit tests.

## Section 6 — Documentation & Follow-Up
- [ ] Refresh multi-server documentation.
  - [ ] Rewrite `docs/multi-server-demo.md` showcasing: initial setup, catalog filters, drilldown exports, and selective re-runs; include CLI parity commands.
  - [ ] Capture annotated screenshots (or ASCII callouts) highlighting new UI affordances (setup, results, drilldown, toasts once implemented).
  - [ ] Add troubleshooting tips (e.g., permission failures, missing hosts) referencing upcoming toast/activity feed features.
- [ ] Expand README quickstart.
  - [ ] Introduce a “Multi-server quickstart” section summarizing GUI steps, sample commands, and export locations.
  - [ ] Link to the refreshed demo doc and note the drilldown export/re-scan abilities.
  - [ ] Update dependency notes for python orchestration script once implemented.
- [ ] Capture future enhancements without implying external teams.
  - [ ] Draft `notes/future-iterations.md` describing backlog ideas: scheduled scans, alerting hooks, bulk export improvements, pluggable notification transports.
  - [ ] Include prerequisites and technical dependencies (scheduler service, messaging transport) while avoiding organisational language.
