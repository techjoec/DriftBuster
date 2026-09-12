#!/usr/bin/env bash
# Parity harness: runs the Python and C# dumps of one engine surface over fixtures/,
# tools/parity/cases/<surface>/ and a set of generated file-system cases (unreadable file,
# unreadable subdirectory, dangling symlink, missing root, unreadable root, tiny sampling
# budget) and diffs them. Exit 1 on any difference that is not an expected divergence (see
# expected_divergences.md). Temporary; deleted with the Python tree.
#
# Usage: tools/parity/run_parity.sh <surface> [dump args...]
#   surfaces: detect, decode
#   example:  tools/parity/run_parity.sh detect --plugins text
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <surface> [dump args...]" >&2
  exit 2
fi

surface="$1"
shift
case "$surface" in
  detect|decode) ;;
  *)
    echo "error: unknown surface '$surface' (detect, decode)" >&2
    exit 2
    ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

run_dotnet() {
  unset __JOE_PROFILE_ENV 2>/dev/null || true
  bash --login -c "dotnet $*"
}

cli_exe="$repo_root/cli/DriftBuster.Cli/bin/Release/net10.0/driftbuster"
if [[ "${PARITY_SKIP_BUILD:-0}" != "1" || ! -x "$cli_exe" ]]; then
  echo "== building cli (Release)"
  run_dotnet build cli/DriftBuster.Cli/DriftBuster.Cli.csproj -c Release -v q --nologo >/dev/null
fi
if [[ ! -x "$cli_exe" ]]; then
  echo "error: built CLI not found at $cli_exe" >&2
  exit 2
fi

work="$(mktemp -d "${TMPDIR:-/tmp}/driftbuster-parity.XXXXXX")"
# Generated cases leave mode-000 entries behind; make them removable before deleting.
trap 'chmod -R u+rwX "$work" 2>/dev/null || true; rm -rf "$work"' EXIT

status=0
files_total=0
expected_total=0

# compare <label> <root> [dump args...]: dump both sides, drop fix-c paths, diff.
compare() {
  local label="$1" root="$2"
  shift 2
  local tag py_out cs_out expected_count count
  tag="$(echo "$label" | tr '/ ' '__')"
  py_out="$work/$tag.py.jsonl"
  cs_out="$work/$tag.cs.jsonl"

  PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py "$surface" "$root" "$@" > "$py_out"
  "$cli_exe" parity-dump "$surface" "$root" "$@" > "$cs_out"

  # Expected divergence (fix c): Python's strict catalog validation rejects output the port accepts.
  jq -n -c '[inputs | select(.error != null and (.error | startswith("MetadataValidationError"))) | .path]' "$py_out" > "$work/$tag.expected.json"
  jq -r '.[]' "$work/$tag.expected.json" > "$work/$tag.expected"
  expected_count="$(wc -l < "$work/$tag.expected")"
  for side in py cs; do
    jq -c --slurpfile skip "$work/$tag.expected.json" '.path as $p | select(($skip[0] | index($p)) == null)' "$work/$tag.$side.jsonl" > "$work/$tag.$side.filtered"
  done

  count="$(wc -l < "$py_out")"
  files_total=$((files_total + count))
  expected_total=$((expected_total + expected_count))

  if diff -u "$work/$tag.py.filtered" "$work/$tag.cs.filtered" > "$work/$tag.diff"; then
    echo "ok   $surface $label: $count files, $expected_count expected divergences"
  else
    status=1
    echo "FAIL $surface $label: $count files, $expected_count expected divergences"
    cat "$work/$tag.diff"
  fi
  if [[ "$expected_count" -gt 0 ]]; then
    sed 's/^/     expected (fix c): /' "$work/$tag.expected"
  fi
}

compare "fixtures" "fixtures" "$@"
if [[ -d "tools/parity/cases/$surface" ]]; then
  compare "tools/parity/cases/$surface" "tools/parity/cases/$surface" "$@"
fi

# Generated file-system cases (the entries cannot be committed: git tracks neither mode 000 nor
# dangling-target guarantees across checkouts).
gen="$work/generated"
mkdir -p "$gen/tree/readable-sub" "$gen/tree/locked-sub" "$gen/locked"
directives=$'alpha one\nbeta two\ngamma three\ndelta four\n'
printf '%s' "$directives" > "$gen/tree/readable.conf"
printf '%s' "$directives" > "$gen/tree/readable-sub/nested.conf"
printf '%s' "$directives" > "$gen/tree/locked-sub/hidden.conf"
printf '%s' "$directives" > "$gen/tree/unreadable.conf"
ln -s "$gen/tree/nowhere" "$gen/tree/dangling"
ln -s "$gen/tree/readable.conf" "$gen/tree/linkfile"
ln -s "$gen/tree/readable-sub" "$gen/tree/linkdir"
if [[ "$(id -u)" != "0" ]]; then
  chmod 000 "$gen/tree/unreadable.conf" "$gen/tree/locked-sub" "$gen/locked"
  compare "generated/tree (unreadable file and subdirectory, dangling link, linked file and directory)" "$gen/tree" "$@"
else
  echo "skip $surface generated/tree: running as root, permission cases are not observable"
fi
compare "generated/missing root" "$work/does-not-exist" "$@"
compare "generated/file root" "$gen/tree/readable.conf" "$@"
compare "generated/dangling root" "$gen/tree/dangling" "$@"
if [[ "$surface" == "detect" ]]; then
  # The built-in budget compares add their own --max-total-sample-bytes; a caller-supplied budget already
  # applies to every compare above, and the CLI rejects the option twice, so the built-ins are skipped.
  if [[ " $* " == *" --max-total-sample-bytes "* || " $* " == *" --max-total-sample-bytes="* || " $* " == *" --max-total-sample-bytes:"* ]]; then
    echo "skip $surface generated budget cases: --max-total-sample-bytes given on the command line"
  else
    compare "generated/tree with a 6-byte budget" "$gen/tree" "$@" --max-total-sample-bytes 6
    compare "tools/parity/cases/detect with a 4 KiB budget" "tools/parity/cases/detect" "$@" --max-total-sample-bytes 4096
  fi
fi

# Expected divergence (unreadable directory root): Python's glob swallows the PermissionError and
# scan_path returns [] silently; the port raises DetectorIOException for a root it cannot read.
if [[ "$(id -u)" != "0" ]]; then
  py_locked="$(PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py "$surface" "$gen/locked" "$@")"
  cs_locked="$("$cli_exe" parity-dump "$surface" "$gen/locked" "$@")"
  if [[ -z "$py_locked" && "$cs_locked" == '{"error": "DetectorIOError", "path": "."}' ]]; then
    echo "ok   $surface generated/unreadable root: expected divergence (python silent, port raises)"
    expected_total=$((expected_total + 1))
  else
    status=1
    echo "FAIL $surface generated/unreadable root: python [$py_locked] port [$cs_locked]"
  fi
fi

echo "== $surface summary: $files_total files compared, $expected_total expected divergences, $([[ $status -eq 0 ]] && echo PASS || echo FAIL)"
exit "$status"
