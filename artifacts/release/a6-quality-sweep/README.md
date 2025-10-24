# A6 Quality Sweep Evidence Bundle

This bundle captures the release collateral gathered while closing out **A6. Quality Sweep & Release Prep (Phase 6)**.

## Evidence Index

| Evidence | Location | Notes |
| --- | --- | --- |
| Coverage summary (Python/.NET) | `../../coverage/final/coverage_summary.txt` | Confirms Python coverage at ≥98% with .NET coverage tracked via Cobertura export when available.
| Python HTML coverage report | `../../coverage/final/html/index.html` | Open locally to review per-module heatmaps verifying ≥90% coverage.
| Headless smoke telemetry | `../../logs/headless-font-health.json` | Aggregates GUI smoke counters after Avalonia 11.2 migration.
| Packaged multi-server storage smoke log | `../../logs/multi-server-storage/2025-10-24-smoke.log` | Captures cold/hot cache behaviour referenced in the GUI research notes for A6.2.1/A6.2.2.
| Perf baseline snapshot | `../../perf/baseline.json` | Provides performance baselines used during sweep sign-off.
| Manual walkthrough placeholder | `../manual-runs/2025-10-24-multi-server-notes.md` | Text notes plus pending screen capture link for A6.2.3.

## Retention Plan

- **Primary storage:** This bundle and linked evidence live under `artifacts/` in git for reproducibility.
- **Retention window:** Keep raw smoke/perf logs for 180 days post-release so regression triage has full history.
- **Rotation:** After 180 days, move logs to long-term archive storage, retaining hashes in `artifacts/logs/README.md`.
- **Access:** Evidence remains developer-accessible within the repository; sensitive paths are redaction-safe per `docs/legal-safeguards.md`.
- **Verification cadence:** Reconfirm evidence integrity (hash + readability) every release candidate freeze.

## Update Log

- 2025-10-24: Initial assembly for A6 release readiness (this change).
