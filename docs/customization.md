# Customising DriftBuster

DriftBuster ships with sensible defaults, but you can adjust sampling, plugin
ordering, and output to suit local workflows. This guide covers the most common
tweaks.

## Console Options

```sh
driftbuster scan <path> --glob "**/*.config" --sample-size 262144 --json
```

- `--glob` narrows directory scans (default `**/*`). Segments split on `/`,
  `**` is zero or more directory levels, and every other segment is matched
  against one entry name: within a segment `*` matches any run of characters in
  that name (never crossing `/`), `?` one character, and every other character —
  brackets and backslash included — is literal. Matching ignores case on Windows
  only. Symlinked directories are listed but not descended into.
- `--sample-size` sets the bytes read per file (default 128 KiB).
- `--json` streams newline-delimited JSON for pipelines.
- `driftbuster hunt` and `driftbuster capture run` take the same `--glob` and
  `--sample-size` options; `capture run` adds `--hunt-glob` and
  `--hunt-exclude` for the hunt pass.

## Adjust Sampling in Code

```csharp
using DriftBuster.Backend.Detection;

var detector = new Detector(sampleSize: 256 * 1024);  // 256 KiB samples
var match = detector.ScanFile("web.config");
```

- `sampleSize` controls how much content is read per file (default
  `Detector.DefaultSampleSize`, 128 KiB).
- Values above `Detector.MaxSampleSize` (512 KiB) are clamped with a warning.
- `maxTotalSampleBytes` caps the bytes sampled across a whole scan (default
  16 MiB); `SampleBudgetExhausted` reports when a scan hit it.
- `onError` receives read failures before they are raised; `onWarning`
  receives guardrail warnings.

## Reorder or Extend Plugins

```csharp
using DriftBuster.Backend.Detection;

var plugins = DefaultPlugins.CreateBuiltIns().Append(new MyPlugin());
var detector = new Detector(plugins, sortPlugins: true);

sealed class MyPlugin : IFormatPlugin
{
    public string Name => "my-plugin";
    public string Version => "0.1.0";
    public int Priority => 50;
    public DetectionMatch? Detect(string path, byte[] sample, string? text) => null;
}
```

- `FormatRegistry.Register` enforces unique plugin names. Declare a `Version`
  string and record it in `docs/format-support.md`.
- `sortPlugins: true` orders plugins by `Priority`; `false` keeps the order
  passed to the detector, which is useful when experimenting with overrides.
- `FormatRegistry.RegistrySummary()` reports the final ordering of a registry.

## Combine with Profiles

```csharp
using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Profiles.Detection;

var store = DetectionProfileStore.Load("profiles.json");
var results = new Detector().ScanWithProfiles("./deployments", store, tags: ["env:prod"]);
```

- `ScanWithProfiles` returns each detection alongside the profile configs that
  apply to it so you can log baselines during manual reviews.
- See `docs/profile-usage.md` for a short walkthrough or
  `docs/configuration-profiles.md` for the data model.

## Reporting and Redaction

- `driftbuster diff --mask-token <value> --placeholder <text>` scrubs values
  before diffs are written; `--content-type` picks the canonicalisation.
- `driftbuster report --mask-token <value>` redacts HTML and JSON lines reports.
- Pair diffs with the detection metadata from `driftbuster scan --json` to
  provide context when sharing results.
