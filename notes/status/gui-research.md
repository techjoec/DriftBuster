# Windows GUI Research Status

## Decisions & Findings

### Reporting adapters GUI bridge (A11.1.4)

- Mapped four-step integration so the Avalonia shell keeps delegating detection work to the Python CLI while surfacing HTML and JSON artefacts inline. Phase 1 keeps the backend bridge streaming JSON lines into view-models; phase 2 hosts HTML summaries inside a WebView2 panel with quick actions; phase 3 adds a split diff view pairing the rendered summary with raw patches; phase 4 layers checksum validation before export to keep evidence bundles honest.
- Captured smoke artefacts (`artifacts/reporting/2025-11-07/`) showing the JSON scan, HTML report, and diff patch with `[REDACTED]` placeholders plus a README describing regeneration commands. These outputs feed the GUI plan so the WebView and diff viewer have concrete fixtures while CLI remains the source of truth.

### Packaging readiness summary (A19.6)

- Portable and self-contained bundles both capture install/uninstall transcripts under `artifacts/gui-packaging/` with matching SHA256 manifests for offline validation. The evidence table in `docs/windows-gui-notes.md#evidence-index-a196` mirrors the exact filenames.
- WebView2 Evergreen runtime `124.0.2478.97` ships alongside every bundle; version and hash values logged in `notes/checklists/legal-review.md` for auditing.
- Signing certificate (`thumbprint ab12 cd34 ef56 7890`, expiry 2026-03-01) validated on Windows 10/11 VMs prior to MSIX installation. Stage future certificate exports under `artifacts/gui-packaging/certificates/` to keep the evidence tree predictable.
- Operators must import the signing certificate into `Trusted People` on test VMs and verify hashes before launching bundles; troubleshooting guidance now lives in `docs/windows-gui-guide.md#12-troubleshooting`.

### Reporting hold exit evidence capture (A10.3)

- Verified the hold-exit verification bundle now lives under the restricted `artifacts/hold-exit/` tree with a matching hash manifest (`verification-2025-10-31.sha256`) covering the `compile-lint.txt` transcript so auditors can validate the capture without reopening the raw log.
- Mirrored the bundle into the offline evidence share (`captures/reporting-hold/2025-10-31/`) with read access limited to the reporting review rota; retention window logged alongside the owner details in `notes/checklists/legal-review.md`.
- Recorded the compliance approval summary in `docs/legal-safeguards.md#hold-exit-briefing` and noted in this queue that no outstanding blockers remain for the evidence capture milestone.

### Installer smoke tests (A19.4.3)

- Recorded February 2025 smoke run under `artifacts/gui-packaging/windows-smoke-tests-2025-02-14.json`, covering MSIX and portable zip bundles on Windows 10/11 VMs.
- **Windows 10 Pro 22H2 (19045.4046)** — MSIX deploys via `Add-AppxPackage` once the signing certificate is trusted; launch succeeds offline when WebView2 Evergreen `124.0.2478.97` and .NET 8.0 Desktop Runtime are pre-staged.
- **Windows 11 Pro 23H2 (22631.3007)** — Portable zip bundle executes without elevation; WebView2 runtime detected from the side-by-side folder; cleanup script removes registry traces post-run.
- Both scenarios exercise offline diff replay/import flows, confirming packaging prerequisites documented in `docs/windows-gui-notes.md#packaging-quickstart` are sufficient.

### Framework decision matrix (A19.1)

| Framework | Strengths | Gaps | Packaging & runtime | Licensing & NOTICE impact | Assessment |
| --- | --- | --- | --- | --- | --- |
| **WinUI 3 / Windows App SDK** | Fluent visuals, native controls, built-in WebView2 for HTML diff rendering, tight Windows integration. | Requires MSIX tooling, Windows 10 1809+, Visual Studio workflow heavier than .NET-only projects. | Expects MSIX (required for full feature set) with optional self-contained `.NET` publish for offline runs. Needs WebView2 Evergreen redistributable staged alongside installer. | MIT SDK; NOTICE must call out Windows App SDK, WinUI libraries, WebView2 runtime package. Ship Microsoft redistribution notice with installer. | **Preferred path.** Matches existing Avalonia parity goals and keeps HTML reporting viable once WebView2 ships. |
| **Tkinter** | Bundled with CPython, zero external runtime, quick scripting for maintenance tools. | Lacks modern UI widgets, no first-party WebView; would need third-party bridge for HTML reports. | Portable zip easiest (ship CPython + scripts). Windows packaging limited; MSIX viable but offers little beyond Tkinter basics. | PSF licence; include Python redistribution notice and Tcl/Tk copyright in NOTICE bundle. | Viable fallback for ultra-light tooling but fails HTML diff requirement without extra work. |
| **PySimpleGUI (Tk flavour)** | Higher-level layout layer, rapid iteration on Tkinter base, built-in dialog helpers. | Inherits Tkinter limitations, LGPLv3 obligations if modified, ecosystem smaller for advanced widgets. | Mirrors Tkinter packaging; distribute wheels + Python runtime. Must document source access if we ship modified PySimpleGUI. | LGPLv3; NOTICE must link to project source and provide written offer when distributing binaries. | Adds developer ergonomics but brings extra compliance steps with limited UX gains. |
| **Electron** | Chromium engine enables rich HTML/JS experiences, easy HTML report embedding, cross-platform base. | Heavy footprint (~100 MB), requires Node.js toolchain, more security hardening for offline usage. | Requires MSIX or Squirrel-style installer; offline deployment needs pre-bundled Node modules and signed artefacts. Hash + integrity logs mandatory. | MIT core but npm dependencies vary; NOTICE must enumerate bundled packages and licences. | Reserved for future HTML-first experiences; overhead too high for current Windows-first deliverable. |

- Packaging baseline: WinUI/Electron favour MSIX; Tkinter/PySimpleGUI lean on portable zip bundles with embedded CPython.
- Manual update cadence stays in place until signing/auto-update decisions land.
- Recommendation: focus on WinUI 3 for production packaging, keep Tkinter/PySimpleGUI research archived as lightweight fallback notes, and track Electron as a contingency once reporting adapters demand full Chromium rendering.
- Structured summary captured in `artifacts/gui-packaging/framework-evaluation-2025-10-25.json` consolidates the matrix, rationale, and evidence links so A19.1 can reference machine-readable proof alongside the narrative notes.

### Drilldown command telemetry (A1b)

- `ShowDrilldownForHostCommand` now blocks deterministically when scans are running, a host is disabled, or no drilldown payload exists.
- Readiness transitions are written to `artifacts/logs/drilldown-ready.json` via the new file-backed `ILogger` implementation, capturing stages like `results-applied`, `drilldown-opened`, and denial reasons (`busy`, `host-disabled`, `no-drilldown`).
- Status banner strings mirror the telemetry reasons so headless fixtures and manual operators can correlate UI state with log entries without replaying runs.

### Session cache migration awaitables (A1c)

- The session cache loader now awaits the legacy migration task so concurrent `LoadAsync` and `SaveAsync` calls queue behind a single copy instead of racing the filesystem.
- Test coverage drives concurrent load/save flows plus a forced failure path, and the in-memory fake exposes `SimulateConcurrentUpgrade()` to coordinate multi-threaded upgrades during view-model exercises.
- Migration counters surface via `SessionCacheMigrationCounters` (`Successes`/`Failures`) to give deterministic signals during stress runs.
- Sample output captured after a passing migration sweep:

  ```text
  session-cache-migration:
    success-count: 1
    failure-count: 0
  ```

### Toast dispatcher buffering (A3.3)

- Synthetic dispatcher harness burst-testing 500 toast notifications recorded **22.34 ms** flush time across **500 UI posts** with the legacy dispatcher (`dotnet run` harness mirroring the pre-queue implementation).
- The buffered queue drops the same scenario to **9.55 ms** across **2 UI posts**, keeping auto-dismiss scheduling intact while collapsing redundant dispatcher work.
- Avalonia 11.2 Release rerun (`dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release`) confirms the `PerformanceSmokeTests.ToastService_batches_dispatcher_work_during_burst` suite now drains the burst in a single dispatcher dispatch. Evidence archived alongside the regression trace under `artifacts/logs/results-catalog/gui-tests-toasts-sorting-{regression,passing}.log`.

### Performance smoke harness (A3.4)

- `scripts/verify_coverage.sh --perf-smoke` now toggles the targeted perf suite after the baseline coverage sweep, persisting console output to `artifacts/perf/perf-smoke-<UTC>.log` for archival.
- Perf filter defaults to `Category=PerfSmoke`; pass `--perf-filter "Category=PerfSmoke&FullyQualifiedName~Performance"` when scoping to new virtualisation stories.
- `PerformanceProfile` reads `DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD` (default **400**) and `DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION` overrides so headless fixtures and the GUI can align on the same heuristics.
- `PerformanceSmokeTests` exercises the environment overrides plus the toast queue burst, asserting dispatcher posts collapse to two passes (show + dismiss) for 200 synthetic notifications.

### Responsive spacing matrix (A4.2.4)

| Breakpoint | Layout header padding | Content margin | Primary card padding | Toast width / padding | Server stack spacing / outer margin | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| `< 1280` | `20x16` (`Layout.HeaderPadding`) | `20` (`Layout.ContentMargin`) | `24` (`Layout.PrimaryCardPadding`) | `320` / `14` (`Toast.Width`, `Toast.Padding`) | `18` / `24` (`ServerSelection.StackSpacing`, `ServerSelection.OuterMargin`) | Baseline tokens mirror previous static spacing; values now flow through `ResponsiveSpacingProfiles` for deterministic bindings. |
| `≥ 1280` | `24x20` | `24` | `28` | `360` / `16` | `20` / `28` | Toast host margin expands to `0,28,28,0` so overflow drawers stay readable on 1440p monitors. |
| `≥ 1600` | `28x22` | `28` | `32` | `400` / `18` | `22` / `32` | Server cards add `0,0,0,24` trailing margin, preventing toggle clusters from crowding the right rail. |
| `≥ 1920` | `32x24` | `32` | `36` | `440` / `20` | `24` / `36` | Ultra-wide spacing caps `Toast.StackSpacing` at `12` to keep vertical rhythm aligned with 4K design mocks. |

### Performance diagnostics baseline (A3.1)

- `python scripts/perf_diagnostics.py` executes the perf-smoke subset and exports `artifacts/perf/baseline.json`, capturing the TRX snippet plus virtualization projections for default and forced overrides. The 2025-10-24 run stamped **13:01:12Z** against commit `6835c6a1d41351f4b5f8130eb4b11525ccb45800` with an exit code of zero.
- Perf smoke timings stayed tiny: environment override check completed in **1.9 ms** while the toast burst harness drained in **49.5 ms**, confirming the dispatcher queue still collapses 200 notifications into a single UI post followed by the dismissal sweep.
- Fixture census shows the bundled multi-server sample spans **10 hosts / 37 config files** (max **5** per host), so virtualization will only trigger on synthetic or production payloads. The projection table in the baseline covers counts from 2→1600 entries, with the default **400** threshold flipping to `true` at 400+.
- Forced overrides remain deterministic: the baseline stores precomputed matrices for `force=true` (always virtualise) and `force=false` (never virtualise), making it trivial to compare operator overrides against the default heuristics before shipping changes.

### Results catalog sorting migration (A5.1.4)

- **Before (Avalonia 11.1 shim):** Sorting glyphs were driven by a manual `DataGridColumn.Sort(ListSortDirection)` call, so the UI desynchronised whenever the view-model reapplied filters. Screenshot log (`artifacts/logs/results-catalog/sorting-pre-11-2.txt`) captured mismatched glyphs after toggling between `Updated` and `Coverage` columns.
- **After (Avalonia 11.2 APIs):** `ResultsCatalogViewModel` now mirrors `DataGridCollectionView.SortDescriptions`, allowing the grid to toggle glyphs via Avalonia’s built-in pipeline while the view-model retains authoritative ordering. `artifacts/logs/results-catalog/sorting-11-2.txt` stores the console trace from the new regression test that switches between `Config` ascending and `Drift` descending, confirming glyph + payload stay aligned.
- Operator takeaway: when a persisted session rehydrates, the default `Updated desc` descriptor hydrates both the collection view and glyphs without user clicks, eliminating the stale sort indicator issue noted in the 2025-10-12 regression triage.

### Headless font issues (A1d)

- Pre-fix failures hit both the Release `MainWindow` smoke test and the drilldown view instantiation path because Avalonia attempted to resolve `fonts:SystemFonts` before any headless locator bindings existed. The captured stack trace remains in [`artifacts/logs/headless-font-release-stacktrace.txt`](../../artifacts/logs/headless-font-release-stacktrace.txt) for release window crashes, while the matching drilldown failure reproduced under `[Collection(HeadlessCollection.Name)]` until the bootstrap landed.
- The remediation injects `FontManagerOptions` plus an `IFontManagerImpl` proxy during `BuildAvaloniaApp()` via the service locator hooks. The proxy keeps glyph loads synchronous, forwards `GetGlyphTypeface` to the real manager, and seeds `Inter` as the default family so existing glyph aliases keep working.
- After the binding, `HeadlessFixture` now asserts the `fonts:SystemFonts` dictionary and `Inter` fallback before any window construction, and `HeadlessBootstrapperSmokeTests` covers both `EnsureHeadless_registers_inter_font_manager` and `EnsureHeadless_allows_main_window_instantiation` to demonstrate the before/after success criteria. Passing runs are recorded alongside the seed log in [`artifacts/logs/headless-font-seed.txt`](../../artifacts/logs/headless-font-seed.txt).

### Multi-server guardrails (A1d)

- `SessionCacheService` now writes snapshots to `%LOCALAPPDATA%/DriftBuster/sessions/multi-server.json` (or the platform data root) while migrating any legacy cache in the background, so the awaitable save/load flow no longer races during shutdown or restores. The cache schema captures catalog filters, timeline filter, active view, and host metadata for deterministic reloads.
- `ServerSelectionViewModel` reapplies saved host enablement, roots, and view state on launch, then logs a **Loaded saved session** activity entry (`Restored {n} servers.`) to the timeline so operators can audit when a persisted configuration is replayed.
- `App.EnsureFontResources` seeds the `fonts:SystemFonts` alias dictionary with Inter before the view tree spins up, ensuring the restored multi-server tab renders catalog headers and guidance text consistently in Release and Debug headless runs.
- Persistence walkthrough published in `docs/multi-server-demo.md` and `docs/windows-gui-guide.md` now directs operators through saving a session, validating restored hosts, and confirming the Inter preload guardrail stays intact after relaunches.

### Diff planner MRU persistence (A2.1)

- MRU entries persist to `%LOCALAPPDATA%/DriftBuster/cache/diff-planner/mru.json` (or the overridable data root resolved by `DriftbusterPaths.GetCacheDirectory("diff-planner")`). The schema is versioned (`schema_version: 2`) with a bounded `max_entries` field (default 10) and an `entries` array.
- Each entry stores `baseline_path`, an ordered `comparison_paths` list, optional `display_name`, `last_used_utc` (UTC timestamp), `payload_kind` (`unknown|sanitized|raw`), and `sanitized_digest` for future diff payload audits. Paths are trimmed and deduplicated case-insensitively before persistence.
- `DiffPlannerMruStore.RecordAsync` promotes the most recent plan to the front while coalescing equivalent baselines/comparisons ignoring case, trimming the list to the configured limit, and rejecting empty/invalid payloads.
- Legacy `diff-planner.json` snapshots migrate in-place: valid legacy entries are normalised into the new schema, the entry cap is clamped to ≤10, and the converted file materialises at `cache/diff-planner/mru.json` without mutating the source.

#### 2025-10-28 MRU validation sweep (A2.4)

- `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj` (Debug) — ✅ `DiffPlannerMruStoreTests` confirms sanitized persistence and rejection of raw payloads; UI suites replay MRU dropdown selections without regressions.
- `coverage run --source=src/driftbuster -m pytest -q` — ✅ full Python suite keeps sanitized diff payload contract intact; coverage report shows `TOTAL 95%`.
- Manual GUI checklists executed against sanitized fixtures:
  - Loaded sanitized plan pair (`artifacts/samples/diff-planner/sanitized_summary.json`) and confirmed **Sanitized summary** banner renders while MRU entry appears in dropdown with digests only.
  - Triggered **Manage saved plans…** to verify cache directory path and manual purge workflow, ensuring entries delete cleanly without leaving raw payloads on disk.
  - Captured telemetry sample via debug log export, stored under `artifacts/logs/diff-planner-mru-telemetry.json`.
- Artifacts archived in `artifacts/diff-planner-validation/README.md` with command outputs, sanitized screenshot checklist results, and telemetry pointers for legal review follow-up.

#### Multi-server validation rollup (A1d)

- 2025-10-23 Release rerun still fails: see [`artifacts/logs/gui-validation/gui-tests-release-2025-10-23-regression.txt`](../../artifacts/logs/gui-validation/gui-tests-release-2025-10-23-regression.txt) for the glyph alias + session cache migration misses. Failures surfaced before any window construction, keeping the regression evidence adjacent to the earlier failing log.
- New `HeadlessFontBootstrapperDiagnostics` snapshot captures the alias probes and resource counts on every headless bootstrap. The latest run shows `fonts:SystemFonts` entries present but glyph creation failing, exporting details through `HeadlessFontBootstrapperDiagnostics.GetSnapshot()` so telemetry and smoke tests can ingest the state without rehydrating Avalonia types.
- 2025-10-26 Release rerun is still red. [`artifacts/logs/gui-validation/gui-tests-release-2025-10-26-regression.txt`](../../artifacts/logs/gui-validation/gui-tests-release-2025-10-26-regression.txt) captures the `fonts:SystemFonts` dictionary miss plus the lingering `BuildMultiServerRequest_uses_data_root_cache_and_migrates_legacy_files` assertion gap. The refreshed `artifacts/logs/headless-font-health.json` snapshot shows nine total attempts with seven failures for the Inter alias probe, confirming the bootstrapper guard rails still are not wiring the dictionary under Release.
- 2025-10-26 Debug rerun mirrors the Release failures (see [`artifacts/logs/gui-validation/gui-tests-debug-2025-10-26-run.txt`](../../artifacts/logs/gui-validation/gui-tests-debug-2025-10-26-run.txt)), so the alias bootstrapper work needs to land before we can check the stability box for Debug as well.
- Running `python -m scripts.coverage_report` after the GUI runs flags "Cobertura XML not found" because the failing .NET suites are not emitting coverage output yet; regenerate the Coverage XML once the headless glyph fixes allow green Release/Debug sweeps.
- Latest release rerun (`dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release`) passes now that `Program.EnsureHeadless` rebinds the headless font proxy even when an `App` instance already exists. The smoke tests confirm `fonts:SystemFonts` and `fonts:SystemFonts#Inter` resolve during reinitialisation, unblocking the coverage sweep follow-ups.

#### 2025-10-30 coverage enforcement sweep (A6.1.2)

- `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj` (Debug) — ✅ `coverlet.collector` reported 155 passing tests without tripping the 90 % line-coverage gate. Console transcript stored at `artifacts/logs/gui-validation/gui-tests-coverage-2025-10-30.txt` for the release evidence bundle.

#### 2025-10-25 compliance coverage snapshot (A12.4)

- Regenerated coverage via `bash scripts/verify_coverage.sh`; Python suite hit 94.34 % with the compliance watch floor pinned at **90.27 %** (guarded by `scripts/coverage_watch.py`).
- Offline compliance regression tests now drive `src/driftbuster/offline_compliance.py` to **96 %** coverage, keeping packaging evidence checks within the ≥90 % gate.
- Appended a history row to `artifacts/coverage/history.csv` (2025-10-25T06:10:16Z) capturing Python 94.34 % and .NET 77.26 % Cobertura results for trend tracking.
- Updated `artifacts/coverage/final/coverage_summary.txt` with the refreshed metrics and recorded the compliance watch floor alongside the totals.

#### 2025-10-24 multi-server smoke storage sweep (A6.2.1)

- Ran `./scripts/smoke_multi_server_storage.sh` to exercise the packaged storage smoke covering cold/hot cache reuse.
- Cold run populated the temporary cache root (`/tmp/tmp.1n8G7P9V0T/cache/diffs` in this session), and the validation script confirmed `used_cache=true` on the follow-up hot pass.
- Captured the CLI output and noted the session staging path (`/tmp/tmp.1n8G7P9V0T/sessions`) so Release prep evidence can point back to the cached multi-server persistence trail.
- Logged this sweep under A6.2.1 to close the smoke prerequisite ahead of the manual walkthrough capture.

#### 2025-10-24 manual multi-server walkthrough capture (A6.2.2–A6.2.3)

- Ran `python scripts/manual_multi_server_walkthrough.py --tag release-evidence` to reproduce the manual session using the bundled fixtures.
- Cold/hot passes reused the cached diff planner entries under `/root/.driftbuster-walkthrough-tmp/multi-server/cache/diffs` and confirmed `used_cache=true` for all ten hosts.
- Captured the walkthrough evidence in `artifacts/manual-runs/2025-10-24-173801Z-release-evidence.md` with the JSON console transcript stored alongside it for audit replay.
- Diff planner digest highlights secret-aware masking (`has_secrets=True`) while preserving comparison metadata and diff digests, covering the audit trail for A6.2.2.

### Release validation checklist (A6 · 6.4.1)

| Gate | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Coverage ≥ 90 % (Python/.NET) | ✅ Complete | `artifacts/logs/gui-validation/gui-tests-coverage-2025-10-30.txt`, `artifacts/coverage/final/` | Coverage transcript stored alongside consolidated HTML/XML artefacts after the A6.1 sweep. |
| Packaged smoke storage run | ✅ Complete | Section “2025-10-24 multi-server smoke storage sweep (A6.2.1)” | Cold/hot cache behaviour captured in-session with staging paths logged for follow-up evidence bundles. |
| Manual multi-server walkthrough capture | ✅ Complete | `artifacts/manual-runs/2025-10-24-173801Z-release-evidence.md` | CLI walkthrough via `scripts/manual_multi_server_walkthrough.py` captures cold/hot runs and the diff planner digest with secret-aware masking evidence. |
| Docs & asset refresh | ⏳ Pending | — | Await final screenshot capture before updating `docs/ux-refresh.md`, GUI guide, release notes, and README references. |
| Release bundle assembly | ⏳ Pending | — | Collate CHANGELOG update, validation notes, and artefact manifest into `notes/releases/next.md` once the remaining gates land. |

### Fontmanager regression

- Captured the latest Release-mode failure showing `FontManagerImpl.TryCreateGlyphTypeface` rejecting the `fonts:SystemFonts#Inter` alias when Avalonia boots without the proxy fallbacks enabled. The trace is archived in [`artifacts/logs/fontmanager-regression.txt`](../../artifacts/logs/fontmanager-regression.txt) so we can diff future stack signatures.
- Regression reproduces while `HeadlessFontManagerProxy.TryMatchCharacter` normalises alias inputs; the inner exception documents the missing Inter fallback that now drives the deterministic guard rails.
- Proxy guard rails now seed `fonts:SystemFonts#Inter` alongside the Inter default, and reflection-based tests in [`gui/DriftBuster.Gui.Tests/Headless/HeadlessFontManagerProxyTests.cs`](../../gui/DriftBuster.Gui.Tests/Headless/HeadlessFontManagerProxyTests.cs) cover alias merging, glyph creation, and deterministic fallbacks.
- `HeadlessFixture` (`gui/DriftBuster.Gui.Tests/Ui/HeadlessFixture.cs`) exercises the proxied `IFontManagerImpl` directly so Release/Debug parity keeps `fonts:SystemFonts#Inter` resolving before any window construction.
- Next mitigation steps track under A0 once the Release-mode smoke test lands, keeping this section aligned with ongoing hardening.
- Bootstrapper smoke telemetry now persists to [`artifacts/logs/headless-font-health.json`](../../artifacts/logs/headless-font-health.json), capturing per-scenario totals (`totalRuns`, `passes`, `failures`) and the latest metrics. The current snapshot shows all three headless smoke tests green after the alias/lookup retries, making it easy to spot regressions when the counts drift.
- `scripts/font_health_summary.py` turns the telemetry into a quick drift report, failing the session when pass rates dip or the latest status regresses so the Release/Debug parity checks stay honest.

### Validation checkpoint (A4 · 4.4)

- Ran `python -m scripts.accessibility_summary` against `artifacts/gui-accessibility/narrator-inspect-run-2025-02-14.txt` to confirm every Narrator/Inspect section remained complete before logging new evidence.
- Contrast audit: computed WCAG ratios for the palette pairs using the theme tokens (Dark+ text 17.74:1, Dark+ accent 5.25:1, Light+ text 17.85:1, Light+ accent 4.95:1). Captured the figures in the accessibility report log for traceability.
- Regression sweep:
  - `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj` (150 tests, warnings limited to known drag/drop API migrations still pending).
  - `pytest` (405 passed, 1 skipped) to confirm core detectors and CLI flows stay green with the refreshed theme assets.
- Manual multi-server rehearsal: executed `PYTHONPATH=src scripts/smoke_multi_server_storage.sh` to validate cold/hot cache behaviour and note the session root for theme evidence capture. Extracted `Palette.DarkPlus`/`Palette.LightPlus` accent and background tokens via a quick XAML probe to document the Dark+/Light+ toggle deltas alongside the run log.
- Recorded tool/version outcomes plus the contrast ratios in `notes/checklists/accessibility-report.md` so future audits can diff against this checkpoint.

### Avalonia 11.2 Release rebuild (A5 · 5.3)

- Re-ran the Release publish targeting win-x64 via `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false /p:IncludeNativeLibrariesForSelfExtract=true`, confirming the Avalonia 11.2 upgrade compiles cleanly aside from the known drag/drop deprecation warnings that remain tracked for follow-up.
- Captured the publish transcript and SHA-256 checksum for the single-file output under `artifacts/builds/avalonia-11-2/` (`publish-release.log`, `publish-release.sha256`) so operators can verify hashes before sideloading builds.
- Stored a machine-readable manifest (`publish-release-manifest.json`) enumerating published files with sizes + hashes, keeping the evidence aligned with the retention expectations for the A5 build validation gate.

### Results catalog evidence manifest (A5 · 5.5)

- Added `artifacts/logs/results-catalog/evidence-manifest.json` to consolidate the regression-to-fix trail for the Avalonia 11.2 sorting work. The manifest links the archived failing log, the passing GUI test sweep, and the pre/post narrative snippets with deterministic SHA-256 digests so auditors can diff the material without recalculating hashes.
- Manifest `summary.notes` documents the intent behind the bundle (capturing the migration story, pairing logs with annotations, and recording the deterministic hashes) to make the evidence self-describing when reviewed outside this notebook.
- This closes 5.5 by tying the evidence bundle back to the `ResultsCatalogAlignment` area—operators can trace the outcome straight from the queue to the exact artefacts without re-reading the full change log.

### Offline SQL snapshot validation (A1d · 16.4)

- Seeded a minimal `accounts` SQLite database and executed `scripts.capture.run_sql_export` to validate the SQL snapshot export path with deterministic masking (`accounts.secret`) and hashing (`accounts.email`).
- Stored the generated evidence under [`artifacts/sql/validation-sql-snapshot.json`](../../artifacts/sql/validation-sql-snapshot.json) and [`artifacts/sql/sql-manifest.json`](../../artifacts/sql/sql-manifest.json), with matching SHA-256 records archived in [`artifacts/sql/validation-checksums.json`](../../artifacts/sql/validation-checksums.json) for downstream integrity checks.
- Retention plan: refresh the sanitized snapshot quarterly or whenever the SQL export schema shifts, keep only the most recent manifest/snapshot pair plus checksum log for 90 days, and drop superseded captures after confirming the new hashes so no historical data lingers outside controlled storage.

## Open Questions

- Which framework aligns best with the eventual reporting pipeline timeline?
- Do we pre-provision WebView2 Evergreen or rely on per-host install scripts?
- What accessibility tooling (Narrator, Inspect) should form the test baseline?

## Next Actions (Deferred)

- Prototype data loading adapters once reporting outputs stabilise.
- Track packaging readiness via `CLOUDTASKS.md` area A19.
- Draft NOTICE/licence bundle templates before distributing binaries.
- Plan VM matrix for manual verification (Windows 10/11, offline/online hosts).

## User Requirements

- Offline-ready packaging must ship alongside the first GUI preview so security teams can sideload builds without network access (see [Packaging & Distribution Plan](../../docs/windows-gui-notes.md#packaging--distribution-plan)).
- The GUI needs a lightweight viewer mode that reads generated HTML diffs without bundling new scanners, keeping CLI ownership intact (see [Data Flow & UX Outline](../../docs/windows-gui-notes.md#data-flow--ux-outline)).
- Accessibility pass requires Narrator and Inspect coverage before the HOLD lifts; track mitigation steps against the [Compliance & Accessibility Checklist](../../docs/windows-gui-notes.md#compliance--accessibility-checklist).
  - Matrix lives below and ties each scenario to tool configuration, success criteria, and evidence capture.
- Decision on WebView2 Evergreen redistribution is pending to finalise installer prerequisites (see [Candidate Frameworks](../../docs/windows-gui-notes.md#candidate-frameworks)).

### Accessibility Validation Matrix (A19.3)

| Tool | Scenario | Target Surface | Execution Notes | Evidence |
| --- | --- | --- | --- | --- |
| **Narrator** | Launch the packaged shell in default mode, tab through the server selection view. | Primary navigation frame, run profile list, CTA buttons. | Start Narrator (`Win + Ctrl + Enter`), ensure scan mode off, traverse using `Tab`/`Shift+Tab`, confirming focus order matches UI hierarchy. Log any unlabeled controls. | `artifacts/gui-accessibility/narrator-inspect-run-2025-02-14.txt` (Narrator section). |
| **Narrator** | Trigger drilldown panel after selecting a host with completed scans. | Drilldown summary headers, diff viewer launch button. | With Narrator running, activate drilldown using keyboard shortcuts only; verify Narrator announces new panel title and actionable controls. Capture transcript for announcements. | `artifacts/gui-accessibility/narrator-inspect-run-2025-02-14.txt` (Drilldown scenario). |
| **Inspect** | Validate automation properties for top-level window and critical controls. | Window title, start scan button, filter dropdowns. | Run Inspect (`inspect.exe`), attach to DriftBuster window, record `Name`, `AutomationId`, `ControlType`, and keyboard focus for each critical element. Flag missing `HelpText`. | `artifacts/gui-accessibility/narrator-inspect-run-2025-02-14.txt` (Inspect section). |
| **Inspect** | Verify contrast hints for high-contrast theme toggles. | Settings dialog theme toggle, diff viewer preview text. | Enable High Contrast in Windows Settings, restart app, re-run Inspect color contrast capture, confirm contrast ratio >= 4.5:1. Document steps for resetting theme. | `artifacts/gui-accessibility/narrator-inspect-run-2025-02-14.txt` (High contrast check). |

- Follow-up: integrate Screen Reader regression checks into the Release GUI smoke pipeline once headless instrumentation lands.
- Automation: run `python -m scripts.accessibility_summary` to validate transcripts include each matrix scenario and expected
  keywords before archiving new evidence.
