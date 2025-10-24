# Reporting adapter smoke outputs (2025-11-07)

Fixtures-only capture showing JSON lines, HTML summary, and diff patch redacted with the standard placeholder contract.

## Commands

```bash
PYTHONWARNINGS=ignore python -m driftbuster.cli fixtures/config --glob "*.config" --json > artifacts/reporting/2025-11-07/config-scan.jsonl
PYTHONWARNINGS=ignore python -m driftbuster.cli diff fixtures/config/web.config fixtures/config/web.Release.config \
  --mask-token Primary --mask-token Release --context-lines 2 > artifacts/reporting/2025-11-07/web-config.patch
python - <<'PY'  # HTML summary generator
from pathlib import Path
from driftbuster.core.detector import Detector
from driftbuster.reporting.html import render_html_report
from driftbuster.reporting.diff import build_unified_diff
from driftbuster.hunt import HuntHit, HuntRule

root = Path('fixtures/config').resolve()
matches = [match for _, match in Detector().scan_path(root) if match]
masked_diff = build_unified_diff(
    Path('fixtures/config/web.config').read_text(encoding='utf-8'),
    Path('fixtures/config/web.Release.config').read_text(encoding='utf-8'),
    content_type='xml',
    from_label='web.config',
    to_label='web.Release.config',
    label='web.config ↔ web.Release.config',
    mask_tokens=('Primary', 'Release'),
)
hunt_rule = HuntRule(
    name='ConnectionString placeholder',
    description='Detects sample placeholders to confirm masking contract.',
    token_name='database_server',
)
hunt_hit = HuntHit(
    rule=hunt_rule,
    path=Path('fixtures/config/appsettings.json'),
    line_number=9,
    excerpt='"DefaultConnection": "Server={{ database_server }};Database=Sample;"',
    matches=('{{ database_server }}', 'Sample'),
)
html = render_html_report(
    matches,
    title='Reporting adapters smoke capture',
    diffs=[masked_diff],
    hunt_hits=[hunt_hit],
    mask_tokens=('Primary', 'Release', 'Sample', 'team-alpha', 'team-bravo'),
    extra_metadata={'run_id': 'reporting-smoke-2025-11-07', 'source': str(root)},
)
Path('artifacts/reporting/2025-11-07/report.html').write_text(html, encoding='utf-8')
PY
```

## Redaction proof

- The HTML report banner includes the redaction badge and summary counts for the masked tokens.
- `web-config.patch` replaces both the baseline and transform values with `[REDACTED]` while retaining the diff context.
- `config-scan.jsonl` relies on catalog metadata only; no raw secrets are present in the sanitized fixtures.

To regenerate, rerun the commands above on a clean checkout so auditors can diff the outputs against these snapshots.
