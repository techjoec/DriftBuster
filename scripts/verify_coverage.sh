#!/usr/bin/env bash
set -euo pipefail

echo "== DriftBuster: Verifying local coverage thresholds =="

# Activate local venv if present (optional)
if [[ -f ".venv/bin/activate" ]]; then
  # shellcheck disable=SC1091
  source .venv/bin/activate
fi

echo "-- Python tests with coverage (fail-under=90)"
coverage run --source=src/driftbuster -m pytest -q
coverage report --fail-under=90
coverage json -o coverage.json

echo "-- .NET tests with line coverage threshold (90%)"
dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj \
  -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total \
  --collect:"XPlat Code Coverage" \
  --results-directory artifacts/coverage-dotnet -v minimal

echo "-- Repo-wide coverage summary"
python -m scripts.coverage_report || true

echo "== Coverage verification complete =="

