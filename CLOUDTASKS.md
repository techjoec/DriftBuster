# CLOUDTASKS.md — Active Work Tracker

<!--
Schema reference
- H#.<area> [deps=] — Area title
  **REASON:** Why this area matters for near-term shipping.
  **MUST NOT:** Hard stops (what to avoid while working this area).
  **MUST:** Non-negotiable requirements.
  **ACCEPT GATES:** Conditions that must be true before marking the area complete.
  **REQUIRED RELATED WORK:** ≥4 concrete subpaths (files/modules/tests) agents must advance. Use hierarchical numbering (1.1, 1.1.1…) for nested subtasks.
- Append new areas at the top. Move finished areas to `CLOUDTASKS-COMPLETED.md` using the same schema.
- Task IDs (`T-xxxxxx`) stay in CLOUDTASKS.md; cross-reference them inside subtasks when relevant.
-->

## A1a. Headless Font Guardrails [deps=]

**REASON:** Remove the font bootstrap gap that currently breaks headless Avalonia runs, letting existing GUI test fixtures boot without manual font installs.

**MUST NOT:** Alter drag/drop ordering or async run coordination while touching the App bootstrapper.

**MUST:** Preload Avalonia fonts in the headless startup path, verify the font dictionary exists during tests, and document the bootstrap expectation for Windows operators.

**ACCEPT GATES:** Headless fixtures pass font assertions; new bootstrap instructions live under `docs/windows-gui-guide.md#headless-bootstrap`; seed logs archived for regression evidence.

**REQUIRED RELATED WORK:**
- [ ] 1.1 Seed Avalonia headless fonts in `gui/DriftBuster.Gui/App.axaml.cs`.
  - [ ] 1.1.1 Inject `fonts:SystemFonts` preload inside `BuildAvaloniaApp()` to eliminate `KeyNotFoundException` on headless startup.
  - [ ] 1.1.2 Extend `gui/DriftBuster.Gui.Tests/Ui/HeadlessFixture.cs` to assert the font dictionary is populated before windows instantiate.
- [ ] 1.2 Document headless bootstrap guardrails.
  - [ ] 1.2.1 Document the headless font preload requirement in `docs/windows-gui-guide.md#headless-bootstrap`.
- [ ] 1.3 Capture headless boot evidence.
  - [ ] 1.3.1 Capture headless boot logs in `artifacts/logs/headless-font-seed.txt`.

## A1b. Drilldown Command Determinism [deps=A1a]

**REASON:** `ShowDrilldownForHostCommand` remains flaky without deterministic gating, blocking multi-server drilldowns in headless validation runs.

**MUST NOT:** Introduce async deadlocks or regress RunAll command safety while updating the command flow.

**MUST:** Tighten `CanExecute` gating, log drilldown transitions, and emit structured telemetry so readiness can be audited.

**ACCEPT GATES:** Deterministic tests exist in `ServerSelectionViewModelAdditionalTests`; telemetry captured to `artifacts/logs/drilldown-ready.json`; transition logs land in `notes/status/gui-research.md`.

**REQUIRED RELATED WORK:**
- [ ] 1.1 Stabilise `ShowDrilldownForHostCommand` gating within `gui/DriftBuster.Gui/ViewModels/ServerSelectionViewModel.cs`.
  - [ ] 1.1.1 Add deterministic `CanExecute` coverage to `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs`.
  - [ ] 1.1.2 Log drilldown transitions to `notes/status/gui-research.md` for regression evidence.
  - [ ] 1.1.3 Emit structured telemetry for drilldown readiness via `ILogger` into `artifacts/logs/drilldown-ready.json`.

## A1c. Awaitable Session Cache Migration [deps=A1b]

**REASON:** Cache migration still blocks shutdown when kicked from multi-server runs; making it awaitable prevents race-induced corruption.

**MUST NOT:** Regress legacy cache discovery or introduce untracked background threads.

**MUST:** Await cache migration, stress it with concurrent tests, and document outcomes within status notes.

**ACCEPT GATES:** Concurrent migration tests in place; in-memory fake covers concurrent upgrades; migration counters and sample output logged in `notes/status/gui-research.md`.

**REQUIRED RELATED WORK:**
- [ ] 1.1 Make cache migration awaitable in `gui/DriftBuster.Gui/Services/SessionCacheService.cs`.
  - [ ] 1.1.1 Expand `gui/DriftBuster.Gui.Tests/Services/SessionCacheServiceTests.cs` to cover multi-threaded migrations and legacy cache discovery.
  - [ ] 1.1.2 Update `gui/DriftBuster.Gui.Tests/Fakes/InMemorySessionCacheService.cs` to simulate concurrent cache upgrades.
  - [ ] 1.1.3 Add migration success/failure counters and capture sample output in `notes/status/gui-research.md`.

## A1d. Multi-Server Validation Rollup [deps=A1c]

**REASON:** Once guardrails land, the suite still needs validation runs and documentation so multi-server persistence stays traceable across releases.

**MUST NOT:** Skip Release-mode GUI tests or leave docs outdated.

**MUST:** Re-run the GUI test matrix, refresh coverage reports, and update the persistence walkthrough plus research summary.

**ACCEPT GATES:** Release + Debug GUI tests rerun; coverage report generated; docs updated with persistence flow; status notes summarise the guardrail work.

**REQUIRED RELATED WORK:**
- [ ] 1.1 Validation & coverage.
  - [ ] 1.1.1 Re-run `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release`.
  - [ ] 1.1.2 Re-run `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Debug` to confirm debug builds stay stable.
  - [ ] 1.1.3 Execute `python -m scripts.coverage_report` after GUI tests to keep shared coverage reporting in sync.
- [ ] 1.2 Evidence & documentation.
  - [ ] 1.2.1 Update `docs/multi-server-demo.md` and `docs/windows-gui-guide.md` with persistence walkthrough + font preload notes.
  - [ ] 1.2.2 Summarise findings in `notes/status/gui-research.md` under the multi-server guardrails section.

## A2. Diff Planner Productivity (Phase 2) [deps=A1]

**REASON:** Elevates diff planning workflows (P5) by persisting MRU choices and dual-pane JSON views so analysts avoid repetitive setup.

**MUST NOT:** Leak sensitive payloads in MRU storage; regress existing diff rendering; bypass coverage for new serialization code.

**MUST:** Define persisted MRU contract, surface sanitised JSON panes, expand backend diff payload, and document the workflow.

**ACCEPT GATES:** MRU spec documented; GUI + backend tests green; sanitized outputs validated and logged.

**REQUIRED RELATED WORK:**
- [ ] 2.1 Finalise MRU requirements.
  - [ ] 2.1.1 Record UX notes and data contract in `notes/status/gui-research.md`.
  - [ ] 2.1.2 Add persisted store (new `gui/DriftBuster.Gui/Services/DiffPlannerMruStore.cs`) leveraging `DriftbusterPaths`.
  - [ ] 2.1.3 Cover serialization/deserialization paths in `gui/DriftBuster.Gui.Tests/Services/DiffPlannerMruStoreTests.cs`.
  - [ ] 2.1.4 Provide migration logic for existing diff planner settings (versioned storage) with regression tests covering legacy data.
- [ ] 2.2 Update GUI surfaces.
  - [ ] 2.2.1 Extend `gui/DriftBuster.Gui/ViewModels/DiffViewModel.cs` with MRU dropdowns and dual-pane JSON viewer toggles.
  - [ ] 2.2.2 Modify `gui/DriftBuster.Gui/Views/DiffView.axaml` to render sanitized JSON panes with accessible automation IDs.
  - [ ] 2.2.3 Ensure clipboard helpers refuse unsanitized payloads (`gui/DriftBuster.Gui/ViewModels/DiffViewModel.cs` logic + tests).
  - [ ] 2.2.4 Add UI automation coverage in `gui/DriftBuster.Gui.Tests/Ui/DiffViewUiTests.cs` verifying MRU selection and JSON pane toggles.
- [ ] 2.3 Extend backend contracts.
  - [ ] 2.3.1 Update `gui/DriftBuster.Backend/DriftbusterBackend.cs` and `gui/DriftBuster.Backend/Models/DiffResult.cs` to emit raw + summarized JSON.
  - [ ] 2.3.2 Mirror payload changes in `src/driftbuster/reporting/diff.py` and related helpers.
  - [ ] 2.3.3 Add regression coverage in `gui/DriftBuster.Gui.Tests/ViewModels/DiffViewModelTests.cs` and `tests/multi_server/test_multi_server.py`.
  - [ ] 2.3.4 Document payload schema in `docs/windows-gui-guide.md#diff-planner` and store samples under `artifacts/samples/diff-planner/`.
- [ ] 2.4 Documentation & validation.
  - [ ] 2.4.1 Refresh `docs/windows-gui-guide.md` and `docs/ux-refresh.md` with MRU instructions and sanitized screenshots.
  - [ ] 2.4.2 Log manual validation steps under `notes/status/gui-research.md`.
  - [ ] 2.4.3 Re-run `dotnet test ...` and `coverage run --source=src/driftbuster -m pytest tests/multi_server/test_multi_server.py` ensuring ≥90%.
  - [ ] 2.4.4 Archive validation artefacts (screenshots, JSON payloads, command output) in `artifacts/diff-planner-validation/README.md`.
- [ ] 2.5 Security & telemetry.
  - [ ] 2.5.1 Add structured logging for sanitized-vs-raw payload rejects in `DiffViewModel`.
  - [ ] 2.5.2 Extend privacy guardrails in `docs/legal-safeguards.md` to cover MRU storage.
  - [ ] 2.5.3 Capture telemetry sample results in `notes/checklists/legal-review.md`.

## A3. Performance & Async Stability (Phase 3) [deps=A2]

**REASON:** Keeps UI responsive at scale (P6), eliminating timeline jank and asynchronous update drops for large scans.

**MUST NOT:** Ship virtualization without opt-out; regress accessibility focus order; leave dispatcher queue unbounded.

**MUST:** Capture diagnostics baseline, introduce virtualization heuristics, buffer dispatcher updates, and add perf harness coverage.

**ACCEPT GATES:** Baseline metrics recorded; virtualization toggles documented; perf harness integrated into `scripts/verify_coverage.sh`.

**REQUIRED RELATED WORK:**
- [ ] 3.1 Capture baseline diagnostics.
  - [ ] 3.1.1 Run Avalonia diagnostics on large fixture scans and log results in `notes/status/gui-research.md`.
  - [ ] 3.1.2 Add guidance to `docs/windows-gui-guide.md#performance`.
  - [ ] 3.1.3 Store raw diagnostics export under `artifacts/perf/baseline.json`.
- [ ] 3.2 Introduce virtualization.
  - [ ] 3.2.1 Apply `ItemsRepeater` + `VirtualizingStackPanel` to high-volume views in `gui/DriftBuster.Gui/Views/ResultsCatalogView.axaml` and `gui/DriftBuster.Gui/Views/ServerSelectionView.axaml`.
  - [ ] 3.2.2 Guard virtualization behind heuristics in `gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs`.
  - [ ] 3.2.3 Add UI tests in `gui/DriftBuster.Gui.Tests/Ui` covering virtualization toggles.
  - [ ] 3.2.4 Document virtualization fallback toggle for low-memory hosts in `docs/windows-gui-guide.md`.
- [ ] 3.3 Buffer dispatcher updates.
  - [ ] 3.3.1 Implement buffered queue in `gui/DriftBuster.Gui/Services/ToastService.cs` (or new progress dispatcher service).
  - [ ] 3.3.2 Add async unit tests in `gui/DriftBuster.Gui.Tests/Services`.
  - [ ] 3.3.3 Mirror progress throttling in Python `src/driftbuster/multi_server.py` for CLI parity with tests in `tests/multi_server/test_multi_server.py`.
  - [ ] 3.3.4 Record timing metrics before/after in `notes/status/gui-research.md`.
- [ ] 3.4 Perf harness + validation.
  - [ ] 3.4.1 Create `gui/DriftBuster.Gui.Tests/Ui/PerformanceSmokeTests.cs` exercising virtualization/perf toggles.
  - [ ] 3.4.2 Wire optional perf flag into `scripts/verify_coverage.sh`.
  - [ ] 3.4.3 Document runbook in `notes/status/gui-research.md`.
  - [ ] 3.4.4 Schedule weekly perf checks in `notes/checklists/perf-calendar.md` with recorded metrics.
- [ ] 3.5 Evidence & release notes.
  - [ ] 3.5.1 Update `docs/release-notes.md` with performance improvements summary.
  - [ ] 3.5.2 Archive perf charts and measurements under `artifacts/perf/`.

## A4. Theme & Responsiveness (Phase 4) [deps=A3]

**REASON:** Delivers P7 visual polish with high-contrast palettes and responsive spacing for large displays.

**MUST NOT:** Ship palettes without contrast verification; break existing theme tokens; forget to regenerate screenshots.

**MUST:** Add Dark+/Light+ tokens, responsive breakpoints, update assets, and capture before/after evidence.

**ACCEPT GATES:** Contrast ratios ≥ WCAG AA recorded; updated screenshots stored under `docs/ux-refresh.md`; release notes refreshed.

**REQUIRED RELATED WORK:**
- [ ] 4.1 Theme tokens.
  - [ ] 4.1.1 Extend `gui/DriftBuster.Gui/Assets/Styles/Theme.axaml` with new token sets and migration defaults.
  - [ ] 4.1.2 Update `gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs` to surface theme selectors.
  - [ ] 4.1.3 Add theme migration tests in `gui/DriftBuster.Gui.Tests/ViewModels`.
  - [ ] 4.1.4 Document palette tokens and migration defaults in `docs/windows-gui-guide.md#themes`.
- [ ] 4.2 Responsive spacing.
  - [ ] 4.2.1 Add breakpoint resources for 1280/1600/1920 widths in `gui/DriftBuster.Gui/Assets/Styles/Notifications.axaml` and layout-specific resource dictionaries.
  - [ ] 4.2.2 Modify `gui/DriftBuster.Gui/Views/MainWindow.axaml` and `ServerSelectionView.axaml` to consume the spacing tokens.
  - [ ] 4.2.3 Extend UI tests to validate layout shifts at different widths.
  - [ ] 4.2.4 Capture layout change matrix in `notes/status/gui-research.md`.
- [ ] 4.3 Asset refresh.
  - [ ] 4.3.1 Capture new screenshots and store under `docs/ux-refresh.md`.
  - [ ] 4.3.2 Update `docs/windows-gui-guide.md` and `docs/release-notes.md` with visuals.
  - [ ] 4.3.3 Maintain screenshot manifest in `docs/ux-refresh.md#asset-inventory`.
- [ ] 4.4 Validation.
  - [ ] 4.4.1 Run contrast tooling (`scripts/coverage_report.py` optional hook + manual audit) and log results.
  - [ ] 4.4.2 Execute regression tests: `dotnet test`, `pytest`, and manual multi-server run with theme toggles documented in `notes/status/gui-research.md`.
  - [ ] 4.4.3 Log accessibility audit results (tool, version, outcome) in `notes/checklists/accessibility-report.md`.
- [ ] 4.5 Release communication.
  - [ ] 4.5.1 Add theme change summary to `CHANGELOG.md` and note screenshot refresh in `docs/release-notes.md`.

## A5. Results Catalog Alignment (Phase 5) [deps=A4]

**REASON:** Upgrades Avalonia APIs (P8) to 11.2-safe surfaces and prevents toast/catalog regressions blocking GUI releases.

**MUST NOT:** Leave deprecated sort helpers; regress toast resource lookups; skip regression tests.

**MUST:** Swap to Avalonia 11.2 sorting APIs, fix toast converters, add regression coverage, and document migration.

**ACCEPT GATES:** Avalonia 11.2 builds pass; tests covering sorting/toasts run green; migration appendix updated.

**REQUIRED RELATED WORK:**
- [ ] 5.1 Sorting API migration.
  - [ ] 5.1.1 Replace deprecated sorting types in `gui/DriftBuster.Gui/Views/ResultsCatalogView.axaml` and code-behind.
  - [ ] 5.1.2 Update `gui/DriftBuster.Gui/ViewModels/ResultsCatalogViewModel.cs` logic accordingly.
  - [ ] 5.1.3 Add regression tests under `gui/DriftBuster.Gui.Tests/ViewModels/ResultsCatalogViewModelTests.cs`.
  - [ ] 5.1.4 Capture before/after sorting behaviour in `notes/status/gui-research.md` with screenshots or logs.
- [ ] 5.2 Toast resource refactor.
  - [ ] 5.2.1 Update converters in `gui/DriftBuster.Gui/Converters` to use Avalonia 11.2 resource lookups.
  - [ ] 5.2.2 Expand tests in `gui/DriftBuster.Gui.Tests/Converters`.
  - [ ] 5.2.3 Document toast resource lookup changes in `docs/windows-gui-guide.md#notifications`.
- [ ] 5.3 Build validation.
  - [ ] 5.3.1 Rebuild GUI with Avalonia 11.2 and capture results in `notes/status/gui-research.md`.
  - [ ] 5.3.2 Run headless UI tests ensuring toasts and sorting propagate.
  - [ ] 5.3.3 Store Release build artefacts and hash outputs in `artifacts/builds/avalonia-11-2/`.
- [ ] 5.4 Documentation.
  - [ ] 5.4.1 Update `docs/windows-gui-guide.md` (or appendix) with migration notes.
  - [ ] 5.4.2 Reflect release-blocker resolution in `CHANGELOG.md`.
  - [ ] 5.4.3 Cross-link updated guidance from `docs/ux-refresh.md` and `docs/release-notes.md`.
- [ ] 5.5 Evidence.
  - [ ] 5.5.1 Archive failing vs fixed test output in `artifacts/logs/results-catalog/`.

## A6. Quality Sweep & Release Prep (Phase 6) [deps=A5]

**REASON:** Completes S1/S2 wrap-up with coverage, regression evidence, and release collateral for the GUI + Python stack.

**MUST NOT:** Drop coverage below 90%; skip smoke tests; omit changelog updates.

**MUST:** Re-run full test battery, refresh docs/assets, and compile release notes with evidence.

**ACCEPT GATES:** Coverage gates met for Python/.NET; smoke runs recorded; changelog ready for tag.

**REQUIRED RELATED WORK:**
- [ ] 6.1 Coverage enforcement.
  - [ ] 6.1.1 Run `coverage run --source=src/driftbuster -m pytest -q && coverage report --fail-under=90`.
  - [ ] 6.1.2 Execute `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj`.
  - [ ] 6.1.3 Capture combined summary via `python -m scripts.coverage_report`.
  - [ ] 6.1.4 Store coverage artefacts (HTML, XML) under `artifacts/coverage/final/`.
- [ ] 6.2 Smoke & manual runs.
  - [ ] 6.2.1 Trigger packaged smoke (`scripts/smoke_multi_server_storage.sh`) and log outputs in `notes/status/gui-research.md`.
  - [ ] 6.2.2 Execute manual multi-server session, verifying persistence + diff planner features.
  - [ ] 6.2.3 Record session walkthrough (screen capture + notes) and archive under `artifacts/manual-runs/`.
- [ ] 6.3 Docs & assets.
  - [ ] 6.3.1 Refresh `docs/ux-refresh.md`, `docs/windows-gui-guide.md`, and `docs/release-notes.md` with final screenshots + notes.
  - [ ] 6.3.2 Update `README.md` and `docs/multi-server-demo.md` with summary of new capabilities.
  - [ ] 6.3.3 Ensure `docs/README.md` index references updated assets.
- [ ] 6.4 Release evidence.
  - [ ] 6.4.1 Update `CHANGELOG.md` and `notes/status/gui-research.md` with validation checklist.
  - [ ] 6.4.2 Archive artifacts under `artifacts/` as needed and note retention plan.
  - [ ] 6.4.3 Compile release handoff bundle (`notes/releases/next.md`) summarising evidence.

## A7. Core Engine Stabilisation & Orchestration [deps=]

**REASON:** Keeps the Python detector pipeline authoritative and unlocks downstream adapters by stabilising metadata and sampling guardrails.

**MUST NOT:** Diverge catalog metadata from implementation; expand sampling budget without profiling; break registry snapshots.

**MUST:** Harden detector orchestration, keep `catalog.py` authoritative, extend registry summaries, deliver diff utilities, and integrate hunt-mode transforms.

**ACCEPT GATES:** Detector orchestrator bench marked; registry summary payload documented; diff utilities available with tests.

**REQUIRED RELATED WORK:**
- [ ] 7.1 Orchestration hardening.
  - [ ] 7.1.1 Refine sampling guardrails in `src/driftbuster/core/detector.py` and `src/driftbuster/multi_server.py`.
  - [ ] 7.1.2 Add stress tests in `tests/core/test_detector.py` and `tests/multi_server/test_multi_server.py`.
  - [ ] 7.1.3 Document sampling benchmarks (inputs, timings, outcomes) in `notes/status/core-detector.md`.
- [ ] 7.2 Catalog authority.
  - [ ] 7.2.1 Audit `src/driftbuster/catalog.py` to ensure detector metadata (priority, severity, variant) remains source of truth.
  - [ ] 7.2.2 Update `docs/detection-types.md` after adjustments.
  - [ ] 7.2.3 Capture catalog diff summary in `notes/checklists/catalog-review.md`.
- [ ] 7.3 Registry summary expansion.
  - [ ] 7.3.1 Extend `src/driftbuster/registry/__init__.py::registry_summary` with usage statistics.
  - [ ] 7.3.2 Capture manual review steps in `notes/checklists/reporting-tests.md`.
  - [ ] 7.3.3 Add regression coverage verifying usage statistics in `tests/registry/test_registry_summary.py`.
- [ ] 7.4 Diff/patch utilities.
  - [ ] 7.4.1 Finalise helpers in `src/driftbuster/reporting/diff.py` for before/after comparisons.
  - [ ] 7.4.2 Add regression coverage in `tests/reporting/test_diff.py` (new) and update docs.
  - [ ] 7.4.3 Document diff helper behaviour in `notes/checklists/reporting-tests.md` and link from `docs/format-playbook.md`.
- [ ] 7.5 Hunt-mode integration.
  - [ ] 7.5.1 Wire dynamic token detection into plan transforms (`src/driftbuster/hunt.py`).
  - [ ] 7.5.2 Add tests in `tests/hunt/test_dynamic_tokens.py`.
  - [ ] 7.5.3 Document transformation hooks in `docs/hunt-mode.md`.
  - [ ] 7.5.4 Record sample hunt transformations in `notes/checklists/hunt-profile-review.md`.
- [ ] 7.6 Observability & docs.
  - [ ] 7.6.1 Update `docs/testing-strategy.md` with the new automation expectations.
  - [ ] 7.6.2 Ensure `notes/status/A1-A6.md` references the completed A7 hardening outcome.

## A8. Format Expansion & Alignment [deps=A7]

**REASON:** Delivers prioritized format work (XML, JSON, INI, structured text, binary/hybrid) so catalog coverage meets roadmap goals.

**MUST NOT:** Ship format plugins without schema provenance; duplicate catalog entries; reduce coverage below 90%.

**MUST:** Finish XML heuristics, JSON/JSONC support, unify INI lineage, add YAML/TOML guardrails, and design binary/hybrid adapters.

**ACCEPT GATES:** Each priority includes schema updates, tests, and doc revisions recorded in `docs/format-support.md`.

**REQUIRED RELATED WORK:**
- [ ] 8.1 XML family (Priority 1).
  - [ ] 8.1.1 Finalise heuristics in `src/driftbuster/formats/xml/*.py` with namespace provenance logging.
  - [ ] 8.1.2 Update catalog entries in `src/driftbuster/catalog.py`.
  - [ ] 8.1.3 Extend tests `tests/formats/test_xml_plugin.py`, `test_xml_plugin_helpers.py`, and diff renderers.
  - [ ] 8.1.4 Document schema-driven hints in `docs/format-support.md`.
  - [ ] 8.1.5 Capture anonymised XML fixtures with provenance notes under `fixtures/xml/README.md`.
  - [ ] 8.1.6 Record legal review status for schema redistribution in `docs/legal-safeguards.md`.
- [ ] 8.2 JSON (Priority 2).
  - [ ] 8.2.1 Implement JSONC/appsettings variant support in `src/driftbuster/formats/json`.
  - [ ] 8.2.2 Ensure sampling guardrails handle large vendor payloads.
  - [ ] 8.2.3 Add coverage in `tests/formats/test_json_plugin.py` and `test_json_flags.py`.
  - [ ] 8.2.4 Note rationale in `docs/format-addition-guide.md`.
  - [ ] 8.2.5 Store large-sample validation outputs in `artifacts/formats/json/validation.md`.
  - [ ] 8.2.6 Update privacy notes in `docs/legal-safeguards.md` for JSON samples.
- [ ] 8.3 INI lineage (Priority 3).
  - [ ] 8.3.1 Unify INI/key-value/dotenv detectors in `src/driftbuster/formats/ini`.
  - [ ] 8.3.2 Map remediation hints + secret classification.
  - [ ] 8.3.3 Extend tests `tests/formats/test_ini_plugin.py`, `test_ini_flags.py`, and `test_ini_preferences*.py`.
  - [ ] 8.3.4 Update docs (`docs/format-playbook.md`) with consolidated lineage.
  - [ ] 8.3.5 Add encoding/secret audit results to `notes/status/core-formats.md`.
  - [ ] 8.3.6 Ensure dotenv fixtures reference sanitisation scripts stored under `scripts/fixtures/README.md`.
- [ ] 8.4 Structured text (Priority 4).
  - [ ] 8.4.1 Harden YAML/TOML detectors in `src/driftbuster/formats/yaml` and `toml`.
  - [ ] 8.4.2 Codify whitespace tolerances.
  - [ ] 8.4.3 Add tests `tests/formats/test_yaml_plugin.py`, `test_yaml_flags_and_gating.py`, and `tests/formats/test_toml_plugin.py`.
  - [ ] 8.4.4 Update docs `docs/format-support.md` + `docs/format-addition-guide.md`.
  - [ ] 8.4.5 Publish indentation tolerance policy in `docs/format-playbook.md#structured-text`.
  - [ ] 8.4.6 Capture multi-document YAML samples with provenance notes under `fixtures/yaml/README.md`.
- [ ] 8.5 Binary/Hybrid (Priority 5).
  - [ ] 8.5.1 Design adapters for embedded SQL, property lists, markdown front matter in `src/driftbuster/formats`.
  - [ ] 8.5.2 Add fixtures under `fixtures/` with legal review tracked.
  - [ ] 8.5.3 Track binary diff adapters in `src/driftbuster/reporting/diff.py`.
  - [ ] 8.5.4 Document legal sign-off steps in `docs/legal-safeguards.md`.
  - [ ] 8.5.5 Produce binary diff adapter design doc in `notes/design/binary-diff.md`.
  - [ ] 8.5.6 Capture anonymised binary fixture generation scripts under `scripts/fixtures/binary/`.

## A9. Metadata Enhancements [deps=A7]

**REASON:** Adds severity hints and remediation guidance so catalog consumers can triage drift effectively.

**MUST NOT:** Diverge metadata between catalog and docs; ship remediation text without legal review; omit tests.

**MUST:** Attach severity/remediation hints, expand variant metadata, and surface schema/reference links.

**ACCEPT GATES:** Metadata fields documented, tests covering new payloads, docs updated.

**REQUIRED RELATED WORK:**
- [ ] 9.1 Severity & remediation hints.
  - [ ] 9.1.1 Extend metadata in `src/driftbuster/catalog.py`.
  - [ ] 9.1.2 Add coverage in `tests/catalog/test_metadata.py` (new).
  - [ ] 9.1.3 Document hints in `docs/detection-types.md`.
  - [ ] 9.1.4 Record legal review outcome for severity text in `docs/legal-safeguards.md`.
- [ ] 9.2 Variant metadata expansion.
  - [ ] 9.2.1 Surface remediation guidance once hunt tokens stabilize.
  - [ ] 9.2.2 Update `src/driftbuster/reporting/summary.py` to include new keys.
  - [ ] 9.2.3 Add tests `tests/reporting/test_summary.py`.
  - [ ] 9.2.4 Update `docs/format-addition-guide.md` with metadata extension workflow.
- [ ] 9.3 Schema/reference links.
  - [ ] 9.3.1 Attach reference URLs in metadata payload.
  - [ ] 9.3.2 Update docs `docs/format-support.md` with link inventory.
  - [ ] 9.3.3 Add schema link QA checklist entry to `notes/checklists/catalog-review.md`.
- [ ] 9.4 Documentation & validation.
  - [ ] 9.4.1 Capture review log in `notes/checklists/reporting-tests.md`.
  - [ ] 9.4.2 Ensure adapters display hints (GUI/CLI validations).
  - [ ] 9.4.3 Verify CLI output includes severity/remediation metadata using `python -m driftbuster.cli` against fixtures, logging results in `notes/snippets/cli-severity-log.md`.

## A10. Reporting Hold Exit Prerequisites [deps=A7,A8]

**REASON:** Safely resumes reporting work by clearing HOLD conditions that previously blocked adapter delivery.

**MUST NOT:** Bypass HOLD gating; leak vendor-specific data; skip manual checklist sign-off.

**MUST:** Verify hold-log decisions, align legal safeguards, sync manual compile/lint workflows, and confirm capture defaults.

**ACCEPT GATES:** `notes/status/hold-log.md` updated to “decision ready”; legal compliance confirmed; manual workflows rehearsed.

**REQUIRED RELATED WORK:**
- [ ] 10.1 HOLD verification.
  - [ ] 10.1.1 Review `notes/status/hold-log.md#decision-ready-summary` and document resolution steps in this queue.
  - [ ] 10.1.2 Confirm neutral language guardrails via `docs/legal-safeguards.md#hold-exit-briefing`.
  - [ ] 10.1.3 Update `notes/status/hold-log.md` entry with timestamped confirmation.
- [ ] 10.2 Workflow alignment.
  - [ ] 10.2.1 Rehearse manual compile/lint commands in `notes/checklists/core-scan.md`.
  - [ ] 10.2.2 Validate `scripts/capture.py` defaults match guidance in `docs/testing-strategy.md#hold-exit-checklist-hooks`.
  - [ ] 10.2.3 Capture command transcripts in `artifacts/hold-exit/compile-lint.txt`.
- [ ] 10.3 Evidence capture.
  - [ ] 10.3.1 Store verification notes in `notes/status/gui-research.md` and append summary to this queue once cleared.
  - [ ] 10.3.2 Ensure compliance approvals recorded in `docs/legal-safeguards.md`.
  - [ ] 10.3.3 List outstanding blockers (if any) directly in this area before closing.
- [ ] 10.4 Access control.
  - [ ] 10.4.1 Confirm capture outputs stored in restricted locations and log retention in `notes/checklists/legal-review.md`.
  - [ ] 10.4.2 Update `notes/checklists/legal-review.md` with purge/retention owners for each storage path.

## A11. Reporting & Output Implementation [deps=A10]

**REASON:** Delivers the reporting adapters (JSON, HTML, diff, CLI, GUI shell) outlined in the backlog once HOLD gates lift.

**MUST NOT:** Emit unmasked secrets; deviate from canonicalisation rules; skip diff blueprint coverage.

**MUST:** Implement adapters, enforce metadata consumption rules, finalise diff canonicalisation, flesh out token replacement API, and document capture pipeline/compliance steps.

**ACCEPT GATES:** Adapters validated via manual audit; diff blueprint executed end-to-end; compliance checklist satisfied.

**REQUIRED RELATED WORK:**
- [ ] 11.1 Output targets.
  - [ ] 11.1.1 Implement JSON lines emitter in `src/driftbuster/reporting/json_lines.py`.
  - [ ] 11.1.2 Build HTML summary adapter in `src/driftbuster/reporting/html.py`.
  - [ ] 11.1.3 Provide diff/patch CLI integration in `src/driftbuster/cli.py` (or successor) and GUI surfaces.
  - [ ] 11.1.4 Plan GUI shell (Windows) leveraging HTML/JSON once CLI stabilises.
  - [ ] 11.1.5 Capture adapter smoke outputs (JSON/HTML/diff) under `artifacts/reporting/` with redaction proof.
- [ ] 11.2 Metadata consumption.
  - [ ] 11.2.1 Ensure adapters share `summarise_metadata` outputs.
  - [ ] 11.2.2 Add tests in `tests/reporting/test_adapters.py`.
  - [ ] 11.2.3 Document shared metadata contract in `docs/format-playbook.md` and `docs/hunt-mode.md`.
- [ ] 11.3 Diff canonicalisation & masking.
  - [ ] 11.3.1 Finalise `canonicalise_text`/`canonicalise_xml` in `src/driftbuster/reporting/diff.py`.
  - [ ] 11.3.2 Ensure `build_unified_diff` returns stats + masking flags.
  - [ ] 11.3.3 Cover redaction filters with tests in `tests/reporting/test_diff_masking.py`.
  - [ ] 11.3.4 Append canonicalisation rationale to `notes/checklists/reporting-tests.md`.
- [ ] 11.4 Diff blueprint execution.
  - [ ] 11.4.1 Implement `execute_diff_plan` in `src/driftbuster/core/diffing.py`.
  - [ ] 11.4.2 Update checklists `notes/snippets/xml-config-diffs.md` and `notes/checklists/reporting-tests.md`.
  - [ ] 11.4.3 Run manual diff plan rehearsal against fixtures and log outputs in `notes/snippets/xml-config-diffs.md`.
- [ ] 11.5 Token replacement API.
  - [ ] 11.5.1 Implement `collect_token_candidates` pipeline with approvals store.
  - [ ] 11.5.2 Add storage for approval log (JSON/SQLite decision) and tests.
  - [ ] 11.5.3 Document API usage in `docs/hunt-mode.md` and updated reporting notes.
  - [ ] 11.5.4 Publish approval-log storage schema in `notes/checklists/token-approval.md`.
- [ ] 11.6 Capture pipeline.
  - [ ] 11.6.1 Document capture workflow in `scripts/capture.py` and ensure output layout matches spec.
  - [ ] 11.6.2 Maintain manifest metadata requirements (environment, operator, reasons).
  - [ ] 11.6.3 Update `notes/checklists/reporting-tests.md` with cleanup steps.
  - [ ] 11.6.4 Ensure capture workflow guidance in `docs/testing-strategy.md` and `notes/checklists/capture.md` stays in sync with script updates.
- [ ] 11.7 Compliance & retention.
  - [ ] 11.7.1 Outline retention limits (30 days) in docs.
  - [ ] 11.7.2 Add purge scripts/checklist entries in `notes/checklists/legal-review.md`.
  - [ ] 11.7.3 Record manual audit steps to confirm placeholder usage.
  - [ ] 11.7.4 Log audit evidence in `notes/checklists/legal-review.md` with sign-off initials.
- [ ] 11.8 Open questions.
  - [ ] 11.8.1 Resolve safe diff content thresholds.
  - [ ] 11.8.2 Decide on canonicalisation options (sorted keys) with tests.
  - [ ] 11.8.3 Evaluate storage backend for token approvals.
  - [ ] 11.8.4 Plan CLI surfacing for unresolved tokens without overwhelming reviewers.
  - [ ] 11.8.5 Revisit open questions monthly and update this queue with resolutions or blockers.

## A12. Compliance & Testing Framework [deps=A11]

**REASON:** Ensures all reporting outputs respect legal guardrails and testing coverage across detectors remains ≥90%.

**MUST NOT:** Distribute proprietary configs; fall below coverage thresholds; skip legal checklist updates.

**MUST:** Document legal/IP guardrails, build vendor sample strategy, and align fuzz testing.

**ACCEPT GATES:** Legal documentation updated; fuzz plan drafted; coverage scripts validated.

**REQUIRED RELATED WORK:**
- [ ] 12.1 Legal guardrails.
  - [ ] 12.1.1 Update `docs/legal-safeguards.md` with current restrictions.
  - [ ] 12.1.2 Align docs with reporting compliance sections.
  - [ ] 12.1.3 Capture legal sign-off in `notes/checklists/legal-review.md`.
- [ ] 12.2 Vendor samples strategy.
  - [ ] 12.2.1 Draft sourcing plan in `docs/testing-strategy.md`.
  - [ ] 12.2.2 Add fixtures under `fixtures/` with anonymised data.
  - [ ] 12.2.3 Document provenance for each fixture set in `fixtures/README.md`.
- [ ] 12.3 Fuzz inputs.
  - [ ] 12.3.1 Extend `scripts/score_configsamples.py` to generate fuzz inputs.
  - [ ] 12.3.2 Add tests ensuring detectors stay within guardrails.
  - [ ] 12.3.3 Record fuzz input catalogue in `notes/checklists/fuzz-plan.md`.
- [ ] 12.4 Coverage validation.
  - [ ] 12.4.1 Run existing coverage scripts and log results in `notes/status/gui-research.md`.
  - [ ] 12.4.2 Ensure per-module coverage data remains ≥90%.
  - [ ] 12.4.3 Trend coverage deltas across releases in `artifacts/coverage/history.csv`.

## A13. Encryption & Secret Scanning Enhancements [deps=A7]

**REASON:** Implements encryption loader and realtime secret scanning backlog now consolidated into this queue.

**MUST NOT:** Store secrets unmasked; bypass DPAPI/AES plan; skip masking audit logs.

**MUST:** Hook offline loader into DPAPI/AES, mirror offline scrubber for realtime, expose ignore controls, and capture validation evidence.

**ACCEPT GATES:** Encryption helpers land with docs/tests; realtime secret masking verified; audit trail recorded.

**REQUIRED RELATED WORK:**
- [ ] 13.1 Encryption loader.
  - [ ] 13.1.1 Implement DPAPI/AES flow in `src/driftbuster/offline_runner.py`.
  - [ ] 13.1.2 Update `docs/encryption.md` with usage instructions.
  - [ ] 13.1.3 Add tests `tests/offline/test_encryption.py`.
  - [ ] 13.1.4 Store DPAPI/AES configuration samples (anonymised) under `artifacts/encryption/README.md`.
- [ ] 13.2 Realtime secret scanning.
  - [ ] 13.2.1 Load `src/driftbuster/secret_rules.json` in realtime path.
  - [ ] 13.2.2 Mask suspect lines before persistence; record masked context.
  - [ ] 13.2.3 Add ignore controls to CLI/GUI (`src/driftbuster/cli.py`, `gui/DriftBuster.Gui/ViewModels/SecretScannerSettingsViewModel.cs`).
  - [ ] 13.2.4 Extend tests `tests/secret_scanning/test_realtime.py`.
  - [ ] 13.2.5 Update `docs/legal-safeguards.md` with realtime masking guardrails.
- [ ] 13.3 Logging & docs.
  - [ ] 13.3.1 Document flow in `docs/hunt-mode.md` and `docs/windows-gui-guide.md`.
  - [ ] 13.3.2 Capture validation logs in `notes/status/gui-research.md`.
  - [ ] 13.3.3 Add troubleshooting appendix to `docs/encryption.md` for realtime failures.
- [ ] 13.4 Validation.
  - [ ] 13.4.1 Run end-to-end scans across `fixtures/` and record masked output evidence.
  - [ ] 13.4.2 Ensure coverage scripts reflect new paths (≥90%).
  - [ ] 13.4.3 Record manual audit (before/after redaction) in `notes/checklists/token-approval.md`.

## A14. Scheduler & Notification Channels [deps=A13]

**REASON:** Adds lightweight scheduling with SMTP/Slack/Teams notifications per backlog.

**MUST NOT:** Hardcode credentials; send notifications without throttling; ignore retention policies.

**MUST:** Design scheduler, implement alerting interface, and document configuration.

**ACCEPT GATES:** Scheduler orchestrates recurring backups; adapters tested with mocks; docs updated.

**REQUIRED RELATED WORK:**
- [ ] 14.1 Scheduler design.
  - [ ] 14.1.1 Prototype scheduler service in `src/driftbuster/run_profiles.py` or new `src/driftbuster/scheduler.py`.
  - [ ] 14.1.2 Add configuration schema updates in `docs/registry.md`.
  - [ ] 14.1.3 Document scheduler JSON schema in `docs/configuration-profiles.md`.
- [ ] 14.2 Notification adapters.
  - [ ] 14.2.1 Implement SMTP adapter with tests `tests/notifications/test_smtp.py`.
  - [ ] 14.2.2 Add Slack adapter leveraging webhooks (`tests/notifications/test_slack.py`).
  - [ ] 14.2.3 Implement Teams adapter (`tests/notifications/test_teams.py`).
  - [ ] 14.2.4 Document secrets handling for each adapter in `docs/legal-safeguards.md`.
- [ ] 14.3 CLI/GUI wiring.
  - [ ] 14.3.1 Surface scheduling commands in `cli/DriftBuster.PowerShell` and the future Python CLI.
  - [ ] 14.3.2 Update GUI configuration surfaces if needed.
  - [ ] 14.3.3 Add integration tests exercising scheduling entry points (`tests/scheduler/test_integration.py`).
- [ ] 14.4 Documentation & validation.
  - [ ] 14.4.1 Document scheduler usage in `docs/windows-gui-guide.md` and `README.md`.
  - [ ] 14.4.2 Log manual end-to-end tests in `notes/status/gui-research.md`.
  - [ ] 14.4.3 Archive notification payload samples in `artifacts/notifications/` with redaction evidence.

## A15. Remote & Registry Scanning [deps=A13]

**REASON:** Enables remote system scans (PowerShell bundle) and live registry support requested in backlog.

**MUST NOT:** Ship remoting without credential guidance; bypass security; store credentials insecurely.

**MUST:** Provide PowerShell bundle for remote shares, extend config schema for batching, and map registry traversal helpers.

**ACCEPT GATES:** Remote scanner validated; registry snapshots integrated; docs updated.

**REQUIRED RELATED WORK:**
- [ ] 15.1 PowerShell bundle.
  - [ ] 15.1.1 Enhance `cli/DriftBuster.PowerShell` scripts for remote admin shares and WinRM.
  - [ ] 15.1.2 Add tests in `cli/DriftBuster.PowerShell.Tests` (create if absent) using mocked remoting.
  - [ ] 15.1.3 Document usage in `docs/windows-gui-guide.md` and `docs/registry.md`.
  - [ ] 15.1.4 Archive remote command transcripts and sample outputs in `artifacts/remote-scans/`.
- [ ] 15.2 Config schema.
  - [ ] 15.2.1 Extend schema for remote credentials/batching in `docs/registry.md` and `src/driftbuster/registry_cli.py`.
  - [ ] 15.2.2 Add validation tests `tests/registry/test_remote_schema.py`.
  - [ ] 15.2.3 Update configuration examples in `docs/configuration-profiles.md` for remote targets.
- [ ] 15.3 Live registry scanning.
  - [ ] 15.3.1 Map hive traversal into `src/driftbuster/registry_cli.py` and offline runner.
  - [ ] 15.3.2 Add manifest integration in `scripts/capture.py`.
  - [ ] 15.3.3 Create tests `tests/registry/test_live_hives.py`.
  - [ ] 15.3.4 Record Windows registry hive traversal notes in `notes/status/gui-research.md`.
- [ ] 15.4 Documentation.
  - [ ] 15.4.1 Update `docs/registry.md` and `docs/windows-gui-guide.md`.
  - [ ] 15.4.2 Record manual verification in `notes/status/gui-research.md`.
  - [ ] 15.4.3 Capture credential handling guidance in `docs/legal-safeguards.md`.

## A16. SQL & Database Snapshot Support [deps=A15]

**REASON:** Adds backlog capability to capture SQL/database-backed configuration stores.

**MUST NOT:** Dump live credentials; ship tooling without export guidance; skip legal review.

**MUST:** Design export routines, integrate into capture pipeline, and document retention.

**ACCEPT GATES:** SQL snapshot prototype executed; docs guiding usage; legal review recorded.

**REQUIRED RELATED WORK:**
- [ ] 16.1 Export routines.
  - [ ] 16.1.1 Implement portable exports in `scripts/capture.py` (new subcommand).
  - [ ] 16.1.2 Add connectors in `src/driftbuster/offline_runner.py`.
  - [ ] 16.1.3 Provide tests `tests/offline/test_sql_snapshots.py`.
  - [ ] 16.1.4 Document sample database schemas and anonymisation steps in `fixtures/sql/README.md`.
- [ ] 16.2 Integration.
  - [ ] 16.2.1 Wire exports into CLI/PowerShell surfaces.
  - [ ] 16.2.2 Update manifests to record database metadata.
  - [ ] 16.2.3 Add CLI/PowerShell usage examples to `docs/registry.md` and `README.md`.
- [ ] 16.3 Documentation & legal.
  - [ ] 16.3.1 Update `docs/registry.md` and `docs/legal-safeguards.md`.
  - [ ] 16.3.2 Log approvals in `notes/checklists/legal-review.md`.
  - [ ] 16.3.3 Capture retention policy for database snapshots in `docs/legal-safeguards.md#retention`.
- [ ] 16.4 Validation.
  - [ ] 16.4.1 Capture sample run, store evidence in `notes/status/gui-research.md`, and ensure retention plan recorded.
  - [ ] 16.4.2 Archive database export checksums in `artifacts/sql/`.

## A17. PowerShell Module Delivery [deps=A10]

**REASON:** Ships Windows-first module exposing backend diff, hunt, and run-profile workflows from the consolidated backlog.

**MUST NOT:** Depend on unbuilt backend; break sync APIs; skip linting.

**MUST:** Implement module commands, ensure JSON output parity, add validation tests, and document packaging.

**ACCEPT GATES:** Module passes `Invoke-ScriptAnalyzer`; commands validated against fixtures; README/doc updates live.

**REQUIRED RELATED WORK:**
- [ ] 17.1 Module implementation.
  - [ ] 17.1.1 Flesh out cmdlets in `cli/DriftBuster.PowerShell/DriftBuster.psm1`.
  - [ ] 17.1.2 Load latest backend assembly via `DriftbusterPaths.GetCacheDirectory`.
  - [ ] 17.1.3 Document module initialisation flow in `docs/windows-gui-guide.md#powershell-module`.
- [ ] 17.2 Validation.
  - [ ] 17.2.1 Add Pester tests (new `cli/DriftBuster.PowerShell.Tests`) covering `Test-DriftBusterPing`, diff/hunt, run-profile commands.
  - [ ] 17.2.2 Ensure JSON outputs match `gui/DriftBuster.Backend` models.
  - [ ] 17.2.3 Track coverage via Pester `Invoke-Pester -OutputFormat NUnitXml` and store report in `artifacts/powershell/tests/`.
- [ ] 17.3 Error handling.
  - [ ] 17.3.1 Surface friendly errors when backend assembly missing.
  - [ ] 17.3.2 Document fallback instructions in `README.md`.
  - [ ] 17.3.3 Add troubleshooting section to `docs/windows-gui-guide.md#powershell-module`.
- [ ] 17.4 Packaging.
  - [ ] 17.4.1 Update `scripts/package_powershell_module.ps1`.
  - [ ] 17.4.2 Document usage in `docs/windows-gui-guide.md` and `README.md`.
  - [ ] 17.4.3 Archive packaged module zip and checksum in `artifacts/powershell/releases/`.

## A18. Python CLI Concept (On Hold) [deps=A7]

**REASON:** Preserves legacy Python CLI blueprint for future resumption once Windows-first focus relaxes.

**MUST NOT:** Publish CLI before backlog clears; add dependencies beyond stdlib without justification; skip documentation.

**MUST:** Keep entry-point sketch current, align packaging checklist, maintain manual validation plan, and track open questions.

**ACCEPT GATES:** Blueprint synced with current detector API; manual commands rehearsed once resumed.

**REQUIRED RELATED WORK:**
- [ ] 18.1 Entry-point prep.
  - [ ] 18.1.1 Maintain `src/driftbuster/cli.py` stub aligning with `pyproject.toml` `[project.scripts]`.
  - [ ] 18.1.2 Keep argument table in sync with detector capabilities.
  - [ ] 18.1.3 Document stub status in `docs/README.md` (Plans & Notes section).
- [ ] 18.2 Packaging checklist.
  - [ ] 18.2.1 Track readiness steps in this queue until activation.
  - [ ] 18.2.2 Update `README.md` placeholder section for CLI usage.
  - [ ] 18.2.3 Record activation prerequisites in `notes/status/cli-plan.md`.
- [ ] 18.3 Manual validation plan.
  - [ ] 18.3.1 Migrate historical notes into `notes/status/cli-plan.md` (new) with command walkthroughs.
  - [ ] 18.3.2 Capture expected outputs for JSON/HTML/diff commands using fixtures under `fixtures/`.
  - [ ] 18.3.3 Store command transcripts in `artifacts/cli-plan/README.md`.
- [ ] 18.4 Open questions.
  - [ ] 18.4.1 Decide on confidence threshold flag handling.
  - [ ] 18.4.2 Evaluate progress indicator requirements.
  - [ ] 18.4.3 Determine packaging strategy (editable vs PyPI) once activated.
  - [ ] 18.4.4 Track decision timeline in `notes/status/cli-plan.md`.

## A19. Windows GUI Packaging & Research (Windows shell readiness) [deps=A11]

**REASON:** Consolidates packaging, accessibility, and runtime decisions from `notes/status/gui-research.md` and `docs/windows-gui-notes.md` so the Windows-first shell can ship once reporting adapters land.

**MUST NOT:** Ship installers without recorded compliance sign-off; rely on online dependencies during offline packaging; skip accessibility tooling runs.

**MUST:** Finalise framework selection, document runtime prerequisites, capture accessibility baselines, script packaging flows (MSIX, portable zip, self-contained), and log offline/compliance evidence.

**ACCEPT GATES:** Updated `docs/windows-gui-notes.md` packaging sections, accessibility checklist completed, packaging smoke tests recorded across Windows 10/11, and evidence archived under `artifacts/gui-packaging/`.

**REQUIRED RELATED WORK:**
- [ ] 19.1 Framework evaluation.
  - [ ] 19.1.1 Review WinUI 3, Tkinter, PySimpleGUI, and Electron options; record decision matrix in `notes/status/gui-research.md`.
  - [ ] 19.1.2 Sync `docs/windows-gui-notes.md#candidate-frameworks` with updated rationale and preferred pathway.
  - [ ] 19.1.3 Capture licensing implications and NOTICE requirements in `docs/legal-safeguards.md#gui-frameworks`.
- [ ] 19.2 Runtime prerequisites.
  - [ ] 19.2.1 Decide on WebView2 Evergreen redistribution strategy; document installer steps in `docs/windows-gui-notes.md#packaging--distribution-plan`.
  - [ ] 19.2.2 Validate `.NET` publish commands (framework-dependent vs self-contained) and log outputs in `notes/dev-host-prep.md`.
  - [ ] 19.2.3 Store publish command transcripts + hashes in `artifacts/gui-packaging/` with README describing reproduction steps.
- [ ] 19.3 Accessibility baseline.
  - [ ] 19.3.1 Define Narrator/Inspect test matrix in `notes/status/gui-research.md#user-requirements`.
  - [ ] 19.3.2 Update `docs/windows-gui-notes.md#compliance--accessibility-checklist` with step-by-step execution notes.
  - [ ] 19.3.3 Archive accessibility run evidence (tool versions, pass/fail, screenshots) in `artifacts/gui-accessibility/`.
- [ ] 19.4 Packaging workflows.
  - [ ] 19.4.1 Produce MSIX packaging checklist and scripts in `notes/dev-host-prep.md`.
  - [ ] 19.4.2 Document portable ZIP and self-contained bundle workflows in `docs/windows-gui-notes.md#packaging-quickstart`.
  - [ ] 19.4.3 Execute installer smoke tests on Windows 10/11 VMs; log results and system prerequisites in `notes/status/gui-research.md`.
- [ ] 19.5 Offline & compliance posture.
  - [ ] 19.5.1 Ensure offline activation guidance lives in `docs/windows-gui-notes.md#distribution--licensing-notes`.
  - [ ] 19.5.2 Update `docs/legal-safeguards.md` with packaging/licensing guardrails (NOTICE contents, WebView2 terms).
  - [ ] 19.5.3 Record security review notes (hash verification, sideload steps) in `notes/checklists/legal-review.md`.
- [ ] 19.6 Evidence & communication.
  - [ ] 19.6.1 Update `docs/windows-gui-guide.md` with packaging prerequisites and troubleshooting tips.
  - [ ] 19.6.2 Summarise packaging readiness status in `notes/status/gui-research.md` and cross-link from this area when closing.
  - [ ] 19.6.3 Keep `docs/windows-gui-notes.md` appendices aligned with packaging outputs, including template NOTICE entries.

# End of priority queue
<!-- PR prepared: 2025-10-22T09:46:54Z -->
<!-- make_pr anchor -->
