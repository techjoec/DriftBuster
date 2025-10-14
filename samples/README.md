# DriftBuster Samples

This directory hosts ready-to-use configuration snippets for manual testing and
demo runs. Pair them with the diff planner to explore baseline vs. variant
comparisons or feed them through hunt mode to exercise search rules.

## Layout

```
samples/
├─ configs/
│  ├─ appsettings.base.json
│  ├─ appsettings.dev.json
│  ├─ appsettings.prod.json
│  └─ web.release.config
```

* `appsettings.*.json` — three JSON snapshots showing gradual drift across
  environments (new logging levels, connection strings, feature toggles).
* `web.release.config` — a transform-style XML file tweaking runtime and module
  entries to contrast with the production baseline.

Drop additional environment versions in the same directory if you need to stage
custom diff sequences.
