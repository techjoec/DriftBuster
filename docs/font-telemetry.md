# Font Telemetry Staleness Guardrails

## Tuning quickstart

```mermaid
flowchart TD
    start([Start telemetry review]) --> check_age{Staleness flag triggered?}
    check_age -- "No" --> monitor[Continue monitoring and weekly review]
    check_age -- "Yes" --> confirm_clock{Clock skew detected?}
    confirm_clock -- "Yes" --> fix_clock[Align telemetry publisher clock]
    confirm_clock -- "No" --> inspect_runs{Recent runs missing?}
    inspect_runs -- "Yes" --> refill_runs[Backfill runs and rerun summary]
    inspect_runs -- "No" --> adjust_threshold{Override `--max-stale-hours`?}
    adjust_threshold -- "No" --> escalate[Escalate to platform owner with log bundle]
    adjust_threshold -- "Yes" --> verify_override[Run summary with new threshold]
    verify_override --> monitor
```

1. Start with the latest telemetry snapshot (default: `artifacts/logs/headless-font-health.json`).
2. If staleness triggers, verify clock alignment on the telemetry publisher first.
3. Confirm minimum run counts meet expectations before relaxing thresholds.
4. Keep overrides short-lived and record rationale in the backlog.

## CLI override scenarios

Use `scripts/font_health_summary.py` to inspect telemetry with explicit staleness windows.

```bash
python -m scripts.font_health_summary artifacts/logs/headless-font-health.json \
  --max-stale-hours 4
```

When validating a temporary extension alongside other tolerance changes:

```bash
python -m scripts.font_health_summary artifacts/logs/headless-font-health.json \
  --max-stale-hours 12 \
  --max-failure-rate 0.15 \
  --allow-last-failure
```

All overrides are logged in structured staleness events so reviewers can audit the context.

## Structured staleness events

Running the summary now writes JSON logs to `artifacts/logs/font-staleness/` (override with
`FONT_STALENESS_LOG_DIR` or `--log-dir`). Each file is named `font-staleness-<UTC timestamp>.json`
and includes:

- CLI guardrail configuration (`maxFailureRate`, `maxLastUpdatedAgeSeconds`, etc.).
- Scenario evaluations with status, issues, and computed `lastUpdatedAgeSeconds`.
- Missing scenario names and whether the run surfaced any issues.

Use `--max-log-files <count>` to prune older event files after each run when local storage or
artifact retention needs to stay within a fixed window. Passing `0` deletes the event log that was
just written (the aggregated summary remains) so that operators can rely solely on the summary
snapshot when necessary.

Include the latest JSON log when filing investigations so reviewers can replay configuration and
scenario metadata without rerunning telemetry.

The CLI also maintains an aggregated snapshot at `font-staleness-summary.json` inside the same log
directory (override with `--summary-path`; pass `-` to disable). When `--log-dir` is provided the
summary follows the override. The summary captures scenario status counts, highlights stale or
missing `lastUpdated` entries, and records the evaluated configuration so operators can monitor
drift at a glance.

## Troubleshooting cues

- Clock skew or pipeline stalls usually present with `lastUpdated` gaps exceeding the configured
  window while `totalRuns` stays flat.
- A missing `lastUpdated` field signals telemetry ingestion bugs; no override should be applied
  until the publisher is patched.
- Prefer smaller increments (1-2 hours) when extending `--max-stale-hours` to avoid masking
  genuine regressions.
