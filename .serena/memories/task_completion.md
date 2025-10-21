# Task Completion Checklist
- Run relevant Python tests with coverage (`coverage run ... && coverage report --fail-under=90`) for any Python changes.
- If GUI/backend touched, execute `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj` and confirm thresholds pass.
- Validate formatting: `python -m pycodestyle src` for Python; `dotnet format ... --verify-no-changes` for each impacted .NET project.
- For PowerShell edits, run `pwsh scripts/lint_powershell.ps1`.
- Review `python -m scripts.coverage_report` for combined summary when both stacks change.
- Update relevant docs under `docs/` if behavior, formats, or workflows shift.
- Ensure no GitHub Actions or external automation files were added inadvertently.