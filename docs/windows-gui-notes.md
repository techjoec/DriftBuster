# Windows GUI Notes

Updated audit of the Avalonia starter plus earlier research log. For a user-facing walkthrough see `docs/windows-gui-guide.md`.

## Current Base Assets (2025-10 audit)

- **Avalonia shell**: `gui/DriftBuster.Gui` targets `net8.0` with Avalonia 11.2.0, serving a red/black window that swaps Diff/Hunt panes via `CurrentView` bindings.
- **Python bridge**: `src/driftbuster/api_server.py` exposes `ping`, `diff`, and `hunt` commands, returning JSON so the GUI can pretty-print diff plans and hunt hits.
- **Process contract**: A pooled Python backend stays warm (idle timeout 3 min) so repeated Diff/Hunt calls reuse the same process; responses surface as `{ "ok": bool, "result": {...} }` envelopes.
- **UI snapshot**: Diff view now validates both file pickers, renders plan/metadata tables, and offers a one-click raw JSON copy. Hunt view adds directory picker, status messaging, and a sortable grid of hits with rule metadata.
- **Responses**: Diff returns `plan` + `metadata` describing the selected files; Hunt returns filtered hit lists (leveraging `hunt_path(..., return_json=True)`).
- **Assets**: `Directory.Build.props` centralises net8.0 defaults; `gui/DriftBuster.Gui/Assets/app.ico` holds the DrB red/black logo baked into the WinExe manifest.

## Host Dependencies

- **.NET SDK 8.0.x** installed locally for restore, build, run, and publish steps.
- **Python 3.10+** discoverable as `python`; repository layout already supports `python -m driftbuster.api_server` without extra packaging.
- **Optional tooling**: Avalonia preview support in editor (Rider, VS Code extension) improves XAML edits but is not required.
- **Runtime checks**: Confirm `dotnet --list-sdks` includes 8.x and `python --version` reports ≥3.10 before running the GUI.
- **NuGet footprint**: Restore succeeds with Avalonia 11.2.0 packages (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Fonts.Inter`, `Avalonia.Themes.Fluent`, `Avalonia.Diagnostics`). No FluentAvalonia dependency is required.
- **Assets**: `Assets/app.ico` already contains the DrB badge; replace it with design-approved artwork before shipping installers.

## Integration Milestones

1. Port the Avalonia project, bridge module, and build props into the repo while preserving relative paths.
2. Replace sample bridge handlers with real JSON responses (e.g., wrap `hunt_path(..., return_json=True)` and planned diff helpers).
3. Document GUI launch instructions plus dependency checklist in the main README or companion doc.
4. Add UX polish: file pickers, status text, and richer result panels driven by the new JSON contract.
5. Prepare Windows packaging guidance (`dotnet publish -r win-x64 -c Release /p:PublishSingleFile=true`) once features settle.
6. Compiled bindings remain disabled (`AvaloniaUseCompiledBindingsByDefault=false`); add `x:DataType` hints later if we re-enable them.

> Detailed host prep commands and logs live in `notes/dev-host-prep.md`.

## Packaging Quickstart

- Restore & build: `dotnet restore` then `dotnet build -c Release gui/DriftBuster.Gui/DriftBuster.Gui.csproj`.
- Portable zip (uses host .NET runtime): `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false /p:IncludeNativeLibrariesForSelfExtract=true`.
- Self-contained bundle (ships .NET runtime): add `/p:SelfContained=true` and keep `PublishSingleFile=true`. Expect ~120 MB output.
- Python runtime: drop a vetted CPython build (or venv) next to `DriftBuster.Gui.exe`, or integrate a launcher script that primes `%PATH%` before the GUI starts.
- Record publish commands + hashes in `notes/dev-host-prep.md` for each distribution flavour.

## Manual Smoke Checklist

- Follow `notes/checklists/gui-smoke.md` for the current walkthrough (ping core, run diff/hunt, validate error handling, and confirm backend shutdown).

> **Still useful:** Historical research content below remains for context on alternative stacks and packaging decisions.

---

## Windows GUI Exploration (Future Work)

Research log for a potential Windows-first shell once the CLI outputs stabilise.
Active user requirements are tracked in the status log (`notes/status/gui-research.md#user-requirements`).

## Candidate Frameworks

- **WinUI 3 / Windows App SDK**
  - Pros: Native Windows visuals, fluent design widgets, built-in WebView2 for HTML embedding.
  - Cons: Requires MSIX packaging and Windows 10 1809+, tooling tied to Visual Studio.
  - Licensing: MIT for the SDK; WebView2 runtime redistribution allowed via Microsoft runtime installer.
- **Tkinter**
  - Pros: Bundled with CPython, trivial to script, no extra runtime dependencies.
  - Cons: Limited modern UI widgets, no native WebView (requires third-party bridge for HTML reports).
  - Licensing: Inherits Python's PSF licence; shipping requires respecting CPython redistribution notice.
- **PySimpleGUI (Tk flavour)**
  - Pros: Rapid prototyping layer, higher-level API for layout, built-in file picker flows.
  - Cons: Depends on Tkinter backend capabilities; LGPL for PySimpleGUI requires source offer for modifications.
  - Licensing: LGPLv3; safe for closed distribution if we distribute unmodified wheels + licence text.
- **Electron**
  - Pros: Full Chromium engine, powerful rendering for HTML/JS-based reports, wide ecosystem of components.
  - Cons: Heavy footprint (~100 MB), Node.js toolchain, security posture must be hardened.
  - Licensing: MIT; must audit bundled npm packages for compatible licences.

## Packaging & Distribution Plan

- **MSIX**
  - Natural fit for WinUI/Electron bundles; delivers auto-updates and clean install/uninstall.
  - Needs code-signing certificate and explicit capability declarations (file system access, WebView2 runtime).
- **Portable ZIP**
  - Provide zip archive with executable + assets for Tkinter/PySimpleGUI builds; supports offline admins.
  - Must include Python runtime (`python311.dll`, stdlib) when targeting hosts without Python preinstalled.
- **Bundled Python Runtime**
  - Use `python -m zipapp`, `pyinstaller`, or `briefcase` depending on framework.
  - Record PSF licence + third-party notices alongside binaries.
- **Update Channel**
  - Manual updates: publish checksum + version in release notes until automation is approved.

## Distribution & Licensing Notes

- Maintain a `NOTICE` file covering Python, PySimpleGUI, Electron/Chromium, and any npm/nuget packages.
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
