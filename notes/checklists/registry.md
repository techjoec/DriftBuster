# Plugin registry smoke-test checklist

Use this when adding, removing or reordering format plugins. The ordering rules are in `docs/customization.md`.

- **Fixture mix:** the XML, JSON and binary samples from `notes/checklists/core-scan.md`.
- **Stub plugin:** a throwaway `IFormatPlugin` kept outside the repo (name `debug-registry`, a low `Priority`) that records
  each call and returns `null` except on the files it targets.
- **Ordering check:**
  - Register the stub into a fresh `new FormatRegistry()` before the built-ins; its `RegistrySummary()` lists
    `debug-registry` at order 0 (`Register` appends in call order).
  - `new Detector(DefaultPlugins.CreateBuiltIns().Prepend(new DebugPlugin()), sortPlugins: false)` keeps the order
    passed in; the stub runs first and wins on its target files.
- **Metadata spot-check:** after the stub declines a file, the XML plugin still fills `bytes_sampled` and `encoding`.
- **Error handling:** point the detector at an unreadable file; the scan raises `DetectorIOException` and the stub is
  never called for it.
- **Clean up:** drop the stub before longer scans. Ordering regressions are covered by
  `gui/DriftBuster.Backend.Tests/Detection/FormatRegistryTests.cs`.
