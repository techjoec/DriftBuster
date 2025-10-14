# Core detector scan checklist

Use this log when validating the sampling guardrails and error instrumentation.
Attach timestamps and notes directly in the tables.

## Fixture runs

| Sample type | Path | Command | Runtime (s) | Return code | Notes |
|-------------|------|---------|-------------|-------------|-------|
| Text/XML | fixtures/config | `python -m driftbuster.cli fixtures/config` | 0.6 | 0 | Output archived in `notes/snippets/xml-cli-run-2025-10-14.txt`. |
| JSON | fixtures/config/appsettings.json | `PYTHONPATH=src python -m driftbuster.cli fixtures/config/appsettings.json` | 0.3 | 0 | Output archived in `notes/snippets/json-cli-run.md`. |
| Binary | | | | | |

## Metadata snapshot

Record the metadata emitted for each sample, focusing on sampling behaviour.

| Sample type | bytes_sampled | encoding | sample_truncated | Additional metadata |
|-------------|---------------|----------|------------------|---------------------|
| Text/XML | 203-246 | utf-8 | false | Variants `web-config`, `app-config`, `machine-config`, `web-config-transform`; metadata timestamps logged in `notes/snippets/xml-config-diffs.md`. |
| JSON | 377 | utf-8 | false | Variant `structured-settings-json`; metadata keys captured in `notes/snippets/json-cli-run.md`. |
| Binary | | | | |

## Profile scan smoke

Run this snippet when profile helpers change to confirm
`Detector.scan_with_profiles` resolves configs without raising errors. Record
the console output and file path alongside the fixture log.

```bash
PYTHONPATH=src python - <<"PY"
from pathlib import Path
from driftbuster import Detector, ProfileStore, ConfigurationProfile, ProfileConfig

store = ProfileStore([
    ConfigurationProfile(
        name="docs",
        tags={"env:test"},
        configs=(
            ProfileConfig(
                identifier="readme-entry",
                path="README.md",
            ),
        ),
    )
])

detector = Detector()
results = detector.scan_with_profiles(
    Path("README.md"),
    profile_store=store,
    tags=["env:test"],
)

entry = results[0]
print("profiles", [cfg.config.identifier for cfg in entry.profiles])
print("detection", entry.detection)
PY
```

## Manual lint + style results

- [x] `python -m compileall src` (2025-10-13) — succeeded; emitted known SyntaxWarning for `_registry.py` string escape.
- [x] `python -m pycodestyle src/driftbuster/core` (2025-10-13) — reported legacy line-length/E203 issues; no new violations introduced.
- [x] `python -m pycodestyle src/driftbuster/formats/registry.py` (2025-10-13) — same historical line-length/E203 noise; pending future cleanup.

## Profile-assisted smoke

```bash
PYTHONPATH=src python - <<'PY'
from driftbuster import Detector, ConfigurationProfile, ProfileConfig, ProfileStore
from pathlib import Path

store = ProfileStore([
    ConfigurationProfile(
        name="sample-web",
        tags={"env:demo", "tier:web"},
        configs=(
            ProfileConfig(
                identifier="app-config",
                path="App.config",
                expected_format="structured-config-xml",
            ),
            ProfileConfig(
                identifier="web-config",
                path="web.config",
                expected_variant="web-config",
            ),
        ),
    ),
])

detector = Detector()
results = detector.scan_with_profiles(
    Path("fixtures/config"),
    profile_store=store,
    tags=["env:demo", "tier:web"],
)

for entry in results[:3]:
    print(entry.path.name, bool(entry.detection), [cfg.config.identifier for cfg in entry.profiles])
PY
```

Output:

```
App.config True ['app-config']
machine.config True []
web.Release.config True []
```

## Unreadable file simulation

1. Create a temporary fixture, then run ``chmod 000 <file>``.
2. Attempt to scan the file and capture the raised ``DetectorIOError``.
3. Restore permissions afterwards.

Expected message template:

```
DetectorIOError(path='path/to/file', reason='[Errno 13] Permission denied: ...')
```

Add any deviations or follow-up tasks below.

- Notes:
  - 2025-10-13: Verified structured-config variants via `Detector().scan_file` against temporary `/tmp/driftbuster-samples/*.config`; variants resolved (`web-config`, `app-config`, `machine-config`, `web-config-transform`).
