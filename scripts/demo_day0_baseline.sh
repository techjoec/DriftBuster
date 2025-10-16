#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="samples/multi-server"
OUT_DIR="artifacts/day0-demo"

echo "== DriftBuster Day 0 Baseline Demo =="
echo "Scanning sample servers under: $ROOT_DIR"

# Optional: activate local venv if present
if [[ -f ".venv/bin/activate" ]]; then
  # shellcheck disable=SC1091
  source .venv/bin/activate
fi

rm -rf "$OUT_DIR" || true
mkdir -p "$OUT_DIR"

python -m scripts.create_day0_baseline \
  --root "$ROOT_DIR" \
  --output "$OUT_DIR" \
  --report

echo
echo "Artifacts:"
echo "- Baseline snapshot: $OUT_DIR/baseline_snapshot/"
echo "- Profile store:     $OUT_DIR/profile_store.day0.json"
echo "- Decisions audit:   $OUT_DIR/baseline.decisions.json"
echo "- HTML report:       $OUT_DIR/day0-baseline.html"
echo
echo "Next: Use the profile store for profile-aware scans or open the HTML report."

