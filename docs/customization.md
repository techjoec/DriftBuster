# Customising DriftBuster

DriftBuster ships with sensible defaults, but you can adjust sampling,
registries, and output to suit local workflows. This guide covers the most
common tweaks.

## Adjust Sampling

```python
from driftbuster import Detector

detector = Detector(sample_size=256 * 1024)  # 256 KiB samples
result = detector.scan_file("web.config")
```

- `sample_size` controls how much content is read per file (default 128 KiB).
- Values above 512 KiB are clamped to protect memory usage.
- Supply `text_decoder` or `on_error` callbacks if you need custom decoding or
  error handling.

## Reorder or Extend Plugins

```python
from driftbuster import Detector, register
from driftbuster.formats.registry import get_plugins

class MyPlugin:
    name = "my-plugin"
    priority = 50
    version = "0.0.2"

    def detect(self, path, sample, text):
        return None

register(MyPlugin())
detector = Detector(plugins=get_plugins(), sort_plugins=True)
```

- `register` enforces unique plugin names. Declare a `version` string for
  documentation (`docs/format-support.md`).
- `sort_plugins=True` respects priority values; `False` keeps the order passed
  to the detector, which is useful when experimenting with overrides.
- Call `driftbuster.formats.registry_summary()` to confirm the final ordering
  before scanning.

## Combine with Profiles

```python
from driftbuster import Detector, ProfileStore

store = ProfileStore.from_dict({...})
detector = Detector()
results = detector.scan_with_profiles(
    "./deployments",
    profile_store=store,
    tags=["env:prod"],
)
```

- `scan_with_profiles` returns the detection alongside any matching profiles so
  you can log baselines during manual reviews.
- See `docs/profile-usage.md` for a short walkthrough or
  `docs/configuration-profiles.md` for the full API surface.

## CLI Options

```
python -m driftbuster.cli <path> --glob "**/*.config" --sample-size 262144 --json
```

- `--glob` narrows directory scans.
- `--sample-size` mirrors the Detector argument.
- `--json` streams newline-delimited JSON for pipelines or notebooks.

## Reporting Hooks

- `driftbuster.reporting.build_unified_diff` accepts a `redactor` or
  `mask_tokens` to scrub sensitive values before saving diffs.
- Pair canonicalised diffs with metadata from `summarise_metadata(match)` to
  provide context when sharing results.
