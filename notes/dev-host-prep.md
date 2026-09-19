# Dev host prep (Avalonia GUI)

## Toolchain

- `dotnet --list-sdks` lists a 10.0 SDK (the solution targets `net10.0`).
- `pwsh --version` reports PowerShell 7.6 or later (Pester suites, PSScriptAnalyzer, packaging scripts).
- Set `AVALONIA_TELEMETRY_OPTOUT=1` for anything that builds or runs Avalonia.

## Restore and build

- `dotnet build DriftBuster.sln` restores and builds everything with zero warnings; analyzers are configured in
  `Directory.Build.props`.
- `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj` for ad-hoc validation after changes.

## Packaging

- `driftbuster release --runtime win-x64 --no-installer` runs the tests and publishes the self-contained CLI and GUI to
  `build/artifacts/{cli,gui}/win-x64`; add `--framework-dependent` for a build that needs the .NET runtime on the host.
- Drop `--no-installer` and pass `--release-notes notes/releases/<semver>.md --installer-rid win-x64` for the Velopack
  installer under `artifacts/velopack/releases/win-x64`.

## MSIX packaging checklist

- [ ] Confirm the Windows SDK App Packaging tools are installed (`makeappx.exe`, `signtool.exe`).
- [ ] Generate MSIX-ready icons under `gui/DriftBuster.Gui/Assets/Msix/` (Square150x150Logo.png, Square44x44Logo.png,
      StoreLogo.png, Wide310x150Logo.png).
- [ ] Pack and sign via `pwsh -NonInteractive -File scripts/package_msix.ps1 -Version <major.minor.patch.0> -CertificatePath <pfx> [-CertificatePassword (Read-Host -AsSecureString)]`.
- [ ] Archive the resulting `.msix`, `AppxManifest.xml` and PowerShell transcript into `artifacts/gui-packaging/msix/`.
- [ ] Record the SHA256 checksum next to the `.msix` and keep it with the release evidence.

The script keeps its staging layout at `artifacts/gui-packaging/msix/staging/` so checks on `App/AppxManifest.xml` are
repeatable before promoting the package.

## Manual verification

- GUI smoke pass: `notes/checklists/gui-smoke.md`.

## Lint and format

- `scripts/lint_all.sh` runs `dotnet format DriftBuster.sln --verify-no-changes` and `scripts/lint_powershell.ps1`
  (PSScriptAnalyzer over `cli/` and `scripts/`).
