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

## A0g++++++++. Font Telemetry Retention Audit Trails [deps=A0g+++++++]

**REASON:** Retention metrics alone lacked artifact-level evidence, making it hard to prove which
logs were removed during pruning reviews.

**MUST NOT:** Alter existing deletion counts or change default retention behaviour.

**MUST:** Capture deleted filenames with reasons, expose them through retention payloads, and
update operator guidance.

**ACCEPT GATES:** Metrics enumerate deleted logs with reasons, CLI tests pin the structure, and
docs tell reviewers how to leverage the data.

**REQUIRED RELATED WORK:**
- [x] 0.22 Expand retention metrics audit data.
  - [x] 0.22.1 Track deleted filenames in `_RetentionMetrics` and expose aggregated `deletedFiles`.
  - [x] 0.22.2 Surface filename data in retention payloads so JSON exports capture the audit trail.
  - [x] 0.22.3 Extend regression coverage in `tests/scripts/test_font_health_summary.py` for the new fields.
  - [x] 0.22.4 Document filename tracking expectations in `docs/font-telemetry.md`.

## A0g+++++++. Font Telemetry Retention Metrics Redirect [deps=A0g++++++]

**REASON:** Operators archiving investigation bundles need deterministic control over where
retention metrics land (or to skip the artifact) without losing inline visibility.

**MUST NOT:** Remove the default metrics export or break existing log/summary writes.

**MUST:** Provide a configurable retention metrics path, preserve inline output for audits, and
document the new workflow for reviewers.

**ACCEPT GATES:** Default behaviour stays unchanged, overrides land in the requested path, tests
cover override/disable scenarios, and docs/runbooks reflect the new flag.

**REQUIRED RELATED WORK:**
- [x] 0.21 Add a `--retention-metrics-path` flag to `scripts/font_health_summary.py` supporting
      overrides and `-` disable while keeping printed metrics.
  - [x] 0.21.1 Extend `tests/scripts/test_font_health_summary.py` to cover override and disabled
        outputs.
  - [x] 0.21.2 Document the flag in `docs/font-telemetry.md` with disable guidance.
  - [x] 0.21.3 Update operator notes in `notes/status/font-telemetry.md` about recording the
        retention metrics destination.

## A0g++++++. Font Telemetry Retention Diagnostics [deps=A0g+++++]

**REASON:** Operators reviewing retention churn need immediate visibility into how pruning switches
impact log counts without opening the metrics JSON manually.

**MUST NOT:** Change the retention metrics file location or remove existing telemetry payloads.

**MUST:** Provide a CLI flag that surfaces retention metrics inline, extend regression coverage for
the new output, and update operator runbooks.

**ACCEPT GATES:** CLI prints the retention summary only when requested, tests pin the formatted
output, and docs capture usage guidance plus review expectations.

**REQUIRED RELATED WORK:**
- [x] 0.20 Add a `--print-retention-metrics` flag to `scripts/font_health_summary.py` that emits
      a human-readable retention summary while keeping JSON files unchanged.
  - [x] 0.20.1 Cover the flag behaviour (enabled vs default) in
        `tests/scripts/test_font_health_summary.py`.
  - [x] 0.20.2 Document the workflow in `docs/font-telemetry.md` with review notes in
        `notes/status/font-telemetry.md`.
  - [x] 0.20.3 Keep the retention metrics payload available to callers (return value) for future
        automation hooks.

## A0g+++++. Font Telemetry Log Age Retention [deps=A0g++++]

**REASON:** Operators now cap log counts, but forensic reviews still need a deterministic way to
drop stale JSON events once they exceed a retention window.

**MUST NOT:** Delete the aggregated summary snapshot or bypass existing count-based pruning.

**MUST:** Provide an age-based retention switch, ensure combined switches behave predictably, and
update operator guidance.

**ACCEPT GATES:** Age flag deletes stale events while preserving fresh telemetry, coverage exercises
negative and positive paths, and docs capture usage plus review expectations.

**REQUIRED RELATED WORK:**
- [x] 0.19 Add `--max-log-age-hours` pruning to `scripts/font_health_summary.py`.
  - [x] 0.19.1 Cover age-pruning scenarios (retention, disabled, interaction) in
        `tests/scripts/test_font_health_summary.py`.
  - [x] 0.19.2 Document the age retention workflow in `docs/font-telemetry.md` and
        `notes/status/font-telemetry.md`.
- [x] 0.19.3 Prototype a retention metrics sampler in `scripts/font_health_summary.py` to report
        deleted counts for future automation.

## A0g++++. Font Telemetry Log Retention Controls [deps=A0g+++]

**REASON:** Event logs accumulate quickly in sandboxed triage environments, so operators need a
bounded retention toggle without scripting manual clean-ups.

**MUST NOT:** Remove the aggregated summary snapshot or loosen existing guardrail defaults.

**MUST:** Provide a CLI switch that prunes historical logs, ensure regression coverage for pruning
behaviour, and document the operational workflow.

**ACCEPT GATES:** Flag deletes older event files deterministically, tests cover positive/negative
paths, and docs tell operators how to configure retention.

**REQUIRED RELATED WORK:**
- [x] 0.18 Add a `--max-log-files` retention flag to `scripts/font_health_summary.py` with pruning
      logic for structured events.
  - [x] 0.18.1 Cover pruning scenarios (limited, disabled, error handling) in
        `tests/scripts/test_font_health_summary.py`.
  - [x] 0.18.2 Document retention guidance in `docs/font-telemetry.md`.

## A0g+++. Font Telemetry Log Overrides [deps=A0g++]

**REASON:** Operators need to redirect structured staleness logs when triaging evidence in
isolated sandboxes.

**MUST NOT:** Break existing log layouts or disable environment variable overrides.

**MUST:** Provide an explicit CLI flag, confirm precedence over environment settings, and update
docs covering the new control.

**ACCEPT GATES:** CLI flag stores events in the supplied path, tests cover precedence, and docs
explain the override behaviour.

**REQUIRED RELATED WORK:**
- [x] 0.17 Add a CLI `--log-dir` override in `scripts/font_health_summary.py`.
  - [x] 0.17.1 Cover override precedence with regression tests under
        `tests/scripts/test_font_health_summary.py`.
  - [x] 0.17.2 Document the flag in `docs/font-telemetry.md`.

## A0g++. Font Telemetry Summary Snapshot [deps=A0g+]

**REASON:** Operators need a quick-glance rollup of staleness enforcement without parsing full
scenario dumps.

**MUST NOT:** Drop structured event logging or hide issue details required for incident review.

**MUST:** Emit an aggregated summary artifact, provide configuration controls, cover regression
behaviour, and document operator touchpoints.

**ACCEPT GATES:** CLI runs produce a refreshed summary file, tests validate the payload, and docs
highlight where to consume the artifact.

**REQUIRED RELATED WORK:**
- [x] 0.15 Emit an aggregated summary log alongside staleness events in
      `scripts/font_health_summary.py`.
  - [x] 0.15.1 Implement the summary payload builder with a disable override and default location.
  - [x] 0.15.2 Add regression coverage verifying the summary JSON content.
- [x] 0.16 Document the summary artifact workflow in `docs/font-telemetry.md`.

## A0g+. Font Telemetry Staleness Follow-through [deps=A0g]

**REASON:** The newly added stale timestamp guard needs production-grade polish so operators can tune, observe, and document the behaviour without reverse-engineering the initial patch.

**MUST NOT:** Relax existing staleness enforcement thresholds or bypass regression coverage introduced in A0g.

**MUST:** Provide operational documentation, observability hooks, fixture coverage for boundary cases, and backlog tracking for open telemetry gaps.

**ACCEPT GATES:** Operators can audit stale timestamp handling end-to-end via docs, logs, and tests; alerts expose actionable metadata; deferred telemetry clean-up stories recorded.

**REQUIRED RELATED WORK:**
- [x] 0.11 Document staleness tuning knobs in `docs/font-telemetry.md` with troubleshooting flowcharts.
  - [x] 0.11.1 Capture CLI usage examples covering `--max-last-updated-age` overrides.
- [x] 0.12 Instrument alert outputs to emit structured log events under `artifacts/logs/font-staleness/`.
  - [x] 0.12.1 Extend `scripts/font_health_summary.py` to mirror new log metadata.
- [x] 0.13 Add regression tests for boundary timestamp drift in `tests/scripts/test_font_health_summary.py`.
  - [x] 0.13.1 Cover both below-threshold and above-threshold cases with fixture snapshots.
- [x] 0.14 Create backlog entry in `notes/status/font-telemetry.md` tracking upstream telemetry fixes and remaining risks.
  - [x] 0.14.1 Outline follow-up owner expectations and review cadence.

## A0g. Font Telemetry Staleness Alerts [deps=A0f]

**REASON:** Telemetry feeds occasionally stall without updating timestamps, hiding stale headless scenarios until regressions ship.

**MUST NOT:** Relax existing failure-rate thresholds or skip required scenario checks while adding the staleness guard.

**MUST:** Detect stale scenario timestamps, expose CLI toggles for operators, and lock coverage with regression tests.

**ACCEPT GATES:** Evaluation highlights stale or missing timestamps, CLI flag enforces guardrails, and tests pin behaviour.

**REQUIRED RELATED WORK:**
- [x] 0.9 Guard scenario staleness detection in `src/driftbuster/font_health.py`.
  - [x] 0.9.1 Add `max_last_updated_age` evaluation coverage in `tests/scripts/test_font_health_summary.py`.
- [x] 0.10 Surface CLI controls for staleness tolerances.
  - [x] 0.10.1 Extend `scripts/font_health_summary.py` parser and enforcement.

## A0f. Font Telemetry Normalisation [deps=A0e]

**REASON:** Scenario name whitespace and case drift currently causes false missing flags in telemetry enforcement.

**MUST NOT:** Change telemetry payload structure or weaken existing drift detection thresholds.

**MUST:** Trim observed scenario names, align required matching to canonical casing, and lock coverage with regression tests.

**ACCEPT GATES:** Required scenario enforcement tolerates whitespace/case variations and CLI evaluation tests stay green.

**REQUIRED RELATED WORK:**
- [x] 0.8 Normalise scenario matching in evaluation flows.
  - [x] 0.8.1 Trim observed scenario names inside `src/driftbuster/font_health.py`.
  - [x] 0.8.2 Add regression coverage under `tests/scripts/test_font_health_summary.py` for whitespace-insensitive matching.

## A0e. Font Telemetry Scenario Enforcement [deps=A0]

**REASON:** Headless telemetry occasionally omits scenario entries when pipelines shift, hiding regressions needed for release validation.

**MUST NOT:** Overwrite collected telemetry or relax existing drift thresholds.

**MUST:** Flag missing scenario coverage in evaluations, expose CLI enforcement, and extend regression tests.

**ACCEPT GATES:** Evaluation reports treat absent scenarios as issues, CLI surface supports required names, and tests cover the new guardrails.

**REQUIRED RELATED WORK:**
- [x] 0.5 Detect missing scenarios inside `src/driftbuster/font_health.py` evaluations.
  - [x] 0.5.1 Extend `ReportEvaluation` to track absent scenarios and update `format_report` output.
- [x] 0.6 Add CLI controls in `scripts/font_health_summary.py` to require scenarios.
- [x] 0.7 Expand `tests/scripts/test_font_health_summary.py` coverage for missing scenario handling.

## A0. FontManager Regression Hardening [deps=]

**REASON:** Release builds still intermittently lose the headless `FontManager` alias chain, causing glyph resolution crashes before the window tree materialises.

**MUST NOT:** Regress the existing `HeadlessFontBootstrapper` flow or introduce platform-specific font hard-coding.

**MUST:** Capture the current regression, stabilise the proxy bindings, validate Release/Debug parity, and document recovery steps for operators.

**ACCEPT GATES:** Reproduction tests cover the regression, proxy changes keep headless glyph loads deterministic, Release + Debug smoke tests stay green, and updated bootstrap guidance ships.

**REQUIRED RELATED WORK:**
- [x] 0.1 Capture the regression evidence.
- [x] 0.1.1 Extend `gui/DriftBuster.Gui.Tests/Ui/HeadlessBootstrapperSmokeTests.cs` with a Release-mode reproduction asserting `FontManager.SystemFonts` still exposes the Inter alias.
  - [x] 0.1.2 Archive the failing stack trace under `artifacts/logs/fontmanager-regression.txt` and summarise it in `notes/status/gui-research.md#fontmanager-regression`.
- [x] 0.2 Harden the headless proxy bindings.
  - [x] 0.2.1 Update `gui/DriftBuster.Gui/Headless/HeadlessFontManagerProxy.cs` to guard `TryCreateGlyphTypeface`/`TryMatchCharacter` fallbacks and normalise alias lookups.
- [x] 0.2.2 Ensure `gui/DriftBuster.Gui/Headless/HeadlessFontBootstrapper.cs` seeds `FontManagerOptions.Fallbacks` deterministically for both Inter and the alias entry.
- [x] 0.3 Validate Release/Debug bootstrap parity.
- [x] 0.3.1 Add targeted fixture coverage in `gui/DriftBuster.Gui.Tests/Ui/HeadlessFixture.cs` verifying the same default family in Release and Debug.
- [x] 0.3.2 Record verification notes and commands in `docs/windows-gui-guide.md#fontmanager-regression-playbook` for operators.
- [x] 0.4 Monitor metrics and regression drift.
  - [x] 0.4.1 Pipe bootstrapper health telemetry into `artifacts/logs/headless-font-health.json` during GUI smoke tests.
  - [x] 0.4.2 Add a condensed status rollup to `notes/status/gui-research.md#fontmanager-regression` describing pass/fail trends.

## A1a. Headless Font Guardrails [deps=]

**REASON:** Remove the font bootstrap gap that currently breaks headless Avalonia runs, letting existing GUI test fixtures boot without manual font installs.

**MUST NOT:** Alter drag/drop ordering or async run coordination while touching the App bootstrapper.

**MUST:** Preload Avalonia fonts in the headless startup path, verify the font dictionary exists during tests, and document the bootstrap expectation for Windows operators.

**ACCEPT GATES:** Headless fixtures pass font assertions; new bootstrap instructions live under `docs/windows-gui-guide.md#headless-bootstrap`; seed logs archived for regression evidence.

**REQUIRED RELATED WORK:**
- [x] 1.1 Seed Avalonia headless fonts in `gui/DriftBuster.Gui/App.axaml.cs`.
  - [x] 1.1.1 Inject `fonts:SystemFonts` preload inside `BuildAvaloniaApp()` to eliminate `KeyNotFoundException` on headless startup.
  - [x] 1.1.2 Extend `gui/DriftBuster.Gui.Tests/Ui/HeadlessFixture.cs` to assert the font dictionary is populated before windows instantiate.
- [x] 1.2 Document headless bootstrap guardrails.
  - [x] 1.2.1 Document the headless font preload requirement in `docs/windows-gui-guide.md#headless-bootstrap`.
- [x] 1.3 Capture headless boot evidence.
  - [x] 1.3.1 Capture headless boot logs in `artifacts/logs/headless-font-seed.txt`.

## A1b. Drilldown Command Determinism [deps=A1a]

**REASON:** `ShowDrilldownForHostCommand` remains flaky without deterministic gating, blocking multi-server drilldowns in headless validation runs.

**MUST NOT:** Introduce async deadlocks or regress RunAll command safety while updating the command flow.

**MUST:** Tighten `CanExecute` gating, log drilldown transitions, and emit structured telemetry so readiness can be audited.

**ACCEPT GATES:** Deterministic tests exist in `ServerSelectionViewModelAdditionalTests`; telemetry captured to `artifacts/logs/drilldown-ready.json`; transition logs land in `notes/status/gui-research.md`.

**REQUIRED RELATED WORK:**
- [x] 1.1 Stabilise `ShowDrilldownForHostCommand` gating within `gui/DriftBuster.Gui/ViewModels/ServerSelectionViewModel.cs`.
  - [x] 1.1.1 Add deterministic `CanExecute` coverage to `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs`.
  - [x] 1.1.2 Log drilldown transitions to `notes/status/gui-research.md` for regression evidence.
  - [x] 1.1.3 Emit structured telemetry for drilldown readiness via `ILogger` into `artifacts/logs/drilldown-ready.json`.

## A1c. Awaitable Session Cache Migration [deps=A1b]

**REASON:** Cache migration still blocks shutdown when kicked from multi-server runs; making it awaitable prevents race-induced corruption.

**MUST NOT:** Regress legacy cache discovery or introduce untracked background threads.

**MUST:** Await cache migration, stress it with concurrent tests, and document outcomes within status notes.

**ACCEPT GATES:** Concurrent migration tests in place; in-memory fake covers concurrent upgrades; migration counters and sample output logged in `notes/status/gui-research.md`.

**REQUIRED RELATED WORK:**
- [x] 1.1 Make cache migration awaitable in `gui/DriftBuster.Gui/Services/SessionCacheService.cs`.
  - [x] 1.1.1 Expand `gui/DriftBuster.Gui.Tests/Services/SessionCacheServiceTests.cs` to cover multi-threaded migrations and legacy cache discovery.
  - [x] 1.1.2 Update `gui/DriftBuster.Gui.Tests/Fakes/InMemorySessionCacheService.cs` to simulate concurrent cache upgrades.
  - [x] 1.1.3 Add migration success/failure counters and capture sample output in `notes/status/gui-research.md`.

## A1d. Multi-Server Validation Rollup [deps=A1c]

**REASON:** Once guardrails land, the suite still needs validation runs and documentation so multi-server persistence stays traceable across releases.

**MUST NOT:** Skip Release-mode GUI tests or leave docs outdated.

**MUST:** Re-run the GUI test matrix, refresh coverage reports, and update the persistence walkthrough plus research summary.

**ACCEPT GATES:** Release + Debug GUI tests rerun; coverage report generated; docs updated with persistence flow; status notes summarise the guardrail work.

**REQUIRED RELATED WORK:**
- [x] 1.0 Headless font bootstrap remediation.
  - [x] 1.0.1 Resolve `fonts:SystemFonts` lookup failures blocking Avalonia window construction during Release GUI tests.
    - [x] 1.0.1.a Capture the current Release failure stack trace and link it under `notes/status/gui-research.md#headless-font-issues`.
    - [x] 1.0.1.b Introduce a minimal headless font bootstrapper that seeds the Avalonia locator with an `Inter` fallback without touching compiled resources.
    - [x] 1.0.1.c Verify the bootstrapper against a single `[Collection(HeadlessCollection.Name)]` smoke test before attempting the full suite.
- [x] 1.0.2 Bind a deterministic headless `IFontManagerImpl` (Inter fallback) and cover it via `HeadlessFixture` smoke asserts.
  - [x] 1.0.2.a Map the existing `IFontManagerImpl` call sites and document the binding strategy in `docs/windows-gui-guide.md#headless-bootstrap`.
  - [x] 1.0.2.b Implement the locator binding with a wrapper/proxy that keeps glyph loading synchronous for tests.
  - [x] 1.0.2.c Extend `HeadlessFixture` with targeted asserts for default family, glyph resolution, and alias preservation.
- [x] 1.0.3 Capture the remediation notes and stack trace context in `notes/status/gui-research.md#headless-font-issues`.
    - [x] 1.0.3.a Summarise the failing scenarios pre-fix (Release window creation, Drilldown view instantiation).
    - [x] 1.0.3.b Record the applied binding approach, including proxy details and locator hooks.
    - [x] 1.0.3.c Attach before/after test evidence referencing the new smoke assertions.
- [x] 1.1 Validation & coverage.
- [x] 1.1.0 Capture current Release GUI regression evidence (fonts:SystemFonts alias + cache locks) under `artifacts/logs/gui-validation/` and summarise failure signatures in `notes/status/gui-research.md#multi-server-validation-rollup`.
- [x] 1.1.1 Investigate Release headless bootstrapper gap causing `FontManager.SystemFonts` to miss the `fonts:SystemFonts` alias before window creation (track findings in `gui/DriftBuster.Gui/Headless/*`).
  - [x] 1.1.2 Stabilise session cache migration writes so concurrent save/load tests stop throwing `/tmp/.../multi-server.json` lock violations in Release builds (`gui/DriftBuster.Gui/Services/SessionCacheService.cs` + tests).
  - [x] 1.1.3 Re-run `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release` once font alias + cache fixes land and archive the green log beside the failure capture.
    - [x] 1.1.3.a Diagnose persistent `fonts:SystemFonts` alias misses despite headless alias collection (Release run).
    - [x] 1.1.3.b Capture updated Release failure logs highlighting `BuildMultiServerRequest` migration gaps post-run.
    - [x] 1.1.3.c Restore `fonts:SystemFonts` dictionary seeding for Release headless runs so the alias probe and cache migration assertions pass.
  - [x] 1.1.4 Re-run `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Debug` to confirm debug builds stay stable after the Release fixes.
    - [x] 1.1.4.a Archive the 2025-10-26 Debug failure log capturing the alias miss for follow-up diagnostics.
  - [x] 1.1.5 Execute `python -m scripts.coverage_report` after GUI tests to keep shared coverage reporting in sync.
    - [x] 1.1.5.a Document the missing Cobertura XML signal so coverage generation can be retried once GUI suites are green again.
- [x] 1.2 Evidence & documentation.
  - [x] 1.2.1 Update `docs/multi-server-demo.md` and `docs/windows-gui-guide.md` with persistence walkthrough + font preload notes.
  - [x] 1.2.2 Summarise findings in `notes/status/gui-research.md` under the multi-server guardrails section.

## A2. Diff Planner Productivity (Phase 2) [deps=A1]

**REASON:** Elevates diff planning workflows (P5) by persisting MRU choices and dual-pane JSON views so analysts avoid repetitive setup.

**MUST NOT:** Leak sensitive payloads in MRU storage; regress existing diff rendering; bypass coverage for new serialization code.

**MUST:** Define persisted MRU contract, surface sanitised JSON panes, expand backend diff payload, and document the workflow.

**ACCEPT GATES:** MRU spec documented; GUI + backend tests green; sanitized outputs validated and logged.

**REQUIRED RELATED WORK:**
- [x] 2.1 Finalise MRU requirements.
  - [x] 2.1.1 Record UX notes and data contract in `notes/status/gui-research.md`.
  - [x] 2.1.2 Add persisted store (new `gui/DriftBuster.Gui/Services/DiffPlannerMruStore.cs`) leveraging `DriftbusterPaths`.
  - [x] 2.1.3 Cover serialization/deserialization paths in `gui/DriftBuster.Gui.Tests/Services/DiffPlannerMruStoreTests.cs`.
  - [x] 2.1.4 Provide migration logic for existing diff planner settings (versioned storage) with regression tests covering legacy data.
- [x] 2.2 Update GUI surfaces.
- [x] 2.2.1 Extend `gui/DriftBuster.Gui/ViewModels/DiffViewModel.cs` with MRU dropdowns and dual-pane JSON viewer toggles.
  - [x] 2.2.2 Modify `gui/DriftBuster.Gui/Views/DiffView.axaml` to render sanitized JSON panes with accessible automation IDs.
  - [x] 2.2.3 Ensure clipboard helpers refuse unsanitized payloads (`gui/DriftBuster.Gui/ViewModels/DiffViewModel.cs` logic + tests).
  - [x] 2.2.4 Add UI automation coverage in `gui/DriftBuster.Gui.Tests/Ui/DiffViewUiTests.cs` verifying MRU selection and JSON pane toggles.
- [x] 2.3 Extend backend contracts.
  - [x] 2.3.1 Update `gui/DriftBuster.Backend/DriftbusterBackend.cs` and `gui/DriftBuster.Backend/Models/DiffResult.cs` to emit raw + summarized JSON.
  - [x] 2.3.2 Mirror payload changes in `src/driftbuster/reporting/diff.py` and related helpers.
  - [x] 2.3.3 Add regression coverage in `gui/DriftBuster.Gui.Tests/ViewModels/DiffViewModelTests.cs` and `tests/multi_server/test_multi_server.py`.
  - [x] 2.3.4 Document payload schema in `docs/windows-gui-guide.md#diff-planner` and store samples under `artifacts/samples/diff-planner/`.
- [x] 2.4 Documentation & validation.
  - [x] 2.4.1 Refresh `docs/windows-gui-guide.md` and `docs/ux-refresh.md` with MRU instructions and sanitized screenshots.
  - [x] 2.4.2 Log manual validation steps under `notes/status/gui-research.md`.
  - [x] 2.4.3 Re-run `dotnet test ...` and `coverage run --source=src/driftbuster -m pytest tests/multi_server/test_multi_server.py` ensuring ≥90%.
  - [x] 2.4.4 Archive validation artefacts (screenshots, JSON payloads, command output) in `artifacts/diff-planner-validation/README.md`.
- [x] 2.5 Security & telemetry.
  - [x] 2.5.1 Add structured logging for sanitized-vs-raw payload rejects in `DiffViewModel`.
  - [x] 2.5.2 Extend privacy guardrails in `docs/legal-safeguards.md` to cover MRU storage.
  - [x] 2.5.3 Capture telemetry sample results in `notes/checklists/legal-review.md`.

## A3. Performance & Async Stability (Phase 3) [deps=A2]

**REASON:** Keeps UI responsive at scale (P6), eliminating timeline jank and asynchronous update drops for large scans.

**MUST NOT:** Ship virtualization without opt-out; regress accessibility focus order; leave dispatcher queue unbounded.

**MUST:** Capture diagnostics baseline, introduce virtualization heuristics, buffer dispatcher updates, and add perf harness coverage.

**ACCEPT GATES:** Baseline metrics recorded; virtualization toggles documented; perf harness integrated into `scripts/verify_coverage.sh`.

**REQUIRED RELATED WORK:**
- [x] 3.1 Capture baseline diagnostics.
  - [x] 3.1.1 Run Avalonia diagnostics on large fixture scans and log results in `notes/status/gui-research.md`.
  - [x] 3.1.2 Add guidance to `docs/windows-gui-guide.md#performance`.
  - [x] 3.1.3 Store raw diagnostics export under `artifacts/perf/baseline.json`.
- [x] 3.2 Introduce virtualization.
  - [x] 3.2.1 Apply `ItemsRepeater` + `VirtualizingStackPanel` to high-volume views in `gui/DriftBuster.Gui/Views/ResultsCatalogView.axaml` and `gui/DriftBuster.Gui/Views/ServerSelectionView.axaml`.
  - [x] 3.2.2 Guard virtualization behind heuristics in `gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs`.
  - [x] 3.2.3 Add UI tests in `gui/DriftBuster.Gui.Tests/Ui` covering virtualization toggles.
  - [x] 3.2.4 Document virtualization fallback toggle for low-memory hosts in `docs/windows-gui-guide.md`.
- [x] 3.3 Buffer dispatcher updates.
  - [x] 3.3.1 Implement buffered queue in `gui/DriftBuster.Gui/Services/ToastService.cs` (or new progress dispatcher service).
  - [x] 3.3.2 Add async unit tests in `gui/DriftBuster.Gui.Tests/Services`.
  - [x] 3.3.3 Mirror progress throttling in Python `src/driftbuster/multi_server.py` for CLI parity with tests in `tests/multi_server/test_multi_server.py`.
  - [x] 3.3.4 Record timing metrics before/after in `notes/status/gui-research.md`.
- [x] 3.4 Perf harness + validation.
  - [x] 3.4.1 Create `gui/DriftBuster.Gui.Tests/Ui/PerformanceSmokeTests.cs` exercising virtualization/perf toggles.
  - [x] 3.4.2 Wire optional perf flag into `scripts/verify_coverage.sh`.
  - [x] 3.4.3 Document runbook in `notes/status/gui-research.md`.
  - [x] 3.4.4 Schedule weekly perf checks in `notes/checklists/perf-calendar.md` with recorded metrics.
- [x] 3.5 Evidence & release notes.
- [x] 3.5.1 Update `docs/release-notes.md` with performance improvements summary.
- [x] 3.5.2 Archive perf charts and measurements under `artifacts/perf/`.

## A4. Theme & Responsiveness (Phase 4) [deps=A3]

**REASON:** Delivers P7 visual polish with high-contrast palettes and responsive spacing for large displays.

**MUST NOT:** Ship palettes without contrast verification; break existing theme tokens; forget to regenerate screenshots.

**MUST:** Add Dark+/Light+ tokens, responsive breakpoints, update assets, and capture before/after evidence.

**ACCEPT GATES:** Contrast ratios ≥ WCAG AA recorded; updated screenshots stored under `docs/ux-refresh.md`; release notes refreshed.

**REQUIRED RELATED WORK:**
- [x] 4.1 Theme tokens.
  - [x] 4.1.1 Extend `gui/DriftBuster.Gui/Assets/Styles/Theme.axaml` with new token sets and migration defaults.
  - [x] 4.1.2 Update `gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs` to surface theme selectors.
  - [x] 4.1.3 Add theme migration tests in `gui/DriftBuster.Gui.Tests/ViewModels`.
  - [x] 4.1.4 Document palette tokens and migration defaults in `docs/windows-gui-guide.md#themes`.
- [x] 4.2 Responsive spacing.
  - [x] 4.2.1 Add breakpoint resources for 1280/1600/1920 widths in `gui/DriftBuster.Gui/Assets/Styles/Notifications.axaml` and layout-specific resource dictionaries.
  - [x] 4.2.2 Modify `gui/DriftBuster.Gui/Views/MainWindow.axaml` and `ServerSelectionView.axaml` to consume the spacing tokens.
  - [x] 4.2.3 Extend UI tests to validate layout shifts at different widths.
  - [x] 4.2.4 Capture layout change matrix in `notes/status/gui-research.md`.
- [x] 4.3 Asset refresh.
  - [x] 4.3.1 Capture new screenshots and store under `docs/ux-refresh.md`.
  - [x] 4.3.2 Update `docs/windows-gui-guide.md` and `docs/release-notes.md` with visuals.
  - [x] 4.3.3 Maintain screenshot manifest in `docs/ux-refresh.md#asset-inventory`.
- [x] 4.4 Validation.
  - [x] 4.4.1 Run contrast tooling (`scripts/coverage_report.py` optional hook + manual audit) and log results.
  - [x] 4.4.2 Execute regression tests: `dotnet test`, `pytest`, and manual multi-server run with theme toggles documented in `notes/status/gui-research.md`.
  - [x] 4.4.3 Log accessibility audit results (tool, version, outcome) in `notes/checklists/accessibility-report.md`.
- [x] 4.5 Release communication.
  - [x] 4.5.1 Add theme change summary to `CHANGELOG.md` and note screenshot refresh in `docs/release-notes.md`.

## A5. Results Catalog Alignment (Phase 5) [deps=A4]

**REASON:** Upgrades Avalonia APIs (P8) to 11.2-safe surfaces and prevents toast/catalog regressions blocking GUI releases.

**MUST NOT:** Leave deprecated sort helpers; regress toast resource lookups; skip regression tests.

**MUST:** Swap to Avalonia 11.2 sorting APIs, fix toast converters, add regression coverage, and document migration.

**ACCEPT GATES:** Avalonia 11.2 builds pass; tests covering sorting/toasts run green; migration appendix updated.

**REQUIRED RELATED WORK:**
- [x] 5.1 Sorting API migration.
  - [x] 5.1.1 Replace deprecated sorting types in `gui/DriftBuster.Gui/Views/ResultsCatalogView.axaml` and code-behind.
  - [x] 5.1.2 Update `gui/DriftBuster.Gui/ViewModels/ResultsCatalogViewModel.cs` logic accordingly.
  - [x] 5.1.3 Add regression tests under `gui/DriftBuster.Gui.Tests/ViewModels/ResultsCatalogViewModelTests.cs`.
  - [x] 5.1.4 Capture before/after sorting behaviour in `notes/status/gui-research.md` with screenshots or logs.
- [x] 5.2 Toast resource refactor.
  - [x] 5.2.1 Update converters in `gui/DriftBuster.Gui/Converters` to use Avalonia 11.2 resource lookups.
  - [x] 5.2.2 Expand tests in `gui/DriftBuster.Gui.Tests/Converters`.
  - [x] 5.2.3 Document toast resource lookup changes in `docs/windows-gui-guide.md#notifications`.
- [x] 5.3 Build validation.
  - [x] 5.3.1 Rebuild GUI with Avalonia 11.2 and capture results in `notes/status/gui-research.md`.
  - [x] 5.3.2 Run headless UI tests ensuring toasts and sorting propagate.
  - [x] 5.3.3 Store Release build artefacts and hash outputs in `artifacts/builds/avalonia-11-2/`.
- [x] 5.4 Documentation.
  - [x] 5.4.1 Update `docs/windows-gui-guide.md` (or appendix) with migration notes.
  - [x] 5.4.2 Reflect release-blocker resolution in `CHANGELOG.md`.
  - [x] 5.4.3 Cross-link updated guidance from `docs/ux-refresh.md` and `docs/release-notes.md`.
- [x] 5.5 Evidence.
  - [x] 5.5.1 Archive failing vs fixed test output in `artifacts/logs/results-catalog/`.
  - [x] 5.5.2 Capture manifest tying logs + annotations with deterministic hashes.

## A6. Quality Sweep & Release Prep (Phase 6) [deps=A5]

**REASON:** Completes S1/S2 wrap-up with coverage, regression evidence, and release collateral for the GUI + Python stack.

**MUST NOT:** Drop coverage below 90%; skip smoke tests; omit changelog updates.

**MUST:** Re-run full test battery, refresh docs/assets, and compile release notes with evidence.

**ACCEPT GATES:** Coverage gates met for Python/.NET; smoke runs recorded; changelog ready for tag.

**REQUIRED RELATED WORK:**
- [x] 6.1 Coverage enforcement.
  - [x] 6.1.1 Run `coverage run --source=src/driftbuster -m pytest -q && coverage report --fail-under=90`.
- [x] 6.1.2 Execute `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj`.
  - [x] 6.1.3 Capture combined summary via `python -m scripts.coverage_report`.
  - [x] 6.1.4 Store coverage artefacts (HTML, XML) under `artifacts/coverage/final/`.
  - [x] 6.1.5 Provide cross-platform automation via `scripts/verify_coverage.py`.
- [x] 6.2 Smoke & manual runs.
  - [x] 6.2.1 Trigger packaged smoke (`scripts/smoke_multi_server_storage.sh`) and log outputs in `notes/status/gui-research.md`.
  - [x] 6.2.2 Execute manual multi-server session, verifying persistence + diff planner features.
  - [x] 6.2.3 Record session walkthrough (screen capture + notes) and archive under `artifacts/manual-runs/`.
- [x] 6.3 Docs & assets.
  - [x] 6.3.1 Refresh `docs/ux-refresh.md`, `docs/windows-gui-guide.md`, and `docs/release-notes.md` with final screenshots + notes.
  - [x] 6.3.2 Update `README.md` and `docs/multi-server-demo.md` with summary of new capabilities.
  - [x] 6.3.3 Ensure `docs/README.md` index references updated assets.
- [x] 6.4 Release evidence.
  - [x] 6.4.1 Update `CHANGELOG.md` and `notes/status/gui-research.md` with validation checklist.
  - [x] 6.4.2 Archive artifacts under `artifacts/` as needed and note retention plan.
  - [x] 6.4.3 Compile release handoff bundle (`notes/releases/next.md`) summarising evidence.

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
  - [x] 7.3.1 Extend `src/driftbuster/registry/__init__.py::registry_summary` with usage statistics.
  - [ ] 7.3.2 Capture manual review steps in `notes/checklists/reporting-tests.md`.
  - [x] 7.3.3 Add regression coverage verifying usage statistics in `tests/registry/test_registry_summary.py`.
- [x] 7.4 Diff/patch utilities.
  - [x] 7.4.1 Finalise helpers in `src/driftbuster/reporting/diff.py` for before/after comparisons.
  - [x] 7.4.2 Add regression coverage in `tests/reporting/test_diff.py` (new) and update docs.
  - [x] 7.4.3 Document diff helper behaviour in `notes/checklists/reporting-tests.md` and link from `docs/format-playbook.md`.
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
- [x] 16.1 Export routines.
  - [x] 16.1.1 Implement portable exports in `scripts/capture.py` (new subcommand).
  - [x] 16.1.2 Add connectors in `src/driftbuster/offline_runner.py`.
  - [x] 16.1.3 Provide tests `tests/offline/test_sql_snapshots.py`.
  - [x] 16.1.4 Document sample database schemas and anonymisation steps in `fixtures/sql/README.md`.
- [x] 16.2 Integration.
  - [x] 16.2.1 Wire exports into CLI/PowerShell surfaces.
  - [x] 16.2.2 Update manifests to record database metadata.
  - [x] 16.2.3 Add CLI/PowerShell usage examples to `docs/registry.md` and `README.md`.
- [x] 16.3 Documentation & legal.
  - [x] 16.3.1 Update `docs/registry.md` and `docs/legal-safeguards.md`.
  - [x] 16.3.2 Log approvals in `notes/checklists/legal-review.md`.
  - [x] 16.3.3 Capture retention policy for database snapshots in `docs/legal-safeguards.md#retention`.
- [x] 16.4 Validation.
  - [x] 16.4.1 Capture sample run, store evidence in `notes/status/gui-research.md`, and ensure retention plan recorded.
  - [x] 16.4.2 Archive database export checksums in `artifacts/sql/`.

## A17. PowerShell Module Delivery [deps=A10]

**REASON:** Ships Windows-first module exposing backend diff, hunt, and run-profile workflows from the consolidated backlog.

**MUST NOT:** Depend on unbuilt backend; break sync APIs; skip linting.

**MUST:** Implement module commands, ensure JSON output parity, add validation tests, and document packaging.

**ACCEPT GATES:** Module passes `Invoke-ScriptAnalyzer`; commands validated against fixtures; README/doc updates live.

**REQUIRED RELATED WORK:**
 - [x] 17.1 Module implementation.
  - [x] 17.1.1 Flesh out cmdlets in `cli/DriftBuster.PowerShell/DriftBuster.psm1`.
  - [x] 17.1.2 Load latest backend assembly via `DriftbusterPaths.GetCacheDirectory`.
  - [x] 17.1.3 Document module initialisation flow in `docs/windows-gui-guide.md#powershell-module`.
 - [x] 17.2 Validation.
  - [x] 17.2.1 Add Pester tests (new `cli/DriftBuster.PowerShell.Tests`) covering `Test-DriftBusterPing`, diff/hunt, run-profile commands.
  - [x] 17.2.2 Ensure JSON outputs match `gui/DriftBuster.Backend` models.
  - [x] 17.2.3 Track coverage via Pester `Invoke-Pester -OutputFormat NUnitXml` and store report in `artifacts/powershell/tests/`.
- [x] 17.3 Error handling.
  - [x] 17.3.1 Surface friendly errors when backend assembly missing.
  - [x] 17.3.2 Document fallback instructions in `README.md`.
  - [x] 17.3.3 Add troubleshooting section to `docs/windows-gui-guide.md#powershell-module`.
 - [x] 17.4 Packaging.
  - [x] 17.4.1 Update `scripts/package_powershell_module.ps1`.
  - [x] 17.4.2 Document usage in `docs/windows-gui-guide.md` and `README.md`.
  - [x] 17.4.3 Archive packaged module zip and checksum in `artifacts/powershell/releases/`.

## A18. Python CLI Concept (On Hold) [deps=A7]

**REASON:** Preserves legacy Python CLI blueprint for future resumption once Windows-first focus relaxes.

**MUST NOT:** Publish CLI before backlog clears; add dependencies beyond stdlib without justification; skip documentation.

**MUST:** Keep entry-point sketch current, align packaging checklist, maintain manual validation plan, and track open questions.

**ACCEPT GATES:** Blueprint synced with current detector API; manual commands rehearsed once resumed.

**REQUIRED RELATED WORK:**
- [x] 18.1 Entry-point prep.
  - [x] 18.1.1 Maintain `src/driftbuster/cli.py` stub aligning with `pyproject.toml` `[project.scripts]`.
  - [x] 18.1.2 Keep argument table in sync with detector capabilities.
  - [x] 18.1.3 Document stub status in `docs/README.md` (Plans & Notes section).
- [x] 18.2 Packaging checklist.
  - [x] 18.2.1 Track readiness steps in this queue until activation.
  - [x] 18.2.2 Update `README.md` placeholder section for CLI usage.
  - [x] 18.2.3 Record activation prerequisites in `notes/status/cli-plan.md`.
- [x] 18.3 Manual validation plan.
  - [x] 18.3.1 Migrate historical notes into `notes/status/cli-plan.md` (new) with command walkthroughs.
  - [x] 18.3.2 Capture expected outputs for JSON/HTML/diff commands using fixtures under `fixtures/`.
  - [x] 18.3.3 Store command transcripts in `artifacts/cli-plan/README.md`.
- [x] 18.4 Open questions.
  - [x] 18.4.1 Decide on confidence threshold flag handling.
  - [x] 18.4.2 Evaluate progress indicator requirements.
  - [x] 18.4.3 Determine packaging strategy (editable vs PyPI) once activated.
  - [x] 18.4.4 Track decision timeline in `notes/status/cli-plan.md`.

## A19. Windows GUI Packaging & Research (Windows shell readiness) [deps=A11]

**REASON:** Consolidates packaging, accessibility, and runtime decisions from `notes/status/gui-research.md` and `docs/windows-gui-notes.md` so the Windows-first shell can ship once reporting adapters land.

**MUST NOT:** Ship installers without recorded compliance sign-off; rely on online dependencies during offline packaging; skip accessibility tooling runs.

**MUST:** Finalise framework selection, document runtime prerequisites, capture accessibility baselines, script packaging flows (MSIX, portable zip, self-contained), and log offline/compliance evidence.

**ACCEPT GATES:** Updated `docs/windows-gui-notes.md` packaging sections, accessibility checklist completed, packaging smoke tests recorded across Windows 10/11, and evidence archived under `artifacts/gui-packaging/`.

**REQUIRED RELATED WORK:**
- [x] 19.1 Framework evaluation.
  - [x] 19.1.1 Review WinUI 3, Tkinter, PySimpleGUI, and Electron options; record decision matrix in `notes/status/gui-research.md`.
  - [x] 19.1.2 Sync `docs/windows-gui-notes.md#candidate-frameworks` with updated rationale and preferred pathway.
  - [x] 19.1.3 Capture licensing implications and NOTICE requirements in `docs/legal-safeguards.md#gui-frameworks`.
- [x] 19.2 Runtime prerequisites.
  - [x] 19.2.1 Decide on WebView2 Evergreen redistribution strategy; document installer steps in `docs/windows-gui-notes.md#packaging--distribution-plan`.
  - [x] 19.2.2 Validate `.NET` publish commands (framework-dependent vs self-contained) and log outputs in `notes/dev-host-prep.md`.
  - [x] 19.2.3 Store publish command transcripts + hashes in `artifacts/gui-packaging/` with README describing reproduction steps.
- [x] 19.3 Accessibility baseline.
  - [x] 19.3.1 Define Narrator/Inspect test matrix in `notes/status/gui-research.md#user-requirements`.
  - [x] 19.3.2 Update `docs/windows-gui-notes.md#compliance--accessibility-checklist` with step-by-step execution notes.
  - [x] 19.3.3 Archive accessibility run evidence (tool versions, pass/fail, screenshots) in `artifacts/gui-accessibility/`.
- [x] 19.4 Packaging workflows.
  - [x] 19.4.1 Produce MSIX packaging checklist and scripts in `notes/dev-host-prep.md`.
  - [x] 19.4.2 Document portable ZIP and self-contained bundle workflows in `docs/windows-gui-notes.md#packaging-quickstart`.
  - [x] 19.4.3 Execute installer smoke tests on Windows 10/11 VMs; log results and system prerequisites in `notes/status/gui-research.md`.
- [x] 19.5 Offline & compliance posture.
  - [x] 19.5.1 Ensure offline activation guidance lives in `docs/windows-gui-notes.md#distribution--licensing-notes`.
  - [x] 19.5.2 Update `docs/legal-safeguards.md` with packaging/licensing guardrails (NOTICE contents, WebView2 terms).
  - [x] 19.5.3 Record security review notes (hash verification, sideload steps) in `notes/checklists/legal-review.md`.
- [x] 19.6 Evidence & communication. (See `notes/status/gui-research.md#packaging-readiness-summary-a196` and `docs/windows-gui-notes.md#evidence-index-a196`.)
  - [x] 19.6.1 Update `docs/windows-gui-guide.md` with packaging prerequisites and troubleshooting tips.
  - [x] 19.6.2 Summarise packaging readiness status in `notes/status/gui-research.md` and cross-link from this area when closing.
  - [x] 19.6.3 Keep `docs/windows-gui-notes.md` appendices aligned with packaging outputs, including template NOTICE entries.

# End of priority queue
<!-- PR prepared: 2025-10-23T08:13:17Z -->
<!-- make_pr anchor -->
