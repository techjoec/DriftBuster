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

run_plan() {
  python -m driftbuster.multi_server <<JSON | tee "$DATA_ROOT/last-run.json" >/dev/null
{
  "plans": [
    {
      "host_id": "server01",
      "label": "Baseline",
      "scope": "custom_roots",
      "roots": ["${ROOT_DIR}/samples/multi-server/server01"],
      "baseline": {"is_preferred": true, "priority": 10}
    },
    {
      "host_id": "server02",
      "label": "Drift sample",
      "scope": "custom_roots",
      "roots": ["${ROOT_DIR}/samples/multi-server/server02"],
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

if ! python - <<PY
import json
import os
from pathlib import Path

lines = [line.strip() for line in Path(os.environ["SMOKE_LAST_RUN"]).read_text(encoding="utf-8").splitlines() if line.strip()]
payload = json.loads(lines[-1]).get("payload", {}) if lines else {}
results = payload.get("results", [])
if not any(result.get("used_cache") for result in results):
    raise SystemExit("cached run did not report used_cache=true")
PY
then
  printf 'Cached run did not report cache reuse.\n' >&2
  exit 1
fi

printf 'Hot run reused cache entries.\n'

printf 'Session directory (if GUI runs) would live at: %s\n' "$SESSION_DIR"
printf 'Smoke test complete. Remove trap to keep data root if you want to inspect artefacts.\n'
