# Windows GUI Research Status

## Decisions & Findings

- Framework shortlist: WinUI 3, Tkinter, PySimpleGUI, Electron. Electron reserved for rich HTML workflows; Tkinter/PySimpleGUI for lightweight builds.
- Packaging baseline: MSIX for WinUI/Electron; portable zip with bundled Python runtime for Tkinter/PySimpleGUI.
- Manual update cadence until signing/auto-update story is approved.

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
- Decision on WebView2 Evergreen redistribution is pending to finalise installer prerequisites (see [Candidate Frameworks](../../docs/windows-gui-notes.md#candidate-frameworks)).
