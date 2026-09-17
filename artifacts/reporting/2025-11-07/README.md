# Reporting adapter smoke outputs (2025-11-07)

Fixtures-only capture showing JSON lines, HTML summary, and diff patch redacted with the standard placeholder contract.

## Regenerate

Run from the repository root with the console tool (`dotnet run --project cli/DriftBuster.Cli -- <command>` from source):

```bash
driftbuster scan fixtures/config --glob "*.config" --json > artifacts/reporting/2025-11-07/config-scan.jsonl
driftbuster diff fixtures/config/web.config fixtures/config/web.Release.config \
  --mask-token Primary --mask-token Release --context-lines 2 > artifacts/reporting/2025-11-07/web-config.patch
driftbuster report fixtures/config --format html --title 'Reporting adapters smoke capture' \
  --mask-token Primary --mask-token Release --mask-token Sample --mask-token team-alpha --mask-token team-bravo \
  --output artifacts/reporting/2025-11-07/report.html
```

## Redaction proof

- The HTML report banner includes the redaction badge and summary counts for the masked tokens.
- `web-config.patch` replaces both the baseline and transform values with `[REDACTED]` while retaining the diff context.
- `config-scan.jsonl` relies on catalog metadata only; no raw secrets are present in the sanitized fixtures.

Rerun the commands above on a clean checkout so auditors can diff the outputs against these snapshots.
