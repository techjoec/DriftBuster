# Font telemetry backlog (A0g+ follow-up)

## Outstanding telemetry gaps

- Capture publisher uptime metrics to correlate with `lastUpdated` gaps.
- Expand fixture coverage for multi-scenario feeds (blocked on new data exports).
- Align alert routing with observability stack (pending Ops hand-off).

## Owner expectations

- Rotation lead reviews telemetry health every Monday and records notes in this file.
- On-call engineer triages new staleness alerts within 2 hours using the latest structured log.
- Pipeline maintainer owns fixes for ingestion gaps and reports status during the weekly sync.
- Any run that enables `--max-log-age-hours` must capture the chosen window and follow-up retention
  plan in the next review entry.

## Review cadence

- Structured log schema review: first Wednesday of each month.
- CLI guardrail settings audit: align with release readiness checkpoints.
- Fixture refresh: regenerate snapshots quarterly or when schema changes land.
