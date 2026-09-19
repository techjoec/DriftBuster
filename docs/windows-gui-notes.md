# Windows GUI Notes

Engineering notes for the Avalonia GUI. For a user-facing walkthrough see `docs/windows-gui-guide.md`.

## Current base assets

- **Avalonia shell**: `gui/DriftBuster.Gui` targets `net10.0` with Avalonia 12. The header couples navigation, backend health, and theme controls in a compact strip; views swap via `CurrentView` bindings.
- **Backend library**: `gui/DriftBuster.Backend` hosts shared diff, hunt, and run-profile helpers consumed by both the GUI and the PowerShell module.
- **Execution contract**: Operations run in process on background tasks through `IDriftbusterBackend`, returning the JSON payloads the UI bindings consume.
- **UI snapshot**: every page fills the window with a list or grid beside the selected item's details, panes that scroll on their own, and right-click menus for actions (see `docs/windows-gui-guide.md`). Shared styles (`Border.pane`, `Button.chip`, `Border.count`, data grid headers, tabs) live in `Assets/Styles/Theme.axaml`; dialogs and the clipboard go through `Views/DialogHost.cs`.
- **Responses**: Multi-server returns the catalog, drilldown entries with every server's copy of each file, and the setting-by-setting `comparison`; Diff returns `plan` + `metadata` per comparison plus a `settings` comparison of the picked files; Hunt returns hit lists from the built-in rule set.
- **Curation and history**: `ICurationService` (`CurationService.Shared` over the data root) holds the user's groups, rules, ignore and mask choices and review list (`curation.json`) and the scan history (`history.db`); `CompareViewModel` re-applies curation on every change.
- **Assets**: `Directory.Build.props` centralises net10.0 defaults; `gui/DriftBuster.Gui/Assets/app.ico` holds the DrB red/black logo baked into the WinExe manifest.

## Embedded Inter Font

The GUI ships Inter through `Avalonia.Fonts.Inter`, which registers the font under the collection URI `fonts:Inter`. Every reference to the family (the `FontManagerOptions` default and fallback in `Program.cs`, the `Window` style in `Assets/Styles/Theme.axaml`) must use the keyed form `fonts:Inter#Inter`. A bare `Inter` only searches the system fonts, so on any machine without Inter installed the first text layout throws:
```
System.InvalidOperationException: Could not create glyphTypeface. Font family: Inter (key: )
```
The headless test suite cannot catch this because the headless platform stubs font resolution; a real Windows guest without Inter installed is the check. Verification on such a guest is a lab workflow kept outside the repository.

## Host Dependencies

- **.NET SDK 10.0.x** installed locally for restore, build, run, and publish steps.
- **Optional tooling**: Avalonia preview support in editor (Rider, VS Code extension) improves XAML edits but is not required.
- **Runtime checks**: Confirm `dotnet --list-sdks` includes 10.x before running the GUI.
- **NuGet footprint**: the `PackageReference` list in `gui/DriftBuster.Gui/DriftBuster.Gui.csproj` (Avalonia with DataGrid, ItemsRepeater, Inter and the Fluent theme, CommunityToolkit.Mvvm, Velopack, logging abstractions). No FluentAvalonia or Avalonia.Diagnostics dependency.
- **Assets**: `Assets/app.ico` already contains the DrB badge; replace it with design-approved artwork before shipping installers.

## Compiled Bindings

Compiled bindings are the default (`AvaloniaUseCompiledBindingsByDefault=true`). Every view declares `x:DataType`; bindings that reach a parent view model through `RelativeSource` cast the ancestor `DataContext` to its type. The one exception is documented inline in `RunProfilesView.axaml`.

> Detailed host prep commands and logs live in `notes/dev-host-prep.md`.

## Packaging Quickstart

Follow the flow below so each bundle ships with a hash manifest an operator can check offline. `artifacts/gui-packaging/README.md` has the same steps with transcript capture.

1. Build once per session: `dotnet build -c Release gui/DriftBuster.Gui/DriftBuster.Gui.csproj`.
2. Record the source commit (`git rev-parse HEAD`) with the bundle so evidence ties back to source.

### Self-contained bundle (ships the .NET runtime)

Publishing with a runtime identifier is self-contained by default (the GUI project sets `SelfContained` whenever `RuntimeIdentifier` is set), so the target host needs nothing installed.

1. Publish:
   ```powershell
   dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64
   ```
2. Copy the `publish/` folder and `NOTICE` into the hand-off folder.
3. Record SHA256 hashes for every staged file (`Get-FileHash -Algorithm SHA256`) in a `hashes.txt` beside them.
4. Verify launch on a clean Windows VM with no .NET runtime installed.

### Framework-dependent bundle (host runtime required)

1. Publish:
   ```powershell
   dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj `
     -c Release -r win-x64 `
     /p:SelfContained=false `
     /p:PublishSingleFile=true
   ```
2. Stage and hash the output as for the self-contained bundle.
3. Note in the release notes that the host must have the .NET 10 Desktop Runtime.

`driftbuster release stage-portable` produces a framework-dependent debug bundle under `artifacts/gui-packaging/portable/` with a zip and its `.sha256`; see `driftbuster release stage-portable --help`.

## Manual Smoke Checklist

- Follow `notes/checklists/gui-smoke.md` for the current walkthrough of every page, the right-click menus and Manage choices, in both themes.

## Headless UI Testing

- **Bootstrap**: `gui/DriftBuster.Gui.Tests/TestAppBuilder.cs` declares `[assembly: AvaloniaTestApplication]` and builds the real `App` with `.UseHeadless(...)`; `[AvaloniaFact]`/`[AvaloniaTheory]` run each test on the Avalonia dispatcher. Test parallelization is disabled assembly-wide.
- **tmux command shape**: Run GUI tests inside tmux to keep sessions responsive, e.g. `tmux new -d -s codexcli-ui 'dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj'`. Capture logs with `|& tee artifacts/<session>.log` when reproducing issues.
- **Focused filters**: Use `--filter 'FullyQualifiedName~MainWindowUiTests'` (or the other class names) for quick iteration, then finish with full Debug and Release passes (compiled XAML is on by default).

## Packaging & Distribution

- **Velopack installer** (the default)
  - `driftbuster release --runtime win-x64 --release-notes notes/releases/<semver>.md --installer-rid win-x64` builds it under `artifacts/velopack/releases/<rid>`; `--channel` sets the update channel label.
- **MSIX** (`scripts/package_msix.ps1`)
  - Delivers auto-updates and clean install/uninstall.
  - Needs a code-signing certificate and explicit capability declarations (file system access).
- **Portable ZIP**
  - Provide a zip archive with the executable and assets; supports offline admins.
- **Bundled runtime**
  - Ship the .NET runtime when targeting hosts without it (the default for a publish with a runtime identifier).
  - Record third-party notices alongside binaries.
- **Updates**
  - Velopack installs update from their channel; for portable bundles publish the checksum and version in the release notes.

## Evidence

Packaging evidence is captured per release, not kept in the repository: the publish transcript, the `hashes.txt` manifest and the commit hash travel with the bundle. `artifacts/gui-packaging/README.md` lists the commands that produce them.

## Distribution & Licensing Notes

- Maintain a `NOTICE` file covering .NET dependencies, Avalonia packages, and any auxiliary tooling, and copy it into every bundle.
- Avoid auto-downloading dependencies at runtime; ship vetted binaries to keep supply chain tight.
- Require an offline activation path so security teams can inspect builds before deployment.
- Generate a SHA256 manifest for every bundle and include it in the operator hand-off package.
- Record signing certificate details (thumbprint, expiry, issuer) in the legal review checklist.
- Provide certificate chain exports under `artifacts/gui-packaging/certificates/` so operators can import signing roots on isolated hosts before installing MSIX packages. Create the folder when it is first needed.

### Offline activation guidance

1. Stage the self-contained bundle (or the framework-dependent bundle plus the .NET 10 Desktop Runtime installer) on a removable drive; include `NOTICE` and the hash manifest so operators can audit contents offline.
2. On the target host, validate hashes with `Get-FileHash <file> -Algorithm SHA256` and compare against the recorded values before extracting or installing anything.
3. Extract the bundle into `C:\ProgramData\DriftBuster\App\` and ensure read/write permissions are limited to administrators.
4. Launch `DriftBuster.Gui.exe` once while offline and confirm the main window opens.
5. Document the activation steps in `notes/dev-host-prep.md` so subsequent operators can replay the process without re-downloading assets.

## Compliance & Accessibility Checklist

- Legal Guardrails: Never embed vendor logos or proprietary sample content; rely on neutral icons.
- Security: the GUI writes under the data root (the session, `Profiles/`, `cache/`, sanitized Diff planner history that never holds raw file contents, `curation.json`, `history.db` with masked values as fingerprints only, exports the user asks for, and logs), plus offline collector packages saved where the user chooses.
- Accessibility: Target keyboard navigation, high-contrast theme, and screen-reader labels for critical controls.
  1. Launch packaged build on Windows 11 VM with stable Narrator + Inspect versions logged in the accessibility evidence file.
  2. Start Narrator (`Win + Ctrl + Enter`) before opening the DriftBuster shell so focus events are captured from the first window.
  3. Tab through Setup, Compare, Files, File details and the dialogs; record any unlabeled controls or incorrect announcements.
  4. Run `inspect.exe` from the Windows SDK, attach to the DriftBuster window, and capture `Name`, `AutomationId`, and `HelpText` for critical controls.
  5. Switch to High Contrast mode (Windows Settings → Accessibility → Contrast Themes) and repeat Inspect sweeps to document contrast ratio readings.
  6. Store transcripts, tool versions, and screenshots in `artifacts/gui-accessibility/` for auditability.
- Privacy: Ensure redacted tokens remain masked in the UI and exports.
- Documentation: Keep safeguards aligned with `docs/legal-safeguards.md` when drafting user guidance.
