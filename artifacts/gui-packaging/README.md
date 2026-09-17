# GUI Packaging Evidence

Commands that produce the publish transcript and checksum for each Windows GUI packaging flow. Run them from the repository root with the .NET 10 SDK; keep the outputs with the release bundle.

## Self-contained build

Publishing with a runtime identifier is self-contained (the GUI project sets `SelfContained` whenever `RuntimeIdentifier` is set).

```bash
dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 \
  |& tee publish-self-contained.log
sha256sum gui/DriftBuster.Gui/bin/Release/net10.0/win-x64/publish/* > publish-self-contained.sha256
```

## Framework-dependent build

```bash
dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj -c Release -r win-x64 \
  /p:SelfContained=false \
  /p:PublishSingleFile=true \
  |& tee publish-framework-dependent.log
sha256sum gui/DriftBuster.Gui/bin/Release/net10.0/win-x64/publish/DriftBuster.Gui.exe > publish-framework-dependent.sha256
```

The host running a framework-dependent build needs the .NET 10 Desktop Runtime.

## Bundle

Archive the `publish/` folder with `NOTICE`, the checksum file and the commit hash (`git rev-parse HEAD`) for distribution.
