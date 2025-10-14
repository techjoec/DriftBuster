# Legal review checklist

Use this log to document compliance passes for reporting artefacts and to track
follow-up questions.

## Scenario walkthroughs

| Date (UTC) | Scenario | JSON output | HTML output | Diff output | Notes |
|------------|----------|-------------|-------------|-------------|-------|
| 2025-09-26 | Mixed fixture scan masked tokens ``API_KEY``/``DB_PASSWORD`` using ``[REDACTED]`` placeholder. | Verified via `render_json_lines` with token list; no raw secrets present. | `render_html_report` emitted warning banner and redaction summary. | `render_unified_diff` replaced secrets in config drift hunk. | Snapshot manifest logged classification=internal-only and redacted tokens. |

## Sample review log

- Confirmed JSON/HTML/diff artefacts and snapshot manifest stored under the
  restricted `captures/2025-09-26-mixed-fixture/` directory.
- Documented retention window (30 days) and scheduled purge in personal task
  tracker.
- No additional redaction passes required.

## Outstanding legal questions

- [ ] Should the retention window adjust for investigations that span longer
      than 30 days while still honouring secure disposal expectations?
- [ ] Do we need extra disclaimers when including masked diffs in incident
      summaries?
