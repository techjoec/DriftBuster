# Windows GUI Research Status

## Decisions & Findings

### Packaging readiness summary (A19.6)

- Portable and self-contained bundles both capture install/uninstall transcripts under `artifacts/gui-packaging/` with matching SHA256 manifests for offline validation.
- WebView2 Evergreen runtime `124.0.2478.97` ships alongside every bundle; version and hash values logged in `notes/checklists/legal-review.md` for auditing.
- Signing certificate (`thumbprint ab12 cd34 ef56 7890`, expiry 2026-03-01) validated on Windows 10/11 VMs prior to MSIX installation, with certificate chain artefacts stored in `artifacts/gui-packaging/certificates/`.
- Operators must import the signing certificate into `Trusted People` on test VMs and verify hashes before launching bundles; troubleshooting guidance now lives in `docs/windows-gui-guide.md#12-troubleshooting`.

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

### Headless font issues (A1d)

- Pre-fix failures hit both the Release `MainWindow` smoke test and the drilldown view instantiation path because Avalonia attempted to resolve `fonts:SystemFonts` before any headless locator bindings existed. The captured stack trace remains in [`artifacts/logs/headless-font-release-stacktrace.txt`](../../artifacts/logs/headless-font-release-stacktrace.txt) for release window crashes, while the matching drilldown failure reproduced under `[Collection(HeadlessCollection.Name)]` until the bootstrap landed.
- The remediation injects `FontManagerOptions` plus an `IFontManagerImpl` proxy during `BuildAvaloniaApp()` via the service locator hooks. The proxy keeps glyph loads synchronous, forwards `GetGlyphTypeface` to the real manager, and seeds `Inter` as the default family so existing glyph aliases keep working.
- After the binding, `HeadlessFixture` now asserts the `fonts:SystemFonts` dictionary and `Inter` fallback before any window construction, and `HeadlessBootstrapperSmokeTests` covers both `EnsureHeadless_registers_inter_font_manager` and `EnsureHeadless_allows_main_window_instantiation` to demonstrate the before/after success criteria. Passing runs are recorded alongside the seed log in [`artifacts/logs/headless-font-seed.txt`](../../artifacts/logs/headless-font-seed.txt).

### Multi-server guardrails (A1d)

- `SessionCacheService` now writes snapshots to `%LOCALAPPDATA%/DriftBuster/sessions/multi-server.json` (or the platform data root) while migrating any legacy cache in the background, so the awaitable save/load flow no longer races during shutdown or restores. The cache schema captures catalog filters, timeline filter, active view, and host metadata for deterministic reloads.
- `ServerSelectionViewModel` reapplies saved host enablement, roots, and view state on launch, then logs a **Loaded saved session** activity entry (`Restored {n} servers.`) to the timeline so operators can audit when a persisted configuration is replayed.
- `App.EnsureFontResources` seeds the `fonts:SystemFonts` alias dictionary with Inter before the view tree spins up, ensuring the restored multi-server tab renders catalog headers and guidance text consistently in Release and Debug headless runs.
- Persistence walkthrough published in `docs/multi-server-demo.md` and `docs/windows-gui-guide.md` now directs operators through saving a session, validating restored hosts, and confirming the Inter preload guardrail stays intact after relaunches.

### Fontmanager regression

- Captured the latest Release-mode failure showing `FontManagerImpl.TryCreateGlyphTypeface` rejecting the `fonts:SystemFonts#Inter` alias when Avalonia boots without the proxy fallbacks enabled. The trace is archived in [`artifacts/logs/fontmanager-regression.txt`](../../artifacts/logs/fontmanager-regression.txt) so we can diff future stack signatures.
- Regression reproduces while `HeadlessFontManagerProxy.TryMatchCharacter` normalises alias inputs; the inner exception documents the missing Inter fallback that now drives the deterministic guard rails.
- Proxy guard rails now seed `fonts:SystemFonts#Inter` alongside the Inter default, and reflection-based tests in [`gui/DriftBuster.Gui.Tests/Headless/HeadlessFontManagerProxyTests.cs`](../../gui/DriftBuster.Gui.Tests/Headless/HeadlessFontManagerProxyTests.cs) cover alias merging, glyph creation, and deterministic fallbacks.
- `HeadlessFixture` (`gui/DriftBuster.Gui.Tests/Ui/HeadlessFixture.cs`) exercises the proxied `IFontManagerImpl` directly so Release/Debug parity keeps `fonts:SystemFonts#Inter` resolving before any window construction.
- Next mitigation steps track under A0 once the Release-mode smoke test lands, keeping this section aligned with ongoing hardening.
- Bootstrapper smoke telemetry now persists to [`artifacts/logs/headless-font-health.json`](../../artifacts/logs/headless-font-health.json), capturing per-scenario totals (`totalRuns`, `passes`, `failures`) and the latest metrics. The current snapshot shows all three headless smoke tests green after the alias/lookup retries, making it easy to spot regressions when the counts drift.
- `scripts/font_health_summary.py` turns the telemetry into a quick drift report, failing the session when pass rates dip or the latest status regresses so the Release/Debug parity checks stay honest.

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
