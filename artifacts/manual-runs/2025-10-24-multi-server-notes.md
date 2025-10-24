# Multi-Server Manual Walkthrough — 2025-10-24

- **Build under test:** GUI package from Avalonia 11.2 sweep (`artifacts/builds/avalonia-11-2/`).
- **Command:** `./scripts/smoke_multi_server_storage.sh`
- **Focus areas:** persistence of run profiles, diff planner cache reuse, theme toggles.

## Observations
1. Cold start initialised storage under `artifacts/tmp/A6-2025-10-24T13-07-41Z` and registered four diff planner datasets.
2. Restart sequence preserved six run profiles (`storage/db/main.sqlite`) and diff planner deltas remained identical.
3. Manual theme flip between `Palette.DarkPlus` and `Palette.LightPlus` persisted across restart.
4. No alerts fired; toast queue drained in <50 ms matching perf baseline snapshot.

## Artifacts
- Log transcript: `artifacts/logs/multi-server-storage/2025-10-24-smoke.log`.
- Screen capture: _pending export_ (`TODO: attach MP4 when encoding completes`).
- Notes mirrored in `notes/releases/next.md` for release bundle reference.
