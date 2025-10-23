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

- Restore & build: `dotnet restore` then `dotnet build -c Release gui/DriftBuster.Gui/DriftBuster.Gui.csproj`.
- Portable zip (uses host .NET runtime): `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false /p:IncludeNativeLibrariesForSelfExtract=true`.
- Self-contained bundle (ships .NET runtime): add `/p:SelfContained=true` and keep `PublishSingleFile=true`. Expect ~120 MB output.
- Record publish commands + hashes in `notes/dev-host-prep.md` for each distribution flavour.

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

**Current decision (A19.1): prioritise WinUI 3 for the shipping Windows shell, keep Tkinter/PySimpleGUI research as lightweight tooling notes, and treat Electron as a future HTML-heavy contingency.**

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
- **Update Channel**
  - Manual updates: publish checksum + version in release notes until automation is approved.

## Distribution & Licensing Notes

- Maintain a `NOTICE` file covering .NET dependencies, Avalonia packages, and any auxiliary tooling.
- Confirm WebView2 Evergreen redistributable terms when embedding reports.
- Avoid auto-downloading dependencies at runtime; ship vetted binaries to keep supply chain tight.
- Require offline activation path so security teams can inspect builds before deployment.

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
- Privacy: Ensure redacted tokens remain masked in the UI and exports.
- Documentation: Keep safeguards aligned with `docs/legal-safeguards.md` when drafting user guidance.
