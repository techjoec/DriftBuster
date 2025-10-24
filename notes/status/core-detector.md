# Core detector sampling benchmarks

## Aggregate sampling guardrail

| Dataset | Files | Sample size | Budget | Files scanned before guardrail | Runtime (s) | Notes |
|---------|-------|-------------|--------|-------------------------------|-------------|-------|
| `/tmp/detector-benchmark` | 200 | 1,024 B | 16 KiB | 16 | 0.0093 | Benchmark plugin recorded metadata; guardrail warning emitted after `file0015.txt`. |

## Regression coverage

- `pytest tests/core/test_detector.py::test_scan_path_enforces_total_sample_budget`
- `pytest tests/multi_server/test_multi_server.py::test_multi_server_reports_sampling_guardrail`

## Follow-up

- Adjust budget via `Detector(max_total_sample_bytes=...)` if repositories require deeper sampling. The new `sample_budget_remaining` property can be logged during long-running scans.
