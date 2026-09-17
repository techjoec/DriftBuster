#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA_ROOT="$(mktemp -d)"
export DRIFTBUSTER_DATA_ROOT="$DATA_ROOT"
CACHE_DIR="$DATA_ROOT/cache/diffs"
SESSION_DIR="$DATA_ROOT/sessions"

cleanup() {
  rm -rf "$DATA_ROOT"
}
trap cleanup EXIT

printf 'Using temporary data root: %s\n' "$DATA_ROOT"

# DRIFTBUSTER_CLI points at a driftbuster console executable; without it the CLI project is built and its output used.
if [[ -n "${DRIFTBUSTER_CLI:-}" ]]; then
  CLI="$DRIFTBUSTER_CLI"
else
  if ! command -v dotnet >/dev/null 2>&1; then
    PATH="$(env -u __JOE_PROFILE_ENV bash --login -c 'printf %s "$PATH"')"
    export PATH
  fi
  printf 'Building the driftbuster console tool...\n'
  dotnet build "$ROOT_DIR/cli/DriftBuster.Cli/DriftBuster.Cli.csproj" -c Release -v quiet -nologo >/dev/null
  CLI="$ROOT_DIR/cli/DriftBuster.Cli/bin/Release/net10.0/driftbuster"
fi

run_plan() {
  "$CLI" multi-server <<JSON | tee "$DATA_ROOT/last-run.json" >/dev/null
{
  "plans": [
    {
      "host_id": "server01",
      "label": "Baseline",
      "scope": "custom_roots",
      "roots": ["${ROOT_DIR}/fixtures/multi-server/server01"],
      "baseline": {"is_preferred": true, "priority": 10}
    },
    {
      "host_id": "server02",
      "label": "Drift sample",
      "scope": "custom_roots",
      "roots": ["${ROOT_DIR}/fixtures/multi-server/server02"],
      "baseline": {"is_preferred": false, "priority": 5}
    }
  ]
}
JSON
}

printf 'Running cold multi-server plan...\n'
run_plan

if [[ ! -d "$CACHE_DIR" ]] || [[ -z "$(ls -A "$CACHE_DIR" 2>/dev/null)" ]]; then
  printf 'Cache directory %s missing or empty after first run.\n' "$CACHE_DIR" >&2
  exit 1
fi

printf 'Cache directory populated: %s\n' "$CACHE_DIR"

printf 'Running hot multi-server plan...\n'
run_plan

export SMOKE_LAST_RUN="$DATA_ROOT/last-run.json"

# The last stdout line is {"type": "result", "payload": response}; the hot run must reuse at least one cache entry.
if ! tail -n 1 "$SMOKE_LAST_RUN" | jq -e '[.payload.results[]? | select(.used_cache == true)] | length > 0' >/dev/null; then
  printf 'Cached run did not report cache reuse.\n' >&2
  exit 1
fi

printf 'Hot run reused cache entries.\n'

printf 'Session directory (if GUI runs) would live at: %s\n' "$SESSION_DIR"
printf 'Smoke test complete. Remove trap to keep data root if you want to inspect artefacts.\n'
