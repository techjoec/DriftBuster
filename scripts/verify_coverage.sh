#!/usr/bin/env bash
set -euo pipefail

show_help() {
  cat <<'USAGE'
Usage: scripts/verify_coverage.sh [--perf-smoke] [--perf-filter <expression>]

Runs the Backend, CLI and GUI test projects with coverlet.msbuild, merges their
reports and enforces the total line coverage threshold on the merged result
(DOTNET_THRESHOLD, default 83). When pwsh is on PATH it also runs the Pester
suites for the PowerShell module and the offline runner. With --perf-smoke it
also runs the targeted performance smoke suite using the provided test filter
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

# dotnet may live behind the login profile; __JOE_PROFILE_ENV blocks .profile in child shells.
# The PATH is taken from a login shell once so dotnet (and the Pester suites, which call it) resolve directly.
if ! command -v dotnet >/dev/null 2>&1; then
  PATH="$(env -u __JOE_PROFILE_ENV bash --login -c 'printf %s "$PATH"')"
  export PATH
fi
export AVALONIA_TELEMETRY_OPTOUT=1

echo "== DriftBuster: Verifying local coverage thresholds =="

echo "-- .NET tests with merged line coverage threshold (${DOTNET_THRESHOLD}%)"
coverage_dir="${repo_root}/build/coverage"
rm -rf "${coverage_dir}"
mkdir -p "${coverage_dir}"

# Each run merges the previous json report; the last one writes the merged report and enforces the threshold on it.
dotnet test gui/DriftBuster.Backend.Tests/DriftBuster.Backend.Tests.csproj -v minimal \
  -p:CollectCoverage=true -p:CoverletOutputFormat=json \
  -p:CoverletOutput="${coverage_dir}/backend.json"
dotnet test cli/DriftBuster.Cli.Tests/DriftBuster.Cli.Tests.csproj -v minimal \
  -p:CollectCoverage=true -p:CoverletOutputFormat=json \
  -p:MergeWith="${coverage_dir}/backend.json" -p:CoverletOutput="${coverage_dir}/cli.json"
dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -v minimal \
  -p:CollectCoverage=true -p:CoverletOutputFormat=json%2Ccobertura \
  -p:MergeWith="${coverage_dir}/cli.json" -p:CoverletOutput="${coverage_dir}/merged/" \
  -p:Threshold="${DOTNET_THRESHOLD}" -p:ThresholdType=line -p:ThresholdStat=total

if command -v pwsh >/dev/null 2>&1; then
  echo "-- Pester: PowerShell module and offline runner"
  pwsh -NoProfile -NonInteractive -Command '
    $ErrorActionPreference = "Stop"
    if (-not (Get-Module -ListAvailable -Name Pester | Where-Object { $_.Version.Major -ge 5 })) {
      throw "Pester 5 or later is required. Install via Install-Module Pester."
    }
    Import-Module Pester -MinimumVersion 5.0
    $config = New-PesterConfiguration
    $config.Run.Path = @("cli/DriftBuster.PowerShell.Tests/DriftBuster.PowerShell.Tests.ps1", "scripts/DriftBusterOfflineRunner.Tests.ps1")
    $config.Run.Exit = $true
    $config.Output.Verbosity = "Normal"
    Invoke-Pester -Configuration $config
  '
else
  echo "-- Pester skipped: pwsh not found on PATH"
fi

if [[ "${RUN_PERF_SMOKE}" == "true" ]]; then
  echo "-- Performance smoke suite (${PERF_FILTER})"
  mkdir -p artifacts/perf
  log_path="artifacts/perf/perf-smoke-$(date -u +"%Y%m%dT%H%M%SZ").log"
  echo "dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter \"${PERF_FILTER}\" --logger 'trx;LogFileName=PerfSmoke.trx' -v minimal" | tee "${log_path}"
  dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj \
    --filter "${PERF_FILTER}" \
    --logger 'trx;LogFileName=PerfSmoke.trx' \
    -v minimal | tee -a "${log_path}"
  echo "Performance smoke log captured at ${log_path}"
fi

echo "== Coverage verification complete =="
