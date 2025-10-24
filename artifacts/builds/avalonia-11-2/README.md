# Avalonia 11.2 Release Build Evidence

This directory captures the Release-mode publish evidence for the Avalonia 11.2 GUI rebuild tracked under CLOUDTASKS area A5 (task 5.3).

## Reproduce

Run the publish command from the repository root to generate a framework-dependent single-file build and capture the transcript/checksum evidence:

```bash
dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj \
  -c Release -r win-x64 \
  /p:PublishSingleFile=true \
  /p:SelfContained=false \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  |& tee artifacts/builds/avalonia-11-2/publish-release.log

sha256sum gui/DriftBuster.Gui/bin/Release/net8.0/win-x64/publish/DriftBuster.Gui.exe \
  > artifacts/builds/avalonia-11-2/publish-release.sha256
```

## Contents

- `publish-release.log` — transcript from the Release-mode publish targeting win-x64.
- `publish-release.sha256` — SHA-256 checksum of the generated `DriftBuster.Gui.exe` single-file binary.
- `publish-release-manifest.json` — machine-readable manifest listing published files with their sizes and hashes.
- `README.md` — this overview.
