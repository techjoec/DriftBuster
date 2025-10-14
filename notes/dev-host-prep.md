# Dev Host Prep Log (Avalonia GUI)

## Toolchain Verification

- `dotnet --list-sdks`
  - Result: `8.0.119 [/usr/lib/dotnet/sdk]`
- `python --version`
  - Result: `Python 3.12.3`
- `pip --version`
  - Result: `pip 24.0 (python 3.12)`

> Host already satisfied the .NET 8 + Python 3.10+ prerequisite checklist.

## Package Restore

- `dotnet restore /tmp/DrB/DriftBuster-GUI-Base/gui/DriftBuster.Gui/DriftBuster.Gui.csproj`
  - Needed extended timeout (120 s) on first run.
  - Warning: `FluentAvaloniaUI (>= 2.1.2)` not found; NuGet resolved `2.2.0` automatically.

## Initial Build Attempt

- `dotnet build -c Debug …`
  - Failed: missing `InitializeComponent`, missing `Avalonia.Fonts.Inter`, empty `Assets/app.ico`, and XAML compile errors because compiled bindings lacked `x:DataType` definitions.

### Actions Taken During Integration

- Added explicit package references (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Diagnostics`) pinned to **11.2.0** so the GUI aligns with the latest supported toolchain.
- Dropped FluentAvalonia in favour of a standard Avalonia `Window`, simplifying the red/black layout while keeping dependencies lean.
- Disabled compiled bindings by setting `<AvaloniaUseCompiledBindingsByDefault>false</…>` and wiring manual `InitializeComponent` loaders in each view.
- Generated a 32x32 DrB icon (`gui/DriftBuster.Gui/Assets/app.ico`) via a custom script and re-enabled `<ApplicationIcon>`.
- Created `notes/dev-host-prep.md` to capture every command/result and keep future prep predictable.

## Current Status (2025-10-13)

- `dotnet restore gui/DriftBuster.Gui/DriftBuster.Gui.csproj` → succeeds with Avalonia 11.2.0 packages.
- `dotnet build -c Debug gui/DriftBuster.Gui/DriftBuster.Gui.csproj` → succeeds; outputs under `gui/DriftBuster.Gui/bin/Debug/net8.0/`.
- Outstanding follow-up: none. The host is primed for `dotnet run` and future publish steps.

## Packaging Notes

- `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false /p:IncludeNativeLibrariesForSelfExtract=true`
  - Produces a portable zip-friendly build that depends on the host .NET 8 runtime.
- `dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true`
  - Emits a self-contained bundle (~120 MB) suitable for offline installs.
- Both publish flavours expect a Python runtime alongside the executable (ship an embedded venv or instruct users to point the GUI at an existing interpreter).

## Manual Verification Snapshot

- GUI smoke test documented in `notes/checklists/gui-smoke.md` (ping core, diff two samples, hunt directory, observe error handling, quit → verify backend exits).
- Use `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj` for ad-hoc validation after changes.
