# Performance Check Calendar (A3.4.4)

Weekly perf sweeps run after coverage verification to ensure GUI heuristics stay honest once virtualization lands.

| Week of (UTC) | Trigger | Dataset / Scenario | Key Metrics | Evidence |
| --- | --- | --- | --- | --- |
| 2025-11-03 | `scripts/verify_coverage.sh --perf-smoke` | Headless toast burst (200 notifications) | Dispatcher posts: **2** (show + dismiss); Active: **3**; Overflow: **197** | `artifacts/perf/perf-smoke-20251103T000000Z.log` (capture after run) |
| 2025-11-10 | `scripts/verify_coverage.sh --perf-smoke --perf-filter "Category=PerfSmoke&FullyQualifiedName~Virtualization"` | Virtualized catalog (post-A3.2) | _TBD once virtualization heuristic lands_ | _Pending_ |

## Execution Notes

1. Run `scripts/verify_coverage.sh --perf-smoke` once per week (Mondays) immediately after the regular coverage sweep.
2. Archive the generated log under `artifacts/perf/` and update the table above with the exact filename plus metrics excerpt.
3. If heuristics change (threshold, force flags), document the new environment variable values alongside the metrics snapshot.
