# Diff Planner Validation — 2025-10-28

This folder records the validation artefacts for the MRU-enabled diff planner workflow.

## Commands executed

```
dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj
coverage run --source=src/driftbuster -m pytest -q
coverage report --fail-under=90
```

- `dotnet test` finished with all suites green (`Total tests: 312, Passed: 312, Failed: 0, Skipped: 0`).
- `coverage report` returned `TOTAL 95%` ensuring the ≥90% requirement is preserved after the full pytest sweep.

## Manual MRU replay checks

1. Launch the GUI with sanitized fixtures from `artifacts/samples/diff-planner/`.
2. Build a diff plan, toggle **Sanitized JSON**, and confirm the footer displays **Sanitized summary**.
3. Open **Recent plans** and replay the latest entry; ensure only digests and metadata appear.
4. Open **Manage saved plans…** to observe the on-disk cache directory and purge entries.

The session captured no raw payloads. See `artifacts/logs/diff-planner-mru-telemetry.json` for the structured log produced
by `DiffViewModel` when caching and replaying sanitized entries.

## Screenshot + asset notes

- Sanitized capture checklist documented in `docs/windows-gui-guide.md#sanitised-screenshot-capture`.
- Captures should be written to `docs/assets/diff-planner/` using `YYYYMMDD-mru-<theme>-<resolution>.png` once the screenshot
  harness is executed.

## Retention

- Retain this record for 30 days alongside telemetry evidence.
- Purge or rotate sanitized screenshots concurrently to keep cache entries aligned with MRU retention guidance.
