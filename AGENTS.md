# DriftBuster Agent Notes for Codex
- No business talk or corporate process jargon.
- Skip stakeholders, sign-offs, reviews, and team references.
- No weeks, sprints, or sprawling documentation.
- No GitHub Actions/Runners.
- **Do not downgrade/reverse**: if you are having dep issues or other moderniztion-induced issues strive to resolve the issue to stay on the latest stable/mainstream versioning possible.

- Coverage baseline for all changes and new formats: keep line coverage at
  90% or higher. Enforce locally, not via CI. Verify with:
  - Python: `coverage run --source=src/driftbuster -m pytest -q && coverage report --fail-under=90`
  - .NET: `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total`
  - Summary: `python -m scripts.coverage_report`

- When adding a new format plugin, include tests that keep the plugin’s file
  coverage at or above 90% and update docs under `docs/` accordingly.
