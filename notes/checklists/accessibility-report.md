# Accessibility evidence log

Use this ledger to track contrast audits, assistive technology walkthroughs,
and follow-up notes for the GUI shell.

## Audit entries

| Date (UTC) | Tools | Versions | Outcome | Notes |
|------------|-------|----------|---------|-------|
| 2025-10-24 | Inspect, Narrator, contrast probe script | Inspect 1.0.1.0; Narrator 2025.106.1 | Pass | Transcript validated via `python -m scripts.accessibility_summary`. Contrast ratios logged below and align with Dark+/Light+ palette review captured during the multi-server rehearsal. |

## Contrast ratios (2025-10-24)

- Dark+ text vs surface: 17.74:1
- Dark+ accent vs background: 5.25:1
- Light+ text vs surface: 17.85:1
- Light+ accent vs background: 4.95:1

## Manual validation notes (2025-10-24)

- Confirmed `artifacts/gui-accessibility/narrator-inspect-run-2025-02-14.txt` still
  covers every Narrator/Inspect checklist item via the summary script before
  filing this audit.
- Ran `PYTHONPATH=src scripts/smoke_multi_server_storage.sh` to rehearse cold/hot
  cache behaviour for the multi-server view and note the temporary session root
  used for theme toggle screenshots.
- Extracted `Palette.DarkPlus`/`Palette.LightPlus` accent and background tokens
  from `gui/DriftBuster.Gui/Assets/Styles/Theme.axaml` to document the Dark+/Light+
  toggle deltas alongside the manual run log.
