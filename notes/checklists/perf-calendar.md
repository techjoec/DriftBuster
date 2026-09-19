# Performance check calendar

Weekly perf sweeps run after coverage verification to keep the GUI's virtualization and toast heuristics honest.

| Week of (UTC) | Trigger | Dataset / Scenario | Key Metrics | Evidence |
| --- | --- | --- | --- | --- |
| 2025-11-03 | `scripts/verify_coverage.sh --perf-smoke` | Headless toast burst (200 notifications) | Dispatcher posts: **2** (show + dismiss); Active: **3**; Overflow: **197** | Log kept with the release evidence (`artifacts/` is not tracked) |

## Execution Notes

1. Run `scripts/verify_coverage.sh --perf-smoke` once per week (Mondays) immediately after the regular coverage sweep. It runs
   the `Category=PerfSmoke` tests in `gui/DriftBuster.Gui.Tests/Ui/PerformanceSmokeTests.cs`; `--perf-filter` narrows it.
2. The run writes `artifacts/perf/perf-smoke-<UTC stamp>.log`; add a row above with the filename and a metrics excerpt.
3. If heuristics change (threshold, force flags), record the new environment variable values alongside the metrics.
