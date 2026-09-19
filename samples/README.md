# DriftBuster Samples

This directory hosts ready-to-use configuration snippets for manual testing.
Pair them with the diff planner to explore baseline vs. variant
comparisons or feed them through hunt mode to exercise search rules.

## Layout

```
samples/
├─ configs/
│  ├─ appsettings.base.json
│  ├─ appsettings.dev.json
│  ├─ appsettings.prod.json
│  └─ web.release.config
└─ offline_runner/
   ├─ configs/
   └─ profiles/
```

* `appsettings.*.json` — three JSON snapshots showing gradual drift across
  environments (new logging levels, connection strings, feature toggles).
* `web.release.config` — a transform-style XML file tweaking runtime and module
  entries; compare it with `fixtures/config/web.config`.
* `offline_runner/` — profile and runner config samples for the offline
  collector; see `offline_runner/README.md`.

Drop additional environment versions in the same directory if you need to stage
custom diff sequences.
