#!/usr/bin/env bash
set -euo pipefail

show_help() {
  cat <<'USAGE'
Usage: scripts/verify_coverage.sh [--perf-smoke] [--perf-filter <expression>]

Runs the standard coverage sweep (pytest + Avalonia tests) and, when the
optional perf flag is supplied, executes the targeted performance smoke
suite with the provided test filter (default: Category=PerfSmoke).
USAGE
}

RUN_PERF_SMOKE=false
PERF_FILTER="Category=PerfSmoke"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --perf-smoke)
      RUN_PERF_SMOKE=true
      shift
      ;;
    --perf-filter)
      if [[ $# -lt 2 ]]; then
        echo "error: --perf-filter expects a value" >&2
        exit 1
      fi
      PERF_FILTER="$2"
      shift 2
      ;;
    -h|--help)
      show_help
      exit 0
      ;;
    *)
      echo "error: unknown argument '$1'" >&2
      show_help
      exit 1
      ;;
  esac
done

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

if [[ "${RUN_PERF_SMOKE}" == "true" ]]; then
  echo "-- Performance smoke suite (${PERF_FILTER})"
  mkdir -p artifacts/perf
  log_path="artifacts/perf/perf-smoke-$(date -u +"%Y%m%dT%H%M%SZ").log"
  echo "dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter \"${PERF_FILTER}\" --logger 'trx;LogFileName=PerfSmoke.trx' -v minimal" | tee "${log_path}"
  dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj \
    --filter "${PERF_FILTER}" \
    --logger "trx;LogFileName=PerfSmoke.trx" \
    -v minimal | tee -a "${log_path}"
  echo "Performance smoke log captured at ${log_path}"
fi

echo "== Coverage verification complete =="

