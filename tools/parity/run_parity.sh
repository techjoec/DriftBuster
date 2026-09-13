#!/usr/bin/env bash
# Parity harness: runs the Python and C# dumps of one engine surface over fixtures/,
# tools/parity/cases/<surface>/ and a set of generated file-system cases (unreadable file,
# unreadable subdirectory, dangling symlink, missing root, unreadable root, tiny sampling
# budget) and diffs them. Exit 1 on any difference that is not an expected divergence (see
# expected_divergences.md). Temporary; deleted with the Python tree.
#
# Usage: tools/parity/run_parity.sh <surface> [dump args...]
#   surfaces: detect, decode
#   example:  tools/parity/run_parity.sh detect            (the full default registry on both sides)
#             tools/parity/run_parity.sh detect --plugins text
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

# compare <label> <root> [dump args...]: dump both sides, normalise the expected divergences, diff.
#   fix c: Python emits the MetadataValidationError next to the plugin's own match fields; the error is dropped from
#          the Python record and the catalog_* keys the port's catalog added are dropped from the C# record, so the
#          plugin output itself is still compared.
#   interpreter limits: Python emits "InterpreterLimit: ..." where json.loads exceeded a CPython limit; the path is
#          dropped from both sides (there is no Python match to compare) and the port record is asserted to be a
#          successful json match.
#   SQLite file names (plan decision 7): Python opens "file:{path}?mode=ro" as a URI, which decodes %XX and cuts the
#          path at ? or #, so such a file is never opened there; the port opens the real file. For those paths the
#          table_count key and the "Enumerated ..." reason are dropped from both records and the rest is compared.
#   Unicode 16.0 repr (runtime tables): registry-live stores str() of nested keyword and pattern items; Python 3.13
#          (Unicode 15.1) escapes code points assigned in 16.0 as non-printable, .NET 10 prints them. For a
#          registry-live record from a case whose file name contains "unicode16", keywords and patterns are dropped
#          from both records and the rest is compared.
UNICODE16_REPR='def unicode16_repr: .plugin == "registry-live" and (.path | test("unicode16"));
  def drop_unicode16_repr: if unicode16_repr then (.metadata |= del(.keywords, .patterns)) else . end;'
SQLITE_URI_NAMES='def sqlite_uri_name: .plugin == "binary-hybrid" and .format == "embedded-sql-db" and (.path | test("%[0-9A-Fa-f]{2}|[?#]"));
  def drop_sqlite_uri_facts: if sqlite_uri_name then (.metadata |= del(.table_count)) | (.reasons |= map(select(startswith("Enumerated ") | not))) else . end;'
compare() {
  local label="$1" root="$2"
  shift 2
  local tag py_out cs_out fixc_count limit_count sqlite_count unicode16_count count
  tag="$(echo "$label" | tr '/ ' '__')"
  py_out="$work/$tag.py.jsonl"
  cs_out="$work/$tag.cs.jsonl"

  # A dump that exits non-zero or prints something jq cannot parse fails this compare and the run continues.
  local rc
  rc=0
  PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py "$surface" "$root" "$@" > "$py_out" 2> "$work/$tag.py.err" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    status=1
    echo "FAIL $surface $label: python dump exited $rc"
    sed 's/^/     /' "$work/$tag.py.err" | tail -n 5
    return
  fi
  rc=0
  "$cli_exe" parity-dump "$surface" "$root" "$@" > "$cs_out" 2> "$work/$tag.cs.err" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    status=1
    echo "FAIL $surface $label: port dump exited $rc"
    sed 's/^/     /' "$work/$tag.cs.err" | tail -n 5
    return
  fi
  if ! jq -e -n '[inputs] | length >= 0' "$py_out" > /dev/null 2> "$work/$tag.py.err"; then
    status=1
    echo "FAIL $surface $label: python dump is not valid JSON lines"
    sed 's/^/     /' "$work/$tag.py.err" | tail -n 5
    return
  fi
  if ! jq -e -n '[inputs] | length >= 0' "$cs_out" > /dev/null 2> "$work/$tag.cs.err"; then
    status=1
    echo "FAIL $surface $label: port dump is not valid JSON lines"
    sed 's/^/     /' "$work/$tag.cs.err" | tail -n 5
    return
  fi
  if grep -q '"error": "DumpError' "$py_out"; then
    status=1
    echo "FAIL $surface $label: python dump could not serialise a record"
    grep '"error": "DumpError' "$py_out" | sed 's/^/     /'
  fi

  jq -n -c '[inputs | select(.error != null and (.error | startswith("MetadataValidationError"))) | .path]' "$py_out" > "$work/$tag.fixc.json"
  jq -n -c '[inputs | select(.error != null and (.error | startswith("InterpreterLimit"))) | .path]' "$py_out" > "$work/$tag.limit.json"
  jq -r '.[]' "$work/$tag.fixc.json" > "$work/$tag.fixc"
  jq -r '.[]' "$work/$tag.limit.json" > "$work/$tag.limit"
  jq -r "$SQLITE_URI_NAMES"' select(sqlite_uri_name) | .path' "$py_out" > "$work/$tag.sqlite"
  jq -r "$UNICODE16_REPR"' select(unicode16_repr) | .path' "$py_out" > "$work/$tag.unicode16"
  fixc_count="$(wc -l < "$work/$tag.fixc")"
  limit_count="$(wc -l < "$work/$tag.limit")"
  sqlite_count="$(wc -l < "$work/$tag.sqlite")"
  unicode16_count="$(wc -l < "$work/$tag.unicode16")"
  jq -c --slurpfile fixc "$work/$tag.fixc.json" --slurpfile limit "$work/$tag.limit.json" "$SQLITE_URI_NAMES$UNICODE16_REPR"'
    .path as $p
    | select(($limit[0] | index($p)) == null)
    | drop_sqlite_uri_facts
    | drop_unicode16_repr
    | if ($fixc[0] | index($p)) != null then del(.error) else . end' "$py_out" > "$work/$tag.py.filtered"
  jq -c --slurpfile fixc "$work/$tag.fixc.json" --slurpfile limit "$work/$tag.limit.json" "$SQLITE_URI_NAMES$UNICODE16_REPR"'
    .path as $p
    | select(($limit[0] | index($p)) == null)
    | drop_sqlite_uri_facts
    | drop_unicode16_repr
    | if ($fixc[0] | index($p)) != null and .metadata != null
      then .metadata |= with_entries(select(.key | startswith("catalog_") | not)) else . end' "$cs_out" > "$work/$tag.cs.filtered"
  # A CPython limit is only an expected divergence when the port parsed the file as JSON.
  jq -r --slurpfile limit "$work/$tag.limit.json" '
    .path as $p | select(($limit[0] | index($p)) != null and (.plugin != "json" or .error != null)) | .path' "$cs_out" > "$work/$tag.limit.bad"

  count="$(wc -l < "$py_out")"
  files_total=$((files_total + count))
  local divergences=$((fixc_count + limit_count + sqlite_count + unicode16_count))
  expected_total=$((expected_total + divergences))

  if diff -u "$work/$tag.py.filtered" "$work/$tag.cs.filtered" > "$work/$tag.diff" && [[ ! -s "$work/$tag.limit.bad" ]]; then
    echo "ok   $surface $label: $count files, $divergences expected divergences"
  else
    status=1
    echo "FAIL $surface $label: $count files, $divergences expected divergences"
    cat "$work/$tag.diff"
    sed 's/^/     port did not match json where python hit an interpreter limit: /' "$work/$tag.limit.bad"
  fi
  if [[ "$sqlite_count" -gt 0 ]]; then
    sed 's/^/     expected (plan decision 7, SQLite name read as a URI by Python, table_count not compared): /' "$work/$tag.sqlite"
  fi
  if [[ "$unicode16_count" -gt 0 ]]; then
    sed 's/^/     expected (runtime Unicode tables, repr of Unicode 16.0 code points; keywords and patterns not compared): /' "$work/$tag.unicode16"
  fi
  if [[ "$fixc_count" -gt 0 ]]; then
    sed 's/^/     expected (fix c, plugin output compared without catalog_* keys): /' "$work/$tag.fixc"
  fi
  if [[ "$limit_count" -gt 0 ]]; then
    sed 's/^/     expected (interpreter limit, port output not compared): /' "$work/$tag.limit"
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
  py_locked="$(PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py "$surface" "$gen/locked" "$@" || echo "python dump exited $?")"
  cs_locked="$("$cli_exe" parity-dump "$surface" "$gen/locked" "$@" || echo "port dump exited $?")"
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
