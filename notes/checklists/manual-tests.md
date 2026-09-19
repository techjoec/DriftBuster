# Manual sample checklist

Use this log to coordinate sample retrieval, mutation runs, and hunt/profile
verification. Mirror the sample entries from `docs/testing-strategy.md` so each
row links to a concrete test action.

## Sample inventory snapshot

| Format | Reference | Local path (do not commit) | Sanitisation state | Reviewer |
|--------|-----------|----------------------------|--------------------|----------|
| XML configuration | Public policy template (generic XML) | fixtures/config/*.config (sanitised repo copy) | ☑ Ingested ☑ Sanitised ☑ Verified (2025-10-14) | Manual 2025-10-14 |
| JSON telemetry | Open audit log example | ~/driftbuster-samples/json/audit.json | ☐ Ingested ☐ Sanitised ☐ Verified | |
| Binary blob | Public CA metadata slice | ~/driftbuster-samples/binary/ca-clip.der | ☐ Ingested ☐ Sanitised ☐ Verified | |

- Mark each checkbox inline as you complete sanitisation. Leave comments if a
  sample cannot be cleared and note the blocking condition.
- Store remediation notes in the "Anomalies" section below and cross-reference
  the row.

### 2025-10-14 XML config spot-check

- `driftbuster scan fixtures/config --json`
  - Output archived in `notes/snippets/xml-cli-run-2025-10-14.txt`.
- Baseline and transform metadata snapshots refreshed in
  `notes/snippets/xml-config-diffs.md` (2025-10-14 run).

## Hunt/profile verification steps

1. Load the XML and JSON samples into the hunt workflow. Confirm detector
   matches include catalog identifiers and `sample_truncated` flags when
   applicable.
2. Run profile generation for the binary sample and capture metadata deltas.
   Record whether truncation thresholds align with `Detector.DefaultSampleSize`.
3. Run `scripts/lint_all.sh` to confirm the sample changes did not break
   formatting or the PowerShell analyzers.
4. Attach CLI output snippets directly beneath the relevant table row. Keep
   timestamps and redacted paths where necessary.
5. Bridge hunt output with profile expectations using `driftbuster
   detection-profile hunt-bridge profiles.json hunt-results.json --tag
   env:prod --tag tier:web --root deployments/prod-web-01 --output
   hunt-profile-bridge.json`. Log the output path.

## Manual fuzz execution log

| Sample | Mutation description | Command(s) | Outcome | Follow-up |
|--------|----------------------|------------|---------|-----------|
| | | | | |

- Use deterministic mutation scripts referenced in the testing strategy doc.
- If a mutation fails, capture stack traces here and note the remediation task
  in the issue tracker.

## GUI research review log

| Date | Reviewer | Notes |
|------|----------|-------|

## Anomalies & remediation

| Date | Sample | Issue | Immediate action | Deferred follow-up |
|------|--------|-------|------------------|--------------------|
| | | | | |

- Link back to `docs/legal-safeguards.md` when legal review is required.
- Flag any data retention or distribution concerns before storing outputs. The
  legal checklist in `notes/checklists/legal-review.md` must be run prior to
  sharing derived artifacts.
