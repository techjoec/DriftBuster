# DriftBuster Windows GUI Guide

This guide explains the capabilities, layout, and operational details of the Avalonia-based Windows shell shipped in `gui/DriftBuster.Gui`.

## 1. Overview
- **Purpose:** Provide a Windows-first experience for building diff plans and running hunt scans powered by the new .NET backend.
- **Architecture:** Avalonia (net8.0) desktop app backed by the shared `DriftBuster.Backend` library used by the PowerShell module.
- **Audience:** Analysts validating configuration drift manually, as well as developers exercising the core engine without the CLI.

## 2. Prerequisites
| Dependency | Notes |
|------------|-------|
| .NET SDK 8.0.x | Required to restore, run, and publish the GUI. |
| DriftBuster repo checkout | Required for fixtures and sample data referenced by the UI. |
| Optional editor tooling | JetBrains Rider, VS Code + Avalonia extension, or equivalent for XAML previews. |

## 3. Launching the GUI
1. Ensure prerequisites are installed (`dotnet --list-sdks`, `python --version`).
2. Restore + build: `dotnet restore gui/DriftBuster.Gui/DriftBuster.Gui.csproj` then `dotnet build -c Debug gui/DriftBuster.Gui/DriftBuster.Gui.csproj`.
3. Run: `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`.
4. The “DrB DriftBuster” window opens with Diff view selected by default. The compact header presents the DrB badge, navigation summary, a live backend health dot with **Check core**, and a theme toggle (Dark/Light). A pillbox banner beneath the header shows the current view context and shortcuts.
5. Demo data is bundled with the app under a `Samples/` directory in the output folder.
   See `docs/DEMO.md` for a guided walkthrough using these files.
6. Registry scan results collected by the offline runner (JSON under `data/<alias>/registry_scan.json`)
   are displayed alongside file-based findings when present.

## 4. Layout Walkthrough
- **Header strip:** DrB badge + title stack, navigation toggle group (Diff / Hunt / Profiles / Multi-server), backend health indicator with **Check core**, theme toggle, and a **Ping core** shortcut. Status messages from the active view flow into the headline banner so you can see scan progress even while switching tabs. Contextual toast notifications appear in the top-right overlay; each toast exposes copy actions for quick sharing of error details.
- **Diff view:** Build diff plans from multiple snapshots. Primary action uses accent fill; secondary actions use outline style. Includes validation, plan/metadata cards, raw JSON expander, and copy control.
- **Hunt view:** Targets directories/files, runs the hunt pipeline, and displays results as cards with rule metadata, counts, and status messaging.
- **Multi-server view:** Configure up to six hosts, validate roots, and orchestrate runs. The refreshed layout moves host management, execution controls, and the activity audit feed into balanced columns, so you can compare hosts without scrolling. Toasts surface scan status (success, attention, failure) and each timeline card exposes copy shortcuts for rapid sharing.

## 5. Diff Planner Details
### Inputs & Validation
- Use the **Browse** buttons beside each textbox to pick the left/right file.
- Inline messages (in red) surface when a path is missing or invalid.
- The **Build Plan** button stays disabled while validation fails or a request is running.

### Execution & Results
- Clicking **Build Plan** triggers an async request to the backend.
- While running, a progress bar animates and the button remains disabled.
- On success:
  - **Plan cards** list before/after snapshots, content type, labels, masks, and context lines.
  - **Metadata cards** reflect resolved paths and diff settings.
  - **Raw JSON** expander contains the untouched backend payload.
  - **Copy raw JSON** copies the payload for downstream tooling or manual inspection.
- Errors (e.g., missing files, permission issues) bubble into the red message banner.

## 6. Hunt View Details
### Inputs
- **Target** accepts either a directory or single file. The Browse button opens a folder picker first, falling back to file selection.
- **Filter** (optional) applies a case-insensitive substring filter to result excerpts.
- Validation ensures the path exists before enabling the **Scan** button.

### Execution & Results
- A progress bar appears during scans; results persist until the next run.
- **Status banner** reports success (hit count) or “No matches found”.
- **Findings** are shown as cards that surface rule name/description, token badges, path, line number, and a trimmed excerpt.
- Hover tooltips provide full text when truncated.
- Raw JSON is available in the expander for exporting or analysis.

## 7. Multi-server View Details
### Host Configuration
- Host cards now render inside a responsive grid that fits 1280–1920 px widths without horizontal scrolling. Cards stretch to fill available space while keeping a minimum width, so you can scan labels, scopes, and roots side by side.
- Each card exposes a `ValidationSummary` tooltip and automation name that narrates the current root status (ready, pending validation, or needs attention) for screen readers.
- Keyboard users get high-contrast focus outlines on buttons, toggles, combo boxes, and root text boxes; the Run/Cancel strip also exposes access keys (`Alt+R`, `Alt+M`, `Alt+C`).
- Drag-and-drop host cards to reorder execution priority. The baseline preference and command ordering update in-place, and the new order is captured with the session snapshot.

### Root Validation & Persistence
- Switching a server to **Custom roots** keeps the validation summary live while you type. Duplicate or relative paths flag the card immediately, and summaries stay cached even when the card loses focus.
- Session saves now persist the active catalog sort descriptor, catalog filters, timeline filter, selected view (setup/results/drilldown), and root ordering so reloading a session restores the same working state.

### Catalog & Drilldown Enhancements
- Catalog columns support click-to-sort with visual indicators; the current sort mode is cached alongside server state.
- Drilldown headers now surface format, baseline host, drift count, and provenance in a metadata strip for quick triage.
- A dedicated **Copy JSON** action copies the sanitised export payload to the clipboard without triggering a file save, mirroring the HTML/JSON export options.
- The execution summary grid exposes a **View drilldown** shortcut per host so analysts can jump into the latest drift snapshot without leaving the setup surface.

### Notifications & Timeline
- Toast alerts now surface in a compact stack with at most three visible at once; additional messages collapse into an overflow tray so long-running scans don't flood the viewport.
- Timeline filters include **All**, **Errors**, **Warnings**, and **Exports**, and the chosen filter plus the last opened drilldown host persist with the rest of the multi-server session.
- Clipboard/export actions write to the timeline with the new **Exports** filter so analysts can isolate delivery events quickly.

## 8. Backend Bridge
- `DriftbusterService` instantiates the shared `DriftbusterBackend` class and executes diff, hunt, and run-profile operations in-process.
- Diff calls load file contents, build the same JSON payload exposed to the UI, and reuse the shared models for plan metadata.
- Hunt scans walk the filesystem locally, apply the default rule set, and surface filtered hits to the view models.
- Run profile actions persist JSON definitions, copy snapshot files, and emit metadata using the shared library helpers.
- Multi-server orchestration shells out to `python -m driftbuster.multi_server`, streams per-host progress back into toasts and the activity timeline, and persists cached diffs under the DriftBuster data root (e.g. `%LOCALAPPDATA%/DriftBuster/cache/diffs/`, `$XDG_DATA_HOME/DriftBuster/cache/diffs/`).
- All work runs asynchronously on background tasks so the UI stays responsive; errors surface through the existing status banners.

## 9. Packaging Options
- Default release builds produce an installer:
  - `python scripts/release_build.py --release-notes notes/releases/<semver>.md --installer-rid win-x64`
  - Installer artifacts: `artifacts/velopack/releases/<rid>`.
- Direct Velopack usage:
  - `dotnet tool restore`
  - `scripts/build_velopack_release.sh --version <semver> --release-notes notes/releases/<semver>.md [--rid win-x64]`
  - Use `--channel` (prereleases) and `--pack-id` (bundle id) as needed.
- Manual portable publish (for quick local runs):
  - `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true`
- Before packaging, sync versions: `python scripts/sync_versions.py`.

## 10. Manual Smoke Checklist
- Located at `notes/checklists/gui-smoke.md`.
- Covers ping, diff, hunt, error handling, and verifying backend shutdown after closing the window.
- Record date/operator each time the checklist is executed.

## 11. Automated & Headless Tests
- UI automation lives in `gui/DriftBuster.Gui.Tests/Ui` and complementary view-model suites under `gui/DriftBuster.Gui.Tests/ViewModels`. Headless UI tests are attributed with `[AvaloniaFact]`, ensuring each case runs on the Avalonia dispatcher (navigation, drilldown exports, hunt flows, converters, session cache, and theme toggles).
- `HeadlessFixture` calls `Program.EnsureHeadless(...)`, which guards against duplicate `AppBuilder.Setup` calls in repeated test execution.

### Headless bootstrap
  - `Program.BuildAvaloniaApp()` preloads the `fonts:SystemFonts` resource so the Avalonia headless pipeline always has a populated `ConcurrentDictionary<string, FontFamily>` including the alias entry consumed by `FontManager.SystemFonts`.
  - `Program.EnsureHeadless(...)` binds `HeadlessFontManagerProxy` through `HeadlessFontBootstrapper` so Avalonia's `IFontManagerImpl` always resolves Inter with synchronous glyph loading. The proxy normalises aliases such as `fonts:SystemFonts`, extends the installed family list, and keeps glyph requests deterministic for smoke tests. Call sites worth preserving:
    - `App.EnsureFontResources` seeds the alias dictionary consumed by Avalonia controls via `FontManager.SystemFonts`.
    - `HeadlessFixture` verifies `FontManager.Current` exposes Inter as the default family and resolves glyphs for both Inter and the alias, protecting glyph paths used by text controls and dialogs.
    - `HeadlessBootstrapperSmokeTests` assert the locator binding and glyph creation path so regressions in the proxy surface before full suites run.
  - Keep this preload intact when editing the bootstrapper so fixtures never hit the `KeyNotFoundException` observed before the guardrails landed.

### FontManager regression playbook
- **Purpose:** Confirm `fonts:SystemFonts` and `fonts:SystemFonts#Inter` stay resolvable in both Release and Debug builds so headless smoke tests mirror production.
- **Targeted tests:**
  1. `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release --filter FullyQualifiedName~HeadlessBootstrapperSmokeTests.EnsureHeadless_release_mode_exposes_inter_alias_through_system_fonts`
  2. `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter FullyQualifiedName~HeadlessFixture`
- **Expected signals:** The smoke test asserts the `fonts:SystemFonts` resource exposes the Inter alias keys consumed by `FontManager.SystemFonts`, and `FontManager.Current.TryGetGlyphTypeface("fonts:SystemFonts#Inter")` succeeds; the fixture cross-checks `FontManagerOptions.DefaultFamilyName` and fallback ordering against `FontManager.Current`.
- **Failure triage:** If either command fails, inspect `artifacts/logs/fontmanager-regression.txt` for the captured stack trace and rerun `Program.EnsureHeadless` instrumentation to verify the proxy registered its aliases.
- Run targeted suites via tmux: `tmux new -d -s codexcli-ui 'cd /github/repos/DriftBuster && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter "FullyQualifiedName~DiffViewTests"'`.
- Full coverage expectations:
  - Debug: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total`
  - Release: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total`
  - XAML compilation gate: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -p:EnableAvaloniaXamlCompilation=true`
- Diagnostic harness: set `AVALONIA_INSPECT=1` and run `--filter FullyQualifiedName=DriftBuster.Gui.Tests.Ui.AvaloniaSetupInspection.LogSetupState` to capture resource/style registration in `artifacts/codexcli-inspect.log`.

## 12. Troubleshooting
| Symptom | Suggested Checks | 
|---------|-----------------| 
| “Backend closed unexpectedly” | Run the GUI from a console and inspect logs; verify the selected files are accessible. |
| Validation won’t clear | Confirm file/directory exists and is accessible; refresh the path using Browse. |
| Empty hunt results | Check filter string, increase rule coverage, or drop filter to view raw hits. |
| Clipboard not working | Ensure the app is running in a desktop session (clipboard APIs require a real user session). Use the activity timeline’s copy buttons to verify clipboard access quickly. |

## 13. Extensibility Pointers
- Extend `Driftbuster.Backend` with new helpers, then wire them into both the GUI service and PowerShell module.
- New view models should expose observable collections + status fields similar to Diff/Hunt pattern.

For deeper implementation notes, refer to `docs/windows-gui-notes.md` (engineering focus) and `notes/dev-host-prep.md` (host setup log).

## 14. PowerShell module
- Imports live under `cli/DriftBuster.PowerShell`. Use the module when running backend commands from Windows shells without the GUI.
- The module loads `DriftBuster.Backend.dll` through the shared cache directory resolved by `DriftbusterPaths.GetCacheDirectory`.
- Initialise the module with the following sequence to guarantee a published backend and JSON-aligned outputs:
  1. Publish the backend once per build: `dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Debug -o gui/DriftBuster.Backend/bin/Debug/published`.
  2. Import the module: `pwsh -NoLogo -NoProfile -Command "Import-Module ./cli/DriftBuster.PowerShell/DriftBuster.psm1 -Force"`.
  3. Verify connectivity and schema: `Test-DriftBusterPing` (returns `{ status = "pong" }`), `Invoke-DriftBusterDiff -Left baseline.json -Right release.json`, and `Invoke-DriftBusterRunProfile -Profile <profile.json> -BaseDir . -NoSave -Raw`.
  4. When validating packaging, run `pwsh ./cli/DriftBuster.PowerShell.Tests/Invoke-ModuleTests.ps1` to execute the Pester suite and capture an NUnit XML report under `artifacts/powershell/tests/`.

### Troubleshooting
| Symptom | Suggested Checks |
|---------|-----------------|
| Import fails with `DriftBusterBackendMissing`. | Publish the backend: `dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Debug -o gui/DriftBuster.Backend/bin/Debug/published`, then re-import the module or copy the resulting `DriftBuster.Backend.dll` next to `DriftBuster.psm1`. Confirm the cache directory contains the DLL afterwards. |
