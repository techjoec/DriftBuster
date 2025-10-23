# GUI Packaging Evidence

This directory captures publish transcripts and checksums for the Windows GUI packaging flows validated on 2025-10-25.

## Reproduce

1. Ensure the .NET 8 SDK is installed and run commands from the repository root.
2. Framework-dependent build (portable ZIP target):
   ```bash
   dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj \
     -c Release -r win-x64 \
     /p:PublishSingleFile=true \
     /p:SelfContained=false \
     /p:IncludeNativeLibrariesForSelfExtract=true \
     |& tee artifacts/gui-packaging/publish-framework-dependent.log
   sha256sum gui/DriftBuster.Gui/bin/Release/net8.0/win-x64/publish/DriftBuster.Gui.exe \
     > artifacts/gui-packaging/publish-framework-dependent.sha256
   ```
3. Self-contained build:
   ```bash
   dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj \
     -c Release -r win-x64 \
     /p:PublishSingleFile=true \
     /p:SelfContained=true \
     |& tee artifacts/gui-packaging/publish-self-contained.log
   sha256sum gui/DriftBuster.Gui/bin/Release/net8.0/win-x64/publish/DriftBuster.Gui.exe \
     > artifacts/gui-packaging/publish-self-contained.sha256
   ```
4. Stage the WebView2 Evergreen offline installer beside the publish output and archive the complete folder for distribution.

## Contents

- `publish-framework-dependent.log` — transcript for the framework-dependent publish.
- `publish-framework-dependent.sha256` — checksum of the resulting single-file executable.
- `publish-self-contained.log` — transcript for the self-contained publish.
- `publish-self-contained.sha256` — checksum of the self-contained single-file executable.
- `framework-evaluation-2025-10-25.json` — structured summary of the framework decision matrix for area A19.1, including packaging and licensing guardrails.
- `README.md` — this guide.
