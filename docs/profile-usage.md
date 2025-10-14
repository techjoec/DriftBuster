# Profile Usage Quick Start

Profiles describe the configuration files you expect to see. Use this guide to
get started quickly; refer to `docs/configuration-profiles.md` for the full
reference.

## 1. Define a Profile

```python
from driftbuster import ConfigurationProfile, ProfileConfig, ProfileStore

store = ProfileStore([
    ConfigurationProfile(
        name="prod-web",
        tags={"env:prod", "tier:web"},
        configs=(
            ProfileConfig(
                identifier="web-config",
                path="web/web.config",
                expected_format="structured-config-xml",
            ),
            ProfileConfig(
                identifier="appsettings",
                path="app/appsettings.json",
                expected_format="json",
            ),
        ),
    )
])
```

- Use `identifier` for stable diffs.
- Store additional tags in `tags` or helper fields such as `application`.

## 2. Run a Profile-Aware Scan

```python
from driftbuster import Detector

detector = Detector()
results = detector.scan_with_profiles(
    "./deployments/prod-web-01",
    profile_store=store,
    tags=["env:prod", "tier:web", "application:inventory"],
)

for hit in results:
    print(hit.path, hit.detection and hit.detection.format_name)
    for prof in hit.profiles:
        print("  matched profile:", prof.profile.name, "->", prof.config.identifier)
```

- `scan_with_profiles` returns detections plus the profiles that apply to the
  supplied tag set.
- Use `ProfileStore.matching_configs(...)` when you only need profile entries
  for a path/tag combination.

## 3. Persist and Diff

```python
summary = store.summary()
updated = store.update_profile("prod-web", lambda prof: prof)

baseline = store.summary()
current = updated.summary()
from driftbuster.core.profiles import diff_summary_snapshots
diff = diff_summary_snapshots(baseline, current)
```

- `ProfileStore.to_dict()` / `from_dict()` help load and store JSON fixtures.
- Use `python -m driftbuster.profile_cli summary profiles.json` to generate a
  snapshot from the command line, or `profile_cli diff` to compare summaries.

## Next Steps

- Represent dynamic values alongside profiles with hunt metadata (see
  `docs/hunt-mode.md`).
- Explore the entire API surface in `docs/configuration-profiles.md`.
