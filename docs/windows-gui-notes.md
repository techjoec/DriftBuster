# Windows GUI Notes

Updated audit of the Avalonia starter plus earlier research log. For a user-facing walkthrough see `docs/windows-gui-guide.md`.

## Current Base Assets (2025-10 audit)

- **Avalonia shell**: `gui/DriftBuster.Gui` targets `net8.0` with Avalonia 11.2.0. The refined header couples navigation, backend health, and theme controls in a compact strip; views swap via `CurrentView` bindings.
- **Backend library**: `gui/DriftBuster.Backend` hosts shared diff, hunt, and run-profile helpers consumed by both the GUI and the PowerShell module.
- **Execution contract**: Operations run on background tasks, returning the same JSON payloads previously emitted by the Python helper so the UI bindings stay untouched.
- **UI snapshot**: Diff view validates inputs, renders plan/metadata cards, and offers a copy-raw-JSON action. Hunt view adds directory picker, status messaging, and card-style findings with token badges. The multi-server screen now uses tidy host cards, side-by-side execution/timeline panels, and a lean guidance banner to keep the orchestration workflow focused.
- **Responses**: Diff returns `plan` + `metadata` describing the selected files; Hunt returns filtered hit lists using the built-in rule set.
- **Assets**: `Directory.Build.props` centralises net8.0 defaults; `gui/DriftBuster.Gui/Assets/app.ico` holds the DrB red/black logo baked into the WinExe manifest.

## Host Dependencies

- **.NET SDK 8.0.x** installed locally for restore, build, run, and publish steps.
- **Optional tooling**: Avalonia preview support in editor (Rider, VS Code extension) improves XAML edits but is not required.
- **Runtime checks**: Confirm `dotnet --list-sdks` includes 8.x before running the GUI.
- **NuGet footprint**: Restore succeeds with Avalonia 11.2.0 packages (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Fonts.Inter`, `Avalonia.Themes.Fluent`, `Avalonia.Diagnostics`). No FluentAvalonia dependency is required.
- **Assets**: `Assets/app.ico` already contains the DrB badge; replace it with design-approved artwork before shipping installers.

## Integration Milestones

1. Port the Avalonia project, bridge module, and build props into the repo while preserving relative paths.
2. Replace sample bridge handlers with real JSON responses wired to `Driftbuster.Backend` helpers.
3. Document GUI launch instructions plus dependency checklist in the main README or companion doc. Include how registry scan outputs (`registry_scan.json`) appear alongside file-based results when present.
4. Add UX polish: accent/outline button variants, larger hit targets, table/card refinements, backend status dot, and theme switching.
5. Prepare Windows packaging guidance (`dotnet publish -r win-x64 -c Release /p:PublishSingleFile=true`) once features settle.
6. Compiled bindings remain disabled (`AvaloniaUseCompiledBindingsByDefault=false`); add `x:DataType` hints later if we re-enable them.

> Detailed host prep commands and logs live in `notes/dev-host-prep.md`.

## Packaging Quickstart

Follow the restore + publish flow below so each bundle lands with reproducible file hashes and matching evidence in `artifacts/gui-packaging/`.

1. Restore & compile once per session: `dotnet restore` then `dotnet build -c Release gui/DriftBuster.Gui/DriftBuster.Gui.csproj`.
2. Snapshot the git commit (`git rev-parse HEAD > artifacts/gui-packaging/commit.txt`) before publishing so downstream evidence ties back to source.

### Portable ZIP workflow (host runtime required)

1. Publish:
   ```powershell
   dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj `
     -c Release -r win-x64 `
     /p:PublishSingleFile=true `
     /p:SelfContained=false `
     /p:IncludeNativeLibrariesForSelfExtract=true
   ```
2. Compress the publish folder (`publish/`) into `DriftBuster.Gui-portable-win-x64.zip` and store it under `artifacts/gui-packaging/portable/` (create the folder when capturing release evidence).
3. Copy `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` beside the zip; bundle both into the operator hand-off package.
4. Capture SHA256 hashes for the zip + WebView2 installer via `Get-FileHash` (PowerShell) and record them in `artifacts/gui-packaging/portable/hashes.txt` (or append the values to `artifacts/gui-packaging/publish-framework-dependent.sha256` to mirror the tracked evidence format).
5. Note the required pre-installed dependencies (host must have .NET 8.0 Desktop Runtime + WebView2) in the release notes.

### Self-contained bundle workflow (ships .NET runtime)

1. Publish:
   ```powershell
   dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj `
     -c Release -r win-x64 `
     /p:PublishSingleFile=true `
     /p:SelfContained=true `
     /p:IncludeNativeLibrariesForSelfExtract=true
   ```
2. Rename the single-file output to `DriftBuster.Gui-selfcontained.exe` and stage it under `artifacts/gui-packaging/selfcontained/` (create the folder if it does not exist yet).
3. Bundle the WebView2 offline installer plus `NOTICE` artefacts in the same folder so operators can deploy without internet access.
4. Capture SHA256 hashes for every staged file and append to `artifacts/gui-packaging/selfcontained/hashes.txt` (or reuse `artifacts/gui-packaging/publish-self-contained.sha256`).
5. Verify launch on a clean Windows VM (no .NET runtime installed) and log the console trace to `artifacts/gui-packaging/selfcontained/first-launch.log`.

For each flavour, append the executed commands, hash outputs, and validation notes to `notes/dev-host-prep.md` so packaging evidence stays centralised. The current repository snapshot captures both publish transcripts and hashes in `artifacts/gui-packaging/publish-framework-dependent.log`, `artifacts/gui-packaging/publish-framework-dependent.sha256`, `artifacts/gui-packaging/publish-self-contained.log`, and `artifacts/gui-packaging/publish-self-contained.sha256`.

## Manual Smoke Checklist

- Follow `notes/checklists/gui-smoke.md` for the current walkthrough (ping core, run diff/hunt, validate error handling, and confirm backend shutdown).

## Headless UI Testing (2025-10 refresh)

- **Guarded initialisation**: `Program.EnsureHeadless(Func<AppBuilder, AppBuilder>?)` now prevents duplicate Avalonia setup by reusing the first headless instance. The fixture in `.gui/DriftBuster.Gui.Tests/Ui/HeadlessFixture.cs` pipes in `UseHeadless` so repeated calls stay safe.
- **Shared collection & dispatcher facts**: `[Collection(HeadlessCollection.Name)]` still coordinates Avalonia access, while `[AvaloniaFact]` ensures dispatcher-bound tests (navigation, drilldown, converters, session cache, compact view instantiation) run on the UI thread.
- **tmux command shape**: Run GUI tests inside tmux to keep sessions responsive, e.g. `tmux new -d -s codexcli-ui 'cd /github/repos/DriftBuster && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj'`. Capture logs with `|& tee artifacts/<session>.log` when reproducing issues.
- **Focused filters**: Use `--filter 'FullyQualifiedName~MainWindowUiTests'` (or the other class names) for quick iteration, then finish with full Debug/Release passes and `-p:EnableAvaloniaXamlCompilation=true` to mirror release builds.
- **Diagnostics**: `AvaloniaSetupInspection.LogSetupState` (run with `AVALONIA_INSPECT=1`) logs style dictionaries into `artifacts/codexcli-inspect.log` for tracing resource registration order when investigating future regressions.

> **Still useful:** Historical research content below remains for context on alternative stacks and packaging decisions.

---

## Windows GUI Exploration (Future Work)

Research log for a potential Windows-first shell once the CLI outputs stabilise.
Active user requirements are tracked in the status log (`notes/status/gui-research.md#user-requirements`).

## Candidate Frameworks

**Current decision (A19.1): prioritise WinUI 3 for the shipping Windows shell, keep Tkinter/PySimpleGUI research as lightweight tooling notes, and treat Electron as a future HTML-heavy contingency.** See `artifacts/gui-packaging/framework-evaluation-2025-10-25.json` for the structured snapshot backing this matrix.

| Framework | Why we would pick it | Key blockers | Packaging notes | Licence callouts |
| --- | --- | --- | --- | --- |
| **WinUI 3 / Windows App SDK** | Fluent native shell, built-in WebView2 keeps HTML diff rendering first-class, aligns with Microsoft tooling most operators already have. | Requires MSIX tooling (Windows 10 1809+), Visual Studio workload heavier than dotnet-only projects, WebView2 runtime must be staged for offline installs. | Default to MSIX with optional self-contained `.NET` publish for offline deployments; bundle WebView2 Evergreen installer in distribution folder. | MIT SDK + WebView2 redistribution notice; add Windows App SDK, WinUI, and WebView2 runtime to NOTICE bundle. |
| **Tkinter** | Ships with CPython, very small footprint, fast to script maintenance utilities. | No native WebView for HTML reports, UI dated for production shell without major effort. | Portable zip with embedded CPython covers offline hosts; MSIX adds little beyond packaging convenience. | Include Python PSF licence plus Tcl/Tk notice in NOTICE directory. |
| **PySimpleGUI (Tk flavour)** | Higher-level layout wrappers on top of Tkinter, minimal code to build dialogs and wizards. | Still inherits Tkinter rendering limits, LGPLv3 obligations if modified. | Same portable zip approach as Tkinter; document source-offer steps if distributing customised wheel. | NOTICE must reference PySimpleGUI LGPLv3 text and source access location. |
| **Electron** | Chromium renderer unlocks fully interactive HTML/JS dashboards and deep telemetry overlays. | Sizeable download (~100 MB), Node.js toolchain, extra security hardening for offline-friendly builds. | MSIX or Squirrel packages; ensure hashed offline bundle with signed Node modules. | Enumerate bundled npm licences in NOTICE and track updates rigorously. |

Update the status log (`notes/status/gui-research.md`) when evaluating new candidates so this section stays synced.

## Packaging & Distribution Plan

_Execution queue:_ see `CLOUDTASKS.md` area A19 for the current packaging backlog.

- **MSIX**
  - Natural fit for WinUI/Electron bundles; delivers auto-updates and clean install/uninstall.
  - Needs code-signing certificate and explicit capability declarations (file system access, WebView2 runtime).
- **Portable ZIP**
  - Provide zip archive with executable + assets; supports offline admins.
- **Bundled runtime**
  - Ship the .NET runtime when targeting hosts without it (`/p:SelfContained=true`).
  - Record third-party notices alongside binaries.
- **WebView2 Evergreen redistribution**
  - Bundle the offline Evergreen installer (`MicrosoftEdgeWebView2RuntimeInstallerX64.exe`) beside the GUI publish output.
  - During packaging, script `MicrosoftEdgeWebView2RuntimeInstallerX64.exe /silent /install` before first GUI launch; emit installer logs to `artifacts/gui-packaging/` for traceability.
  - After installation, verify `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` exists and record the `pv` value in release notes so operators can confirm runtime parity offline.
- **Update Channel**
  - Manual updates: publish checksum + version in release notes until automation is approved.

## Evidence index (A19.6)

| Evidence | Location | Purpose |
| --- | --- | --- |
| Framework-dependent publish transcript | `artifacts/gui-packaging/publish-framework-dependent.log` | Records the exact command output for the portable ZIP workflow. |
| Framework-dependent hash | `artifacts/gui-packaging/publish-framework-dependent.sha256` | Verifies the single-file portable binary shipped in that workflow. |
| Self-contained publish transcript | `artifacts/gui-packaging/publish-self-contained.log` | Captures the command output for the self-contained bundle. |
| Self-contained hash | `artifacts/gui-packaging/publish-self-contained.sha256` | Confirms the signed binary that includes the .NET runtime. |
| Framework decision matrix | `artifacts/gui-packaging/framework-evaluation-2025-10-25.json` | Machine-readable rationale aligning with the narrative matrix in this doc. |
| Windows packaging smoke log | `artifacts/gui-packaging/windows-smoke-tests-2025-02-14.json` | Evidence of Windows 10/11 installer validation runs referenced in the status notes. |
| Packaging evidence guide | `artifacts/gui-packaging/README.md` | Step-by-step instructions mirroring the captured transcripts and hash commands. |

Keep new publish runs in sync with this table so operators can audit the exact evidence set without guessing at filenames or directories.

## Distribution & Licensing Notes

- Maintain a `NOTICE` file covering .NET dependencies, Avalonia packages, and any auxiliary tooling. Reference the current template stored alongside the packaging evidence so regenerated bundles inherit the same baseline.
- Confirm WebView2 Evergreen redistributable terms when embedding reports.
- Avoid auto-downloading dependencies at runtime; ship vetted binaries to keep supply chain tight.
- Require offline activation path so security teams can inspect builds before deployment.
- Generate SHA256 manifests for every bundle (see the checked-in `publish-*.sha256` files in `artifacts/gui-packaging/`) and mirror the manifest inside the operator hand-off package.
- Capture install/uninstall transcripts per flavour (e.g., `publish-*.log` in `artifacts/gui-packaging/`) and record signing certificate details (thumbprint, expiry, issuer) in the legal review checklist.
- Provide certificate chain exports under `artifacts/gui-packaging/certificates/` so operators can import signing roots on isolated hosts before installing MSIX packages. Create the folder on first run if it does not yet exist in the evidence tree.

### Offline activation guidance (A19.5.1)

1. Stage the portable zip or self-contained bundle plus `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` on a removable drive; include `NOTICE` and hash manifest files so operators can audit contents offline.
2. On the target host, validate hashes with `Get-FileHash <file> -Algorithm SHA256` and compare against the recorded values before extracting or installing anything.
3. Run the WebView2 installer with `MicrosoftEdgeWebView2RuntimeInstallerX64.exe /silent /install` while disconnected from the network; capture `%TEMP%\MicrosoftEdgeWebView2Setup.log` and move it into `C:\ProgramData\DriftBuster\Logs\webview2-offline.log` for archival.
4. Extract the portable zip (or copy the self-contained executable) into `C:\ProgramData\DriftBuster\App\` and ensure read/write permissions are limited to administrators.
5. Launch the GUI once with `DriftBuster.Gui*.exe --log-file C:\ProgramData\DriftBuster\Logs\first-boot-offline.log` to generate the initial cache while offline; archive the log alongside the hash manifest.
6. Document the activation steps and log locations in `notes/dev-host-prep.md` so subsequent operators can replay the process without re-downloading assets.
7. Run `python -m scripts.offline_compliance_audit artifacts/gui-packaging` and archive the resulting report alongside the logs so compliance reviewers can verify evidence without internet access.

## Data Flow & UX Outline

- **Input Sources**: Consume generated HTML reports for rich rendering and JSON summaries for metadata panels.
- **Workflow**: Prompt user to select snapshot bundle → parse JSON metadata → display HTML diff alongside metadata sidebar.
- **Minimal Features**:
  - Load snapshot/scan outputs from local disk.
  - Toggle between HTML diff view and metadata table.
  - Highlight sensitive tokens flagged by redaction hooks.
  - Provide quick links to open source file paths in default editor (read-only).
- **Extensibility Hooks**: Keep data loading modular so CLI continues to own scanning logic.

## Manual Testing Expectations (Future)

- Smoke test zipped build on clean Windows VM (no Python installed) to confirm bundled runtime works.
- Validate GUI handles large HTML reports (>5 MB) without freezing; note memory footprint.
- Confirm JSON metadata parsing tolerates missing optional fields and surfaces errors via dialog.

## Compliance & Accessibility Checklist

- Legal Guardrails: Never embed vendor logos or proprietary sample content; rely on neutral icons.
- Security: Store recent files list in memory only; avoid writing cache files unless explicitly configured.
- Accessibility: Target keyboard navigation, high-contrast theme, and screen-reader labels for critical controls.
  1. Launch packaged build on Windows 11 VM with stable Narrator + Inspect versions logged in the accessibility evidence file.
  2. Start Narrator (`Win + Ctrl + Enter`) before opening the DriftBuster shell so focus events are captured from the splash screen.
  3. Tab through server selection and drilldown views; record any unlabeled controls or incorrect announcements.
  4. Run `inspect.exe` from the Windows SDK, attach to the DriftBuster window, and capture `Name`, `AutomationId`, and `HelpText` for critical controls.
  5. Switch to High Contrast mode (Windows Settings → Accessibility → Contrast Themes) and repeat Inspect sweeps to document contrast ratio readings.
  6. Store transcripts, tool versions, and screenshots in `artifacts/gui-accessibility/` for auditability.
- Privacy: Ensure redacted tokens remain masked in the UI and exports.
- Documentation: Keep safeguards aligned with `docs/legal-safeguards.md` when drafting user guidance.
