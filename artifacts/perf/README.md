# Performance Evidence Bundle

This directory captures performance harness outputs tied to the Performance & Async Stability work.

- `virtualization-baseline.json` — Recorded `PerformanceProfile.ShouldVirtualize` decisions for the default heuristics and the
  explicit force overrides. Use it as a quick reference when validating release notes or investigating overrides reported by
  operators.

Future perf smoke logs from `scripts/verify_coverage.sh --perf-smoke` should continue to land here so the release bundle has a
single location for virtualization and dispatcher metrics.
