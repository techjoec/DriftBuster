# Python CLI Readiness Plan (Hold)

The CLI entry points remain paused while GUI-first work continues. This plan keeps
packaging prerequisites, manual validation steps, and open decisions aligned so
the CLI can resume without rediscovery.

## Packaging readiness checklist

- [x] Entry points exposed via `pyproject.toml` (`driftbuster`, `driftbuster-export-sql`).
- [x] Editable installs verified (`python -m pip install -e .`).
- [ ] Wheel build smoke test (`python -m build`) queued for activation phase.
- [ ] CLI help text finalisation pending UI/UX sign-off.
- [ ] Release notes template update blocked on packaging restart.

### Activation prerequisites

| Area | Requirement | Status |
| --- | --- | --- |
| Versioning | Align CLI version with core package semver when resumed. | Pending |
| Dependency audit | Re-run `pip-licenses` + `pip-audit` to refresh compliance notes. | Pending |
| Documentation | Publish CLI usage primer in README + docs before release. | Drafted |
| Packaging | Capture wheel + sdist artefacts and checksum logs under `artifacts/cli-plan/`. | Pending |

## Manual validation plan

### Command walkthroughs

1. **Detector scan (table)** — `python -m driftbuster.cli fixtures/config --glob "*.config"`
   - Confirms XML format detection and table renderer.
   - Expected output stored under `artifacts/cli-plan/README.md` (Detector scan section).
2. **Detector scan (JSON)** — `python -m driftbuster.cli fixtures/config --glob "*.config" --json`
   - Ensures JSON lines include metadata keys (`catalog_format`, `config_role`, etc.).
   - Reference transcript in `artifacts/cli-plan/README.md`.
3. **SQL export** — `python -m driftbuster.cli export-sql <sqlite>` with masking + hashing flags.
   - Validates manifest contents (`hashed_columns`, `masked_columns`, `row_counts`).
   - See `artifacts/cli-plan/README.md` for command + manifest excerpt.
4. **HTML snapshot renderer** — run the helper script documented below to produce sample HTML header.
   - Confirms renderer keeps dark theme + warning banner.
5. **Unified diff renderer** — call `driftbuster.reporting.diff.render_unified_diff` against config fixtures.
   - Expect transformer namespace addition on diff output as captured in transcripts.

### Expected outputs

- Table + JSON transcripts recorded in `artifacts/cli-plan/README.md` with exact metadata.
- HTML header snippet and diff excerpt included in the same transcript for quick comparison.
- SQL manifest structure recorded with placeholders to tolerate timestamp variance.

### Historical runs migrated

- 2025-10-17 JSON scan (former `notes/snippets/json-cli-run.md`) captured structured-settings JSON detection.
  - Metadata expectations: `top_level_type=json`, `top_level_keys`, `settings_hint`, `bytes_sampled≈377`.
- Profile summary helper (`notes/snippets/profile-summary-cli.py`) remains the reference for diff workflows; keep script in-place for manual diff rehearsals once CLI resumes.

## Decision log (open items)

| Topic | Decision | Date | Follow-up |
| --- | --- | --- | --- |
| Confidence threshold flag | Introduce `--min-confidence` (float, default 0.50) mapped to `Detector(min_confidence=…)`; clamp to [0,1], reject invalid values early. | 2025-10-23 | Add parser + validation during activation. |
| Progress indicator | Emit periodic `stderr` status (`Scanned N files…`) every 25 paths; keep optional `--no-progress` toggle for scripts. | 2025-10-23 | Prototype once packaging resumes; ensure quiet mode silences updates. |
| Packaging strategy | Resume with editable installs + wheel builds; defer PyPI upload until GUI backlog clears, but keep `dist/` wheel/SDist artifacts in release staging. | 2025-10-23 | Document final release checklist alongside packaging restart. |
| Decision timeline tracking | Re-evaluate readiness monthly while area A18 stays open; next checkpoint 2025-11-20 (align with GUI guardrail cadence). | 2025-10-23 | Update this table after each checkpoint. |

## Helper scripts

To reproduce the HTML + diff expectations:

```bash
python - <<'PY'
from pathlib import Path
from driftbuster.core.detector import Detector
from driftbuster.reporting.html import render_html_report
from driftbuster.reporting.diff import render_unified_diff

root = Path('fixtures/config')
detector = Detector()
matches = [match for _, match in detector.scan_path(root, glob='*.config') if match]
html = render_html_report(matches, title='CLI Sample Report')
Path('artifacts/cli-plan/sample-report.html').write_text(html, encoding='utf-8')

before = Path('fixtures/config/web.config').read_text(encoding='utf-8')
after = Path('fixtures/config/web.Release.config').read_text(encoding='utf-8')
diff = render_unified_diff(before, after, content_type='xml', from_label='web.config', to_label='web.Release.config')
Path('artifacts/cli-plan/sample.diff').write_text(diff, encoding='utf-8')
PY
```

This script mirrors the transcripts and stores reproducible artefacts under
`artifacts/cli-plan/` once packaging work resumes.
