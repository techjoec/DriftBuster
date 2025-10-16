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
4. A red/black “DrB DriftBuster” window appears with Diff view selected by default.

## 4. Layout Walkthrough
- **Header strip:** DrB badge, title, Diff/Hunt navigation buttons, and a “Ping Core” shortcut.
- **Diff view:** Focused on building diff plans from two files. Includes file pickers, validation, results table, raw JSON expander, and a copy-to-clipboard control.
- **Hunt view:** Targets directories/files, runs the hunt pipeline, and displays results in a sortable grid with rule metadata, counts, and status messaging.

## 5. Diff Planner Details
### Inputs & Validation
- Use the **Browse** buttons beside each textbox to pick the left/right file.
- Inline messages (in red) surface when a path is missing or invalid.
- The **Build Plan** button stays disabled while validation fails or a request is running.

### Execution & Results
- Clicking **Build Plan** triggers an async request to the backend.
- While running, a progress bar animates and the button remains disabled.
- On success:
  - **Plan table** lists before/after snapshots, content type, labels, masks, and context lines.
  - **Metadata table** reflects resolved paths and diff settings.
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
- **Hits table** columns:
  - Rule name & description
  - Token (if supplied by the rule; `—` otherwise)
  - Relative path and full path
  - Line number and trimmed excerpt
- Rows are sortable by clicking column headers; hover tooltips provide full text when truncated.
- Raw JSON is available in the expander for exporting or analysis.

## 7. Backend Bridge
- `DriftbusterService` instantiates the shared `DriftbusterBackend` class and executes diff, hunt, and run-profile operations in-process.
- Diff calls load file contents, build the same JSON payload exposed to the UI, and reuse the shared models for plan metadata.
- Hunt scans walk the filesystem locally, apply the default rule set, and surface filtered hits to the view models.
- Run profile actions persist JSON definitions, copy snapshot files, and emit metadata using the shared library helpers.
- All work runs asynchronously on background tasks so the UI stays responsive; errors surface through the existing status banners.

## 8. Packaging Options
- **Velopack installers:** `dotnet tool restore` then `scripts/build_velopack_release.sh --version <semver> --release-notes notes/releases/<semver>.md [--rid win-x64]`. The script publishes the app self-contained and calls `vpk pack`, dropping installers under `artifacts/velopack/releases/<rid>`. Release notes must follow `docs/release-notes.md`. Run it on the OS you’re targeting (the script switches between `[win]`, `[linux]`, `[osx]` directives automatically). Use `--channel` to label prerelease feeds and `--pack-id` if you need an alternate bundle identifier.
- **Manual portable publish:** For quick disposable builds, `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true`. (Velopack is the preferred path for anything shipping to users.)
- No external runtimes are required beyond the .NET runtime chosen for your publish target.
- Cross-check `versions.json` (`core`, `gui`) before packaging and run `python scripts/sync_versions.py` to propagate changes prior to publishing.

## 9. Manual Smoke Checklist
- Located at `notes/checklists/gui-smoke.md`.
- Covers ping, diff, hunt, error handling, and verifying backend shutdown after closing the window.
- Record date/operator each time the checklist is executed.

## 10. Automated & Headless Tests
- UI automation lives in `gui/DriftBuster.Gui.Tests/Ui`. Each class is tagged with `[Collection(HeadlessCollection.Name)]` so all headless runs share a single Avalonia instance.
- `HeadlessFixture` calls `Program.EnsureHeadless(...)`, which guards against duplicate `AppBuilder.Setup` calls in repeated test execution.
- Run targeted suites via tmux: `tmux new -d -s codexcli-ui 'cd /github/repos/DriftBuster && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter "FullyQualifiedName~DiffViewTests"'`.
- Full coverage expectations:
  - Debug: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj`
  - Release: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release`
  - XAML compilation gate: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -p:EnableAvaloniaXamlCompilation=true`
- Diagnostic harness: set `AVALONIA_INSPECT=1` and run `--filter FullyQualifiedName=DriftBuster.Gui.Tests.Ui.AvaloniaSetupInspection.LogSetupState` to capture resource/style registration in `artifacts/codexcli-inspect.log`.

## 11. Troubleshooting
| Symptom | Suggested Checks | 
|---------|-----------------| 
| “Backend closed unexpectedly” | Run the GUI from a console and inspect logs; verify the selected files are accessible. |
| Validation won’t clear | Confirm file/directory exists and is accessible; refresh the path using Browse. |
| Empty hunt results | Check filter string, increase rule coverage, or drop filter to view raw hits. |
| Clipboard not working | Ensure the app is running in a desktop session (clipboard APIs require a real user session). |

## 12. Extensibility Pointers
- Extend `Driftbuster.Backend` with new helpers, then wire them into both the GUI service and PowerShell module.
- New view models should expose observable collections + status fields similar to Diff/Hunt pattern.

For deeper implementation notes, refer to `docs/windows-gui-notes.md` (engineering focus) and `notes/dev-host-prep.md` (host setup log).
