# Version Management

All component versions live in `versions.json`:

| Key | Versions | Written to |
| --- | --- | --- |
| `core` | Backend library and `driftbuster` console tool | `Directory.Build.props` (`DriftBusterCoreVersion`), the module manifest's `BackendVersion`, `CLA/INDIVIDUAL.md`, `CLA/ENTITY.md` |
| `catalog` | Detection catalog | `gui/DriftBuster.Backend/Detection/Catalog/DetectionCatalogData.cs`, the catalog version assertions in `CatalogTests.cs` and `DetectorTests.cs`, `docs/detection-types.md`, `notes/snippets/xml-config-diffs.md` |
| `gui` | Avalonia desktop app and its installer | `gui/GuiVersion.props` (`DriftBusterGuiVersion`) |
| `powershell` | PowerShell module | `cli/DriftBuster.PowerShell/DriftBuster.psd1` (`ModuleVersion`) |

Adjust the values that changed and run from the checkout:

```bash
dotnet run --project cli/DriftBuster.Cli -- version
```

The command propagates the numbers into the files above. A `versions.json`
without one of the four keys, or a replacement pattern that no longer matches
its file, stops the command with exit code 1 and a message naming the missing keys, or
the file and pattern, so you can correct the inputs before committing.

Format plugin versions are not in `versions.json`: each plugin declares its own
`Version` property (see `docs/format-support.md`).

The separation allows the GUI or PowerShell module to ship a new version
without touching the backend, and vice versa. Only bump the entries whose
implementation actually changed. `scripts/build_velopack_release.sh` reads the
`gui` version when `--version` is omitted, and
`scripts/package_powershell_module.ps1` checks that the manifest matches
`powershell` and `core`.
