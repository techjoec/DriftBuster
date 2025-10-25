# Vendor Sample Fixtures

These fixtures provide anonymised configuration samples used when rehearsing
vendor integrations during compliance and coverage reviews.

## Files

| File | Format | Scenario | Sanitisation summary |
| ---- | ------ | -------- | -------------------- |
| `telemetry_collector_sample.json` | JSON | Retention + telemetry ingest configuration exercising log pruning flags. | Derived from public retention checklist guidance; replaced hostnames with `example.invalid` domains and swapped tokens for environment placeholders. |
| `identity_directory_sample.yaml` | YAML | Directory export sync covering incremental windows and anonymised identifiers. | Mirrored from open standards payload structure with organisation references removed and hashing guidance inlined. |

Both fixtures avoid real vendor identifiers and follow the guardrails documented in
`docs/legal-safeguards.md#vendor-sample-handling`.
