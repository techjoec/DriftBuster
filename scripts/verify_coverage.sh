#!/usr/bin/env bash
set -euo pipefail

show_help() {
  cat <<'USAGE'
Usage: scripts/verify_coverage.sh [--perf-smoke] [--perf-filter <expression>]

Runs the Python coverage gate (pytest, fail-under=90) and the .NET line
coverage gate (DOTNET_THRESHOLD, default 83). With --perf-smoke it also runs
the targeted performance smoke suite using the provided test filter
(default: Category=PerfSmoke).
USAGE
}

RUN_PERF_SMOKE=false
PERF_FILTER="Category=PerfSmoke"
DOTNET_THRESHOLD="${DOTNET_THRESHOLD:-83}"

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

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${repo_root}"

# dotnet lives behind the login profile; __JOE_PROFILE_ENV blocks .profile in child shells.
run_dotnet() {
  unset __JOE_PROFILE_ENV 2>/dev/null || true
  bash --login -c "dotnet $*"
}

echo "== DriftBuster: Verifying local coverage thresholds =="

echo "-- Python tests with coverage (fail-under=90)"
coverage run --source=src/driftbuster -m pytest -q
coverage report --fail-under=90
coverage json -o coverage.json

echo "-- .NET tests with line coverage threshold (${DOTNET_THRESHOLD}%)"
run_dotnet test -p:CollectCoverage=true \
  -p:Threshold="${DOTNET_THRESHOLD}" -p:ThresholdType=line -p:ThresholdStat=total \
  gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -v minimal

if [[ "${RUN_PERF_SMOKE}" == "true" ]]; then
  echo "-- Performance smoke suite (${PERF_FILTER})"
  mkdir -p artifacts/perf
  log_path="artifacts/perf/perf-smoke-$(date -u +"%Y%m%dT%H%M%SZ").log"
  echo "dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter \"${PERF_FILTER}\" --logger 'trx;LogFileName=PerfSmoke.trx' -v minimal" | tee "${log_path}"
  run_dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj \
    --filter "\"${PERF_FILTER}\"" \
    --logger "'trx;LogFileName=PerfSmoke.trx'" \
    -v minimal | tee -a "${log_path}"
  echo "Performance smoke log captured at ${log_path}"
fi

echo "== Coverage verification complete =="
