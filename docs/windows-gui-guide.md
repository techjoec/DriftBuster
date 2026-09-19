# DriftBuster Windows GUI Guide

How the Avalonia desktop app in `gui/DriftBuster.Gui` is laid out and what each page does.

## 1. Overview
- **Purpose:** show, for people who do not read diffs, which settings differ between servers or files and what each one has; then let them quiet the noise so the next run shows only what matters.
- **Architecture:** Avalonia (net10.0) desktop app over the shared `DriftBuster.Backend` library, which the console tool and PowerShell module use too. Everything runs in process.
- **Look and feel:** every page fills the window. A list or grid sits beside the selected item's details, each pane scrolls on its own, and right-click menus carry the actions. Colours come from theme tokens, so Dark+ and Light+ both work.

## 2. Prerequisites
| Dependency | Notes |
|------------|-------|
| .NET SDK 10.0.x | Required to build, run, and publish the GUI from source. Released builds are self-contained and need nothing installed. |
| DriftBuster repo checkout | Required for fixtures and sample data when running from source. |
| Optional editor tooling | JetBrains Rider, VS Code + Avalonia extension, or equivalent for XAML previews. |

## 3. Launching the GUI
1. Ensure the SDK is installed (`dotnet --list-sdks`).
2. Build: `dotnet build -c Debug gui/DriftBuster.Gui/DriftBuster.Gui.csproj`.
3. Run: `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`, or run `DriftBuster.Gui.exe` from a release zip.
4. The window opens on **Multi-server**. The top bar holds the tabs (Multi-server, Diff planner, Hunt explorer, Profiles), a core status dot with **Check core**, and the **Theme** selector (Dark+ / Light+).

## 4. Multi-server
The toggles at the top of the page switch between **Setup**, **Compare**, **Files** and **File details**; **Run all** scans every included host. After a run the page lands on Compare.

### Setup
- The host list on the left shows every host: tick to include it in runs, drag to change the order, and read its scope in one line ("Custom roots: C:\\apps (+1 more)") and its state in a word (Ready, Scanning, Done, Cached, Failed, Off).
- The selected host's settings sit on the right: label, scope (all drives, single drive, custom roots), roots with add and remove, **Retry** after a failure, and the last run time.
- **Remember session** keeps hosts, roots and view state in `sessions/multi-server.json` under the data root; **Save**, **Add host** and **Clear** sit beside it.
- **Run status and activity** (collapsed at the bottom) lists each host's run state with a **File details** link, and the activity timeline with **All / Errors / Warnings / Exports** filters and copy buttons. Toasts report progress, warnings and failures.

### Compare
- One chip per server says in plain words what differs ("prod: 6 settings differ in 2 files, 1 file missing"); click a chip to show only what differs on that server.
- The file list shows every file with the number of differing settings or why it differs (missing, extra, unreadable). The selected file's settings fill a grid: the setting name stays in view, one column per server, differing values highlighted, secrets masked but still compared.
- **Previous / Next** (Shift+F8 / F8) walk every difference and carry on into the next file.
- Filters: **Only show differences**, search (settings, values, files), **Show: All / Marked / Unmarked**, **Show ignored** (dimmed), **Review list**.
- **Save report** writes the comparison as HTML and CSV under `exports/` in the data root; **Export review** does the same for the review list.

### Right-click: curate what future runs show
Right-click any setting, value, or file (in Compare, in the Diff planner's Settings tab, and in Files):
- **Group**: add to a group (pick or name one), view a group, remove from it, manage groups.
- **Rule**: create a rule from the item, add the item to an existing rule, manage rules. A rule matches files and settings by pattern (`*` and `?`, `;` between alternatives); it can name the application, give the file a friendlier label and a description, and ignore, mask, unmask, or group what it matches.
- **Mark / Unmark**: a marker kept until the app closes.
- **Copy as** JSON, TSV, Text or Hex: the setting and every value, the setting name, the values, or the right-clicked value. Masked values are copied as their marker.
- **View**: the file as a tree, the raw text of any server's copy, and history.
- **Add to report / Remove from report**: the review list, kept until removed.
- **Ignore** the setting, the right-clicked value, or the whole file, and **Mask / Unmask values**: each for this run only, always, or always for these servers. Ignored things leave the counts.
- **History**: the setting's values over time, where else it is set, and where else the value appears, from every recorded run.
- **What is…**: a placeholder showing the question a future assistant will be asked.
- **Report bug**: a preview of the whole payload (relative file path, setting, values with secrets always masked, metadata). You must confirm it holds nothing sensitive; then open a prefilled GitHub issue, or post it to a receiver set with `DRIFTBUSTER_BUG_REPORT_URL`. Nothing is sent otherwise.

**Manage choices…** edits everything saved: groups, rules, ignore and mask choices, the review list, the history's size (and clearing it), and export or import (merge or replace) of the whole set. Choices live in `curation.json` and scan history in `history.db`, both in the data root.

### Files
- Every scanned file in a sortable grid with drift, coverage, format, severity and tags; filters on top.
- Double-click a row for File details. Right-click for **Show settings in Compare**, **File details**, and Compare's file actions.
- **Files missing on some servers** (collapsed at the bottom) lists partial coverage with per-file and all-at-once re-scan.

### File details
- The baseline and any other server's copy (**Compared with**) side by side with changed lines aligned and unchanged runs folded, or as a unified diff. **Previous / Next change** (Shift+F7 / F7).
- The servers that hold the file, with selection for **Re-scan selected**; **Export HTML**, **Export JSON** and **Copy JSON**.

### Remote capture orchestration
- Use the PowerShell module when coordinating multi-host captures without launching the GUI: `Invoke-DriftBusterRemoteScan -ComputerName branch-01 -RemotePath "ProgramData\\VendorA" -RunProfilePath profiles\\vendor.json -Environment prod -Reason audit -MaskToken <token>` mounts the admin share and runs the capture in process against the UNC path.
- For environments where SMB access is blocked, flip to WinRM with `Invoke-DriftBusterRemoteScan -UseWinRM -ComputerName hq-core -RemotePath "C:\\ProgramData\\VendorA" -RunProfilePath profiles\\vendor.json -Environment prod -Reason audit -AllowUnmasked -RemoteWorkingDirectory "$env:ProgramData\\DriftBusterRemote"`. The cmdlet stages the module and backend on the remote host (PowerShell 7.6 required there), runs the capture, and copies the snapshot and manifest back into `<output>/<host>/` alongside GUI evidence.
- Generate offline runner snippets with `driftbuster registry-scan emit-config "VendorA" --root "HKLM\\Software\\VendorA,view=64"` so the multi-server view and manifests can display the requested hive list next to each host.
- After pulling results back, run `driftbuster capture run --registry-scan <output>/<host>/registry_scan.json ...` to embed the registry summary alongside filesystem detections before importing evidence into the GUI session archive.

## 5. Diff Planner
- **Files to compare** (folds away after a build): pick a baseline and one or more files, or reopen a **Recent plans** entry. **Build plan** runs the comparison.
- Results open in tabs:
  - **Settings**: the Compare view for the picked files (one column per file), with the same filters and right-click menu.
  - **Line by line**: the unified diff of each comparison with line numbers, colours and **Previous / Next change**; a picker when there are several comparisons; **Copy diff**.
  - **JSON**: the sanitized or raw payload with **Copy JSON**.

### JSON payload schema
- The **JSON** tab (Raw) mirrors the backend response returned by `DriftbusterBackend.DiffAsync`. The payload is a
  single object with the following shape:
  - `versions`: ordered list of absolute or relative file paths exactly as submitted to the backend.
  - `comparisons`: array of comparisons, each containing:
    - `from` / `to`: resolved file names displayed in plan cards.
    - `plan`: serialized content including `before`, `after`, `content_type`, labels, `mask_tokens`, `placeholder`, and
      `context_lines`.
    - `metadata`: source file paths (`left_path`, `right_path`), the resolved `content_type`, and the enforced `context_lines`.
- The **Sanitized JSON** toggle emits the digest-only summary that the GUI stores in MRU entries. Sanitized payloads always
  exclude raw file contents and instead provide:
  - `generated_at`: UTC timestamp recorded when the diff ran.
  - `versions`: file names with directory information stripped.
  - `comparison_count`: convenience counter for paging bulk comparisons.
  - `comparisons[]`: each entry includes `plan` metadata (labels, mask tokens, placeholder, context lines), `metadata`
    (content type plus redacted path names), and a `summary` block exposing `before_digest`, `after_digest`, `diff_digest`,
    and line statistics (`before_lines`, `after_lines`, `added_lines`, `removed_lines`, `changed_lines`).
- Sanitized payloads are the only variant persisted to disk; MRU entries store the `sanitized_summary.json` format for replay
  without risking sensitive data.
- Redacted samples for both payloads live under `artifacts/samples/diff-planner/`:
  - `raw_payload.json` demonstrates the direct backend contract.
  - `sanitized_summary.json` shows the MRU-safe structure with digests and counts.

## 6. Hunt Explorer
- Scan a folder or file for values that change between machines (paths, hosts, connection strings, versions), optionally only lines containing some text.
- Rule chips show how many findings each rule has; click one to show only its findings. Search narrows by rule, file, or excerpt.
- The findings grid (rule, file, line) sits beside the selected finding in full: rule, token, description, `path:line`, the excerpt, **Copy location**, **Copy excerpt**, **Open folder**.
- Right-click a finding to copy it (location, path, excerpt, JSON, TSV), show only its rule or file, open its folder, or **Report false positive** through the bug report preview. The **JSON** tab holds the raw result.

## 7. Profiles
- Saved profiles on the left (**Refresh**, **Load**), the selected profile's form on the right with **Save profile**, **Run profile** and **Prepare offline collector** pinned above it.

### Run profile scheduling workflow

1. Open the **Profiles** view and either load an existing profile or enter the sources/baseline for a new one.
2. Scroll past the options list to find the schedule cards. Each card requires a **Name**, **Profile** reference (typically the profile name), and an **Every** interval (shorthand like `15m`, `24h`, or ISO 8601). The **Profile** field is now an editable dropdown that lists every saved profile plus the in-progress draft name so you can reuse definitions without retyping. Optional **Start at**, **Window start/end/timezone**, **Tags**, and metadata rows capture quiet hours, labels, and notification contacts.
3. Renaming the active profile updates any blank schedule rows automatically so cadence entries continue targeting the right definition; schedule cards with custom profile overrides keep their values intact.
4. Click **Save profile** to persist both the `profile.json` definition and the consolidated `Profiles/schedules.json` manifest. The GUI normalises tag lists and metadata keys before writing to disk.
5. Use the console tool when automating: `driftbuster schedule list` to inspect schedules, `due` to surface pending runs, `mark-complete` to advance cadence after a run, and `skip-until` to defer execution. The GUI and console tool share the manifest and `scheduler-state.json` files so state remains aligned.

### Run profiles secret scanner workflow
- Switch to **Profiles** and open **Secret scanner settings** to review ignore lists. The dialog (`SecretScannerSettingsViewModel`) clones the active profile configuration, so cancelling leaves the persisted options untouched.
- Saving applies the ignore rules/patterns plus optional inline ruleset JSON to the profile and updates the summary string beneath the button. The view model emits the same payload the backend run profile executor consumes for `driftbuster profile run`, so the GUI and console runs stay aligned.
- When a run executes, the backend writes redaction messages (for example, `secret candidate redacted (PasswordAssignment) …`) into the activity timeline and into `metadata.json → secrets.messages`. Rows associated with scrubbed files surface a **Secrets** pill, mirroring the `HasSecrets` flag in the results view models.
- Sanitised copies persist under the profile output directory alongside `metadata.json`. The manifest exposes rule version, ignored lists, findings, and the logged messages so auditors can reconcile GUI output with stored evidence. Pair these manifests with `artifacts/secret-scanning/realtime-validation-20251025T065645Z.log` when capturing validation proof for A13.3.

## Themes

- **Palette catalog:** `gui/DriftBuster.Gui/Assets/Styles/Theme.axaml` now exposes `Palette.DarkPlus` and `Palette.LightPlus` resource dictionaries. Each dictionary defines the `Color.*` and `Brush.*` tokens consumed throughout the GUI so palette updates remain isolated to a single file.
- **Migration defaults:** Legacy callers that rely on `Color.Accent`, `Brush.Surface`, and related keys continue to resolve without change. The base resources still point at the Dark+ palette until the selector applies a new option, preventing regressions for cached control templates and custom styles.
- **Runtime selection:** `MainWindowViewModel` binds the header dropdown to these palette entries. Selecting an option updates `Application.Current.RequestedThemeVariant` and rewrites the shared color/brush tokens so view refreshes pick up the new palette immediately.
- **Extending palettes:** To add additional themes, clone the structure used by `Palette.DarkPlus`, register a new `ThemeOption` in `ApplicationThemeRuntime`, and update the documentation matrix above. Keep the `Theme.DefaultPaletteId` resource in sync with the intended startup palette so migrations stay deterministic.

### Performance

- Run `scripts/verify_coverage.sh --perf-smoke` to add the perf-smoke suite (`Category=PerfSmoke`); the run log lands in `artifacts/perf/perf-smoke-<timestamp>.log`.
- Lists and grids that can grow large (hosts, files, settings, diff lines, findings) always virtualise. The activity feed and the missing-files list switch to a virtualised layout at **400** entries; set `DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD=<count>` to move that point, or `DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION=true|false` to force it, before launching the GUI.
- Re-run the suite after tweaking thresholds or toast batching logic and compare the logs to confirm behavioural drift before shipping changes.

## 8. Backend Bridge
- `DriftbusterService` instantiates the shared `DriftbusterBackend` class and executes diff, hunt, and run-profile operations in-process.
- Diff calls load file contents, build the same JSON payload exposed to the UI (including the `settings` comparison of the picked files), and reuse the shared models for plan metadata.
- Hunt scans walk the filesystem locally, apply the default rule set, and surface filtered hits to the view models.
- Run profile actions persist JSON definitions, copy snapshot files, and emit metadata using the shared library helpers.
- Multi-server orchestration runs in process through the backend multi-server runner, returns the setting-by-setting `comparison` and every server's copy of each file (`host_diffs`), streams per-host progress back into toasts and the activity timeline, and persists cached diffs under the DriftBuster data root (e.g. `%LOCALAPPDATA%/DriftBuster/cache/diffs/`, `$XDG_DATA_HOME/DriftBuster/cache/diffs/`).
- All work runs asynchronously on background tasks so the UI stays responsive; errors surface through the existing status banners.

## 9. Packaging Options
- Default release builds produce an installer:
  - `dotnet run --project cli/DriftBuster.Cli -- release --runtime win-x64 --release-notes notes/releases/<semver>.md --installer-rid win-x64` (from the repository root)
  - Self-contained publishes: `build/artifacts/{cli,gui}/<rid>`; installer artifacts: `artifacts/velopack/releases/<rid>`.
- Direct Velopack usage:
  - `dotnet tool restore`
  - `scripts/build_velopack_release.sh --version <semver> --release-notes notes/releases/<semver>.md [--rid win-x64]`
  - Use `--channel` (prereleases) and `--pack-id` (bundle id) as needed.
- Manual portable publish (for quick local runs):
  - `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true`
- Before packaging, sync versions: `dotnet run --project cli/DriftBuster.Cli -- version`.

### Packaging prerequisites checklist

| Step | Purpose |
|------|---------|
| Capture current commit hash (`git rev-parse HEAD`) with the bundle. | Tie installer evidence back to source. |
| Generate SHA256 manifest for every staged file. | Enable downstream integrity verification without internet access; see `docs/windows-gui-notes.md#evidence`. |
| Copy updated `NOTICE` directory into the bundle. | Keep licence obligations intact across packaging flavours. |
| Log the publish transcript (`artifacts/gui-packaging/README.md` has the commands) and archive it alongside hashes. | Provide reproducible evidence for legal and security reviews. |

## 10. Manual Smoke Checklist
- Located at `notes/checklists/gui-smoke.md`.
- Walks every page (Setup, Compare, Files, File details, Diff planner, Hunt explorer, Profiles), the right-click menus and Manage choices, in both themes.
- Record date/operator each time the checklist is executed.

## 11. Automated & Headless Tests
- UI automation lives in `gui/DriftBuster.Gui.Tests/Ui` and complementary view-model suites under `gui/DriftBuster.Gui.Tests/ViewModels`. Headless UI tests are attributed with `[AvaloniaFact]`, ensuring each case runs on the Avalonia dispatcher (navigation, drilldown exports, hunt flows, converters, session cache, and theme toggles).
- The test assembly bootstraps Avalonia once through `gui/DriftBuster.Gui.Tests/TestAppBuilder.cs` (`[assembly: AvaloniaTestApplication]`), which reuses `Program.BuildAvaloniaApp()` (real `App`, Fluent theme, embedded Inter font) with `.UseHeadless(...)`. `HeadlessBootstrapTests` proves the styles load and a window with a `ToggleSwitch` shows headless; `AutomationPropertiesTests` proves every interactive control carries an automation ID and a resolvable name.
- Run targeted suites via tmux: `tmux new -d -s codexcli-ui 'dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter "FullyQualifiedName~DiffViewTests"'`.
- Full coverage expectations:
  - Debug collect: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --collect:"XPlat Code Coverage" --results-directory artifacts/coverage-dotnet`
  - Release collect: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory artifacts/coverage-dotnet`
  - XAML compilation gate: `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -p:EnableAvaloniaXamlCompilation=true`

## 12. Troubleshooting
| Symptom | Suggested Checks |
|---------|-----------------|
| Validation won’t clear | Confirm file/directory exists and is accessible; refresh the path using Browse. |
| Empty hunt results | Check filter string, increase rule coverage, or drop filter to view raw hits. |
| Clipboard not working | Ensure the app is running in a desktop session (clipboard APIs require a real user session). Use the activity timeline’s copy buttons to verify clipboard access quickly. |
| Hash verification mismatch | Recompute SHA256 hashes for the staged bundle and confirm the manifest includes every file distributed to operators; regenerate the manifest before retrying install. |
| MSIX refuses to install (certificate error) | Verify the signing certificate thumbprint matches the value logged in `notes/checklists/legal-review.md`; import the certificate into `Trusted People` on the VM before rerunning the install. |

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
- Package the module for distribution using `pwsh ./scripts/package_powershell_module.ps1 -Configuration Release`. The script copies the compiled backend into a temporary staging area, produces `artifacts/powershell/releases/DriftBuster.PowerShell-<version>.zip`, and writes a matching `.sha256` checksum file in the same directory.
- Confirm the checksum before publishing: `Get-FileHash artifacts/powershell/releases/DriftBuster.PowerShell-<version>.zip -Algorithm SHA256` should match the recorded hash.

### Troubleshooting
| Symptom | Suggested Checks |
|---------|-----------------|
| Import fails with `DriftBusterBackendMissing`. | Publish the backend: `dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Debug -o gui/DriftBuster.Backend/bin/Debug/published`, then re-import the module or copy the resulting `DriftBuster.Backend.dll` next to `DriftBuster.psm1`. Confirm the cache directory contains the DLL afterwards. |
