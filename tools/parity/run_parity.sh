#!/usr/bin/env bash
# Parity harness: runs the Python and C# dumps of one engine surface and diffs them. detect and
# decode run over fixtures/, tools/parity/cases/<surface>/ and a set of generated file-system cases
# (unreadable file, unreadable subdirectory, dangling symlink, missing root, unreadable root, tiny
# sampling budget, a FIFO, a socket and a device link); diff, canon, hunt and secrets run over the inputs listed in
# run_phase4_surface. Exit 1 on any difference that is not an expected divergence (see
# expected_divergences.md). Temporary; deleted with the Python tree.
#
# Usage: tools/parity/run_parity.sh <surface> [dump args...]
#   surfaces: detect, decode, diff, canon, hunt, secrets
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
  detect|decode|diff|canon|hunt|secrets) ;;
  *)
    echo "error: unknown surface '$surface' (detect, decode, diff, canon, hunt, secrets)" >&2
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
# Seconds before a port dump is killed (a walk blocked on a FIFO, say) and before py_dump.py gives up on one secrets file (fix g).
port_timeout="${PARITY_PORT_TIMEOUT:-600}"
secrets_timeout="${PARITY_SECRETS_TIMEOUT:-5}"

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
  timeout "$port_timeout" "$cli_exe" parity-dump "$surface" "$root" "$@" > "$cs_out" 2> "$work/$tag.cs.err" || rc=$?
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

# ---------------------------------------------------------------------------------------------------------------------
# Phase 4 surfaces: diff, canon, hunt, secrets. Each compare dumps both sides with identical arguments, validates the JSON
# lines, applies the surface's divergence normaliser to both records and diffs; a failing compare is reported and the
# run continues.
#
#   fix d (XML namespace prefixes; diff and canon): for a record whose content type is xml and whose canonical text on
#          either side declares a namespace, every xmlns / xmlns:p attribute is dropped from both sides, and each prefix
#          Python chose (ns0, ns1, ... or a well-known prefix such as xsi) is renamed to the prefix the port kept for the
#          same URI (the source prefix; none for a default namespace), and the two canonical texts are compared (when neither
#          side clamped them; a clamped text is compared on the canon surface). Everything after canonicalisation (redaction,
#          the diff, stats, clamps, digests, the summary and key order) is recomputed by py_dump.py from the port's own
#          canonical texts (PARITY_PORT_CANONICAL, read with the port's canon surface) and compared exactly. Because that text
#          normaliser cannot see where declarations sit, tools/parity/xml_semantics.py also parses every xml canonical text
#          on both sides (unnormalised) and compares the element trees by expanded name ({uri}local): a declaration on the
#          wrong element, a missing xmlns="" undeclaration or an unbound prefix fails the compare. The same script asserts the fix
#          itself: each namespaced port canonical text uses exactly the qualified names written in its source document.
#   fix e (install-path drive pattern; hunt): the Python dump runs with PARITY_HUNT_FIX_E=1 (default_rules() with the
#          port's single-escaped drive pattern) and every record is compared exactly. A second, stock Python dump must equal
#          the fixed one with install-path records removed from both, and the install-path lines that differ between the two
#          are counted as expected divergences; for the tools/parity/cases/hunt compare, the port is asserted to find the
#          intended Windows path, and stock Python not to, at each entry of INSTALL_PATH_EXPECTATIONS.
#   interpreter errors (diff): Python's clamps and digests encode strictly, so a canonical text or diff holding an unpaired
#          surrogate raises UnicodeEncodeError (py_dump.py emits "InterpreterError: ..."); the pair is not compared and the port
#          record is asserted to be a successful result.
#   fix g (redaction loop; secrets): py_dump.py gives each file PARITY_SECRETS_TIMEOUT seconds. secrets_guard.py replaces each
#          record Python never finished with its reference model's record (CPython re, the guard budget rule), which must carry
#          a redaction guard, and checks the model equals every Python record that did return; the port is then compared
#          exactly, redaction_guard included.
# ---------------------------------------------------------------------------------------------------------------------
NS_DEFS='
def py_prefixes: [scan("xmlns:([^=\\s\"]+)=\"([^\"]*)\"")] | reduce .[] as $m ({}; . + {($m[0]): $m[1]});
def port_uris: [scan("xmlns(?::([^=\\s\"]+))?=\"([^\"]*)\"")] | reduce .[] as $m ({}; if has($m[1]) then . else . + {($m[1]): ($m[0] // "")} end);
def drop_decls: gsub(" xmlns(?::[^=\\s\"]+)?=\"[^\"]*\""; "");
def rename($py; $port): ($py | to_entries) as $e
  | reduce range(0; $e | length) as $i (.; gsub("(?<pre>[<\\s/])" + $e[$i].key + ":"; "\(.pre)\u0001\($i)\u0002"))
  | reduce range(0; $e | length) as $i (.; ($port[$e[$i].value] // $e[$i].key) as $t
      | gsub("\u0001\($i)\u0002"; if $t == "" then "" else $t + ":" end))
  | drop_decls;
def declares_ns: test(" xmlns(:|=)");
def unclamped($other): (.result.safety_limits.canonical // null) == null and ($other.result.safety_limits.canonical // null) == null;
def fixd_diff($other):
  (.result.content_type == "xml")
  and ([.result.canonical_before, .result.canonical_after, $other.result.canonical_before, $other.result.canonical_after]
       | map(. // "" | declares_ns) | any);
def interpreter_error: (.error // "") | startswith("InterpreterError");
def normalise_diff($side; $other; $recomputed):
  if $side == "py" and interpreter_error then {pair, interpreter_error: true}
  elif $side == "cs" and ($other | interpreter_error) and (.result | type) == "object" and .error == null then {pair, interpreter_error: true}
  elif (.result | type) == "object" and fixd_diff($other) then
    if $side == "py" then
      (.result.canonical_before | py_prefixes) as $pb | (.result.canonical_after | py_prefixes) as $pa
      | ($other.result.canonical_before | port_uris) as $cb | ($other.result.canonical_after | port_uris) as $ca
      | (if unclamped($other) then {before: (.result.canonical_before | rename($pb; $cb)), after: (.result.canonical_after | rename($pa; $ca))}
         else null end) as $canonical
      | $recomputed + {fixd_canonical: $canonical}
    else
      . + {fixd_canonical: (if unclamped($other) then {before: (.result.canonical_before | drop_decls), after: (.result.canonical_after | drop_decls)}
                            else null end)}
    end
  else . end;
def fixd_canon($other):
  .content_type == "xml" and ([.canonical, $other.canonical] | map(. // "" | declares_ns) | any);
def normalise_canon($side; $other):
  if fixd_canon($other) then
    if $side == "py" then (.canonical | py_prefixes) as $p | ($other.canonical | port_uris) as $c | .canonical |= rename($p; $c)
    else .canonical |= drop_decls end
  else . end;
'

# Hunt cases where the port must find the Windows install path Python's double-escaped pattern misses (fix e):
# "<relative_path>\t<line_number>\t<plan_transform value>".
INSTALL_PATH_EXPECTATIONS=(
  $'install-windows-single.ini\t2\tC:\\Program Files'
  $'install-windows-mixed.txt\t1\tD:\\Apps'
)

# dump_both <tag> <args...>: runs both dumps into $work/<tag>.{py,cs}.jsonl; returns 1 (after reporting) on a failed or
# unparsable dump.
dump_both() {
  local tag="$1" rc side
  shift
  rc=0
  PARITY_HUNT_FIX_E="$([[ "$surface_cmd" == hunt ]] && echo 1 || echo 0)" \
    PARITY_SECRETS_TIMEOUT="$([[ "$surface_cmd" == secrets ]] && echo "$secrets_timeout" || echo 0)" PYTHONPATH="$repo_root/src" \
    python tools/parity/py_dump.py "$surface_cmd" "$@" > "$work/$tag.py.jsonl" 2> "$work/$tag.py.err" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    echo "FAIL $surface $tag: python dump exited $rc"
    sed 's/^/     /' "$work/$tag.py.err" | tail -n 5
    return 1
  fi
  rc=0
  timeout "$port_timeout" "$cli_exe" parity-dump "$surface_cmd" "$@" > "$work/$tag.cs.jsonl" 2> "$work/$tag.cs.err" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    echo "FAIL $surface $tag: port dump exited $rc"
    sed 's/^/     /' "$work/$tag.cs.err" | tail -n 5
    return 1
  fi
  for side in py cs; do
    if ! jq -e -n '[inputs] | length >= 0' "$work/$tag.$side.jsonl" > /dev/null 2> "$work/$tag.$side.err"; then
      echo "FAIL $surface $tag: $side dump is not valid JSON lines"
      sed 's/^/     /' "$work/$tag.$side.err" | tail -n 5
      return 1
    fi
  done
  if grep -q '"error": "DumpError' "$work/$tag.py.jsonl"; then
    echo "FAIL $surface $tag: python dump could not serialise a record"
    return 1
  fi
}

# recompute_fixd <tag> <diff args...>: fix d. For every record the diff normaliser treats as namespaced xml, reads the port's
# canonical texts of the pair with the port's canon surface and reruns the Python dump with PARITY_PORT_CANONICAL, so
# $work/<tag>.pyport.jsonl holds CPython's redaction, difflib, stats, clamps and summary over the port's canonical texts.
recompute_fixd() {
  local tag="$1" pair before after side rc
  shift
  jq -r -n --slurpfile other "$work/$tag.cs.jsonl" "$NS_DEFS"'
    foreach inputs as $r (-1; . + 1; . as $i | $r | select((.result | type) == "object" and fixd_diff($other[$i] // {})) | .pair)' \
    "$work/$tag.py.jsonl" > "$work/$tag.fixd.pairs"
  if [[ ! -s "$work/$tag.fixd.pairs" ]]; then
    cp "$work/$tag.py.jsonl" "$work/$tag.pyport.jsonl"
    return 0
  fi
  : > "$work/$tag.portcanon.jsonl"
  while IFS= read -r pair; do
    before="${pair%%$'\t'*}"
    after="${pair#*$'\t'}"
    for side in before after; do
      rc=0
      timeout "$port_timeout" "$cli_exe" parity-dump canon "${!side}" --content-type xml > "$work/$tag.canon-$side.jsonl" 2>> "$work/$tag.cs.err" || rc=$?
      if [[ "$rc" -ne 0 || "$(wc -l < "$work/$tag.canon-$side.jsonl")" != "1" ]] \
          || ! jq -e 'has("canonical")' "$work/$tag.canon-$side.jsonl" > /dev/null; then
        echo "FAIL $surface $tag: port canon of ${!side} did not give one canonical text (fix d recompute)"
        return 1
      fi
    done
    jq -c -n --arg pair "$pair" --slurpfile b "$work/$tag.canon-before.jsonl" --slurpfile a "$work/$tag.canon-after.jsonl" \
      '{pair: $pair, canonical_before: $b[0].canonical, canonical_after: $a[0].canonical}' >> "$work/$tag.portcanon.jsonl"
  done < "$work/$tag.fixd.pairs"
  rc=0
  PARITY_PORT_CANONICAL="$work/$tag.portcanon.jsonl" PYTHONPATH="$repo_root/src" \
    python tools/parity/py_dump.py diff "$@" > "$work/$tag.pyport.jsonl" 2>> "$work/$tag.py.err" || rc=$?
  if [[ "$rc" -ne 0 || "$(wc -l < "$work/$tag.pyport.jsonl")" != "$(wc -l < "$work/$tag.py.jsonl")" ]]; then
    echo "FAIL $surface $tag: python dump over the port's canonical texts failed (fix d recompute)"
    return 1
  fi
}

# compare_records <label> <surface command> <normaliser: none|diff|canon|hunt|secrets> <args...>
compare_records() {
  local label="$1" normaliser="$3" tag count divergences side other dropped
  surface_cmd="$2"
  shift 3
  tag="$(echo "$surface_cmd-$label" | tr '/ ' '__')"
  if ! dump_both "$tag" "$@"; then
    status=1
    return
  fi
  if [[ "$normaliser" == "diff" ]] && ! recompute_fixd "$tag" "$@"; then
    status=1
    return
  fi
  : > "$work/$tag.timeout"
  if [[ "$normaliser" == "secrets" ]]; then
    jq -r 'select(.error == "Timeout") | .path' "$work/$tag.py.jsonl" > "$work/$tag.timeout"
    # fix g: every file Python never finished is replaced by secrets_guard.py's reference model record (which must carry a
    # redaction guard), and the model must equal every Python record that did return.
    local ruleset_arg=() previous=""
    for argument in "$@"; do
      [[ "$previous" == "--ruleset" ]] && ruleset_arg=(--ruleset "$argument")
      previous="$argument"
    done
    if ! PYTHONPATH="$repo_root/src" python tools/parity/secrets_guard.py "$work/$tag.py.jsonl" "$1" "${ruleset_arg[@]}" \
        > "$work/$tag.pymodel.jsonl" 2> "$work/$tag.guard"; then
      status=1
      echo "FAIL $surface $label: fix g reference model"
      head -c 4000 "$work/$tag.guard"
      echo
      return
    fi
  fi
  for side in py cs; do
    other=$([[ "$side" == py ]] && echo cs || echo py)
    case "$normaliser" in
      diff)
        jq -n -c --arg side "$side" --slurpfile other "$work/$tag.$other.jsonl" --slurpfile recomputed "$work/$tag.pyport.jsonl" "$NS_DEFS"'
          foreach inputs as $r (-1; . + 1; . as $i | $r | normalise_diff($side; ($other[$i] // {}); ($recomputed[$i] // {})))' \
          "$work/$tag.$side.jsonl" > "$work/$tag.$side.filtered"
        ;;
      canon)
        jq -n -c --arg side "$side" --slurpfile other "$work/$tag.$other.jsonl" "$NS_DEFS"'
          foreach inputs as $r (-1; . + 1; . as $i | $r | normalise_canon($side; ($other[$i] // {})))' \
          "$work/$tag.$side.jsonl" > "$work/$tag.$side.filtered"
        ;;
      secrets)
        jq -c . "$work/$tag.$([[ "$side" == py ]] && echo pymodel || echo cs).jsonl" > "$work/$tag.$side.filtered"
        ;;
      *)
        cp "$work/$tag.$side.jsonl" "$work/$tag.$side.filtered"
        ;;
    esac
  done
  count="$(wc -l < "$work/$tag.py.jsonl")"
  files_total=$((files_total + count))
  # A normaliser that failed would leave both filtered files empty and the diff silent.
  dropped="$(wc -l < "$work/$tag.timeout")"
  for side in py cs; do
    if [[ "$(wc -l < "$work/$tag.$side.filtered")" != "$(wc -l < "$work/$tag.$side.jsonl")" ]]; then
      status=1
      echo "FAIL $surface $label: the $normaliser normaliser dropped $side records"
      return
    fi
  done
  if [[ "$normaliser" == "diff" || "$normaliser" == "canon" ]]; then
    local semantics_root=()
    [[ "$normaliser" == "canon" ]] && semantics_root=(--root "$1")
    if ! python tools/parity/xml_semantics.py "$normaliser" "$work/$tag.py.jsonl" "$work/$tag.cs.jsonl" "${semantics_root[@]}" > "$work/$tag.semantics" 2>&1; then
      status=1
      echo "FAIL $surface $label: xml canonical texts differ by expanded name or do not keep the source prefixes"
      head -c 4000 "$work/$tag.semantics"
      echo
    fi
  fi
  if [[ "$normaliser" == "hunt" ]]; then
    if ! PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py hunt "$@" > "$work/$tag.pystock.jsonl" 2> "$work/$tag.pystock.err"; then
      status=1
      echo "FAIL $surface $label: stock python hunt dump failed"
      return
    fi
    if ! diff -q <(jq -c 'select(.rule.name != "install-path")' "$work/$tag.pystock.jsonl") \
                 <(jq -c 'select(.rule.name != "install-path")' "$work/$tag.py.jsonl") > /dev/null; then
      status=1
      echo "FAIL $surface $label: fix e changed records other than install-path hits"
    fi
  fi
  case "$normaliser" in
    diff)
      divergences="$(jq -n --slurpfile other "$work/$tag.cs.jsonl" "$NS_DEFS"'
        [foreach inputs as $r (-1; . + 1; . as $i | $r | select(interpreter_error or ((.result | type) == "object" and fixd_diff($other[$i] // {}))) | .pair)]
        | length' "$work/$tag.py.jsonl")"
      ;;
    canon)
      divergences="$(jq -n --slurpfile other "$work/$tag.cs.jsonl" "$NS_DEFS"'
        [foreach inputs as $r (-1; . + 1; . as $i | $r | select(fixd_canon($other[$i] // {})) | .path)] | length' \
        "$work/$tag.py.jsonl")"
      ;;
    secrets)
      divergences="$dropped"
      ;;
    hunt)
      divergences="$(diff <(jq -c 'select(.rule.name == "install-path")' "$work/$tag.pystock.jsonl") \
                          <(jq -c 'select(.rule.name == "install-path")' "$work/$tag.py.jsonl") | grep -c '^[<>]' || true)"
      ;;
    *)
      divergences=0
      ;;
  esac
  expected_total=$((expected_total + divergences))
  if diff -u "$work/$tag.py.filtered" "$work/$tag.cs.filtered" > "$work/$tag.diff"; then
    echo "ok   $surface $label: $count records, $divergences expected divergences"
  else
    status=1
    echo "FAIL $surface $label: $count records, $divergences expected divergences"
    head -c 20000 "$work/$tag.diff"
    echo
  fi
  if [[ -s "$work/$tag.semantics" ]]; then
    tail -n 2 "$work/$tag.semantics" | sed 's/^/     /'
  fi
  if [[ "$dropped" -gt 0 ]]; then
    sed 's/^/     expected (fix g, python never returned, port compared with the reference model): /' "$work/$tag.timeout"
  fi
  if [[ "$normaliser" == "hunt" && "$label" == "tools/parity/cases/hunt" ]]; then
    local expectation rel line value found missed
    for expectation in "${INSTALL_PATH_EXPECTATIONS[@]}"; do
      IFS=$'\t' read -r rel line value <<< "$expectation"
      found="$(jq -s --arg rel "$rel" --argjson line "$line" --arg value "$value" \
        '[.[] | select(.rule.name == "install-path" and .relative_path == $rel and .line_number == $line
                       and .metadata.plan_transform.value == $value)] | length' "$work/$tag.cs.jsonl")"
      missed="$(jq -s --arg rel "$rel" --argjson line "$line" --arg value "$value" \
        '[.[] | select(.rule.name == "install-path" and .relative_path == $rel and .line_number == $line
                       and .metadata.plan_transform.value == $value)] | length' "$work/$tag.pystock.jsonl")"
      if [[ "$found" == "1" && "$missed" == "0" ]]; then
        echo "     expected (fix e): port finds install path '$value' at $rel:$line, stock python does not"
      else
        status=1
        echo "FAIL $surface $label: fix e expectation $rel:$line '$value' (port hits $found, python hits $missed)"
      fi
    done
  fi
}

# make_special_files <dir>: a FIFO, a Unix socket and a symlink to /dev/null, none of them a regular file. A walk that opened
# the FIFO would block forever, so every port dump over them also runs under a timeout.
make_special_files() {
  mkdir -p "$1"
  mkfifo "$1/pipe.ini"
  python -c 'import socket, sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])' "$1/socket.ini"
  ln -s /dev/null "$1/device.ini"
  printf 'server host=special.corp.local\n' > "$1/regular.ini"
}

# diff pairs: every unordered pair of same-named files across the multi-server fixtures, then every case directory.
diff_pairs_multi_server() {
  local servers=() rel i j
  for i in $(seq -w 1 10); do servers+=("fixtures/multi-server/server$i"); done
  while IFS= read -r rel; do
    for ((i = 0; i < ${#servers[@]}; i++)); do
      [[ -f "${servers[$i]}/$rel" ]] || continue
      for ((j = i + 1; j < ${#servers[@]}; j++)); do
        [[ -f "${servers[$j]}/$rel" ]] || continue
        printf '%s\t%s\n' "${servers[$i]}/$rel" "${servers[$j]}/$rel"
      done
    done
  done < <(for i in "${servers[@]}"; do (cd "$i" && find . -type f | sed 's|^\./||'); done | LC_ALL=C sort -u)
}

run_phase4_surface() {
  case "$surface" in
    diff)
      diff_pairs_multi_server > "$work/multi-server.pairs"
      compare_records "fixtures/multi-server pairs" diff diff --pairs "$work/multi-server.pairs" "$@"
      compare_records "tools/parity/cases/diff pair with trailing separators" diff diff \
        tools/parity/cases/diff/xml-attribute-reorder/before.xml/ tools/parity/cases/diff/xml-attribute-reorder/after.xml// "$@"
      local case_dir case_dirs before after args_file
      : > "$work/cases.pairs"
      # Every directory holding a before* or after* file is a case, nested case directories included.
      mapfile -t case_dirs < <(find tools/parity/cases/diff -type f \( -name 'before*' -o -name 'after*' \) -printf '%h\n' | LC_ALL=C sort -u)
      for case_dir in "${case_dirs[@]}"; do
        before="$(find "$case_dir" -maxdepth 1 -type f -name 'before*' | head -n 1)"
        after="$(find "$case_dir" -maxdepth 1 -type f -name 'after*' | head -n 1)"
        if [[ -z "$before" || -z "$after" ]]; then
          status=1
          echo "FAIL $surface $case_dir: needs one before* and one after* file"
          continue
        fi
        args_file="$case_dir/args"
        if [[ -f "$args_file" ]]; then
          local extra=()
          mapfile -t extra < "$args_file"
          compare_records "$case_dir" diff diff "$before" "$after" "${extra[@]}" "$@"
        else
          printf '%s\t%s\n' "$before" "$after" >> "$work/cases.pairs"
        fi
      done
      compare_records "tools/parity/cases/diff pairs" diff diff --pairs "$work/cases.pairs" "$@"
      ;;
    canon)
      local content_type
      for content_type in text json xml; do
        compare_records "fixtures as $content_type" canon canon fixtures --content-type "$content_type" "$@"
        compare_records "tools/parity/cases/diff as $content_type" canon canon tools/parity/cases/diff --content-type "$content_type" "$@"
      done
      ;;
    hunt)
      compare_records "fixtures" hunt hunt fixtures "$@"
      compare_records "tools/parity/cases/hunt" hunt hunt tools/parity/cases/hunt "$@"
      compare_records "tools/parity/cases/hunt with excludes" hunt hunt tools/parity/cases/hunt \
        --exclude 'excluded/*' --exclude '*.skip' "$@"
      compare_records "tools/parity/cases/hunt with glob" hunt hunt tools/parity/cases/hunt --glob '*.ini' "$@"
      compare_records "tools/parity/cases/hunt file root" hunt hunt tools/parity/cases/hunt/every-rule.config "$@"
      compare_records "tools/parity/cases/hunt file root with trailing separator" hunt hunt tools/parity/cases/hunt/every-rule.config/ "$@"
      local glob
      for glob in '?.txt' '[ab]*' '[!a]*.txt' '*/*.ini' '**/*/*.txt' '*/**' 'nested/' '**/' 'nested//*' '**/../*.ini' 'a/../a.txt'; do
        compare_records "tools/parity/cases/hunt with glob $glob" hunt hunt tools/parity/cases/hunt --glob "$glob" "$@"
      done
      compare_records "generated/missing hunt root" hunt hunt "$work/no-such-hunt-root" "$@"
      # Generated tree (symlinks cannot be committed portably): a symlinked directory is followed by a wildcard part and
      # not by **, a symlinked file is scanned, a dangling link is skipped.
      local tree="$work/hunt-tree"
      mkdir -p "$tree/real" "$tree/plain/deeper"
      printf 'server host=real.corp.local\n' > "$tree/real/r.ini"
      printf 'version 1.2.3\n' > "$tree/plain/deeper/v.txt"
      printf 'server host=top.lan\n' > "$tree/top.ini"
      ln -s real "$tree/linkdir"
      ln -s top.ini "$tree/linkfile.ini"
      ln -s nowhere "$tree/dangling.ini"
      make_special_files "$tree/special"
      for glob in '**/*' '*/*.ini' 'linkdir/*' '**/linkdir/*' '*/**' 'linkdir/**' '**/*.ini' '*'; do
        compare_records "generated/hunt tree with glob $glob" hunt hunt "$tree" --glob "$glob" "$@"
      done
      if [[ "$(id -u)" != "0" ]]; then
        # Fix b: is_file() raising EACCES (a file inside a listable directory that cannot be searched) aborts Python's hunt with
        # PermissionError; the port lists the file in unreadable_files and reports the readable file's hits.
        local refused="$work/hunt-refused" py_refused cs_refused rc
        mkdir -p "$refused/unsearchable-sub"
        printf 'server host=plain.corp.local\n' > "$refused/plain.ini"
        printf 'server host=hidden.corp.local\n' > "$refused/unsearchable-sub/refused.ini"
        chmod 644 "$refused/unsearchable-sub"
        rc=0
        PYTHON_COLORS=0 PARITY_HUNT_FIX_E=1 PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py hunt "$refused" "$@" > "$work/hunt-refused.py.jsonl" 2> "$work/hunt-refused.py.err" || rc=$?
        cs_refused="$(timeout "$port_timeout" "$cli_exe" parity-dump hunt "$refused" "$@" || echo "port dump exited $?")"
        py_refused="$(tail -n 1 "$work/hunt-refused.py.err")"
        if [[ "$rc" -ne 0 && "$py_refused" == "PermissionError: [Errno 13] Permission denied: '$refused/unsearchable-sub/refused.ini'" ]] \
            && [[ "$(jq -c -s 'map(select(has("unreadable_files")) | .unreadable_files)' <<< "$cs_refused")" == "[[\"$refused/unsearchable-sub/refused.ini\"]]" ]] \
            && [[ "$(jq -r -s 'map(select(has("relative_path")) | .relative_path) | unique | join(",")' <<< "$cs_refused")" == "plain.ini" ]]; then
          echo "ok   $surface generated/hunt tree with a file whose stat is refused: expected divergence (fix b, python aborts, port lists it)"
          expected_total=$((expected_total + 1))
        else
          status=1
          echo "FAIL $surface generated/hunt tree with a file whose stat is refused: python exit $rc [$py_refused] port [$cs_refused]"
        fi
      fi
      ;;
    secrets)
      compare_records "fixtures/secret_samples" secrets secrets fixtures/secret_samples "$@"
      compare_records "tools/parity/cases/secrets" secrets secrets tools/parity/cases/secrets "$@"
      compare_records "tools/parity/cases/secrets file root with trailing separator" secrets secrets tools/parity/cases/secrets/password.env/ "$@"
      local guard_dir
      for guard_dir in tools/parity/cases/secrets-guard/*/; do
        guard_dir="${guard_dir%/}"
        compare_records "$guard_dir" secrets secrets "$guard_dir/input" --ruleset "$guard_dir/ruleset.json" "$@"
      done
      # Generated tree (special files cannot be committed): a FIFO, a Unix socket and a link to a character device are not
      # regular files, so both walks skip them without opening them.
      local special="$work/secrets-special"
      make_special_files "$special"
      printf 'password = Hunter12345\n' > "$special/plain.env"
      compare_records "generated/secrets tree with a fifo, a socket and a device link" secrets secrets "$special" "$@"
      if [[ "$(id -u)" != "0" ]]; then
        # A file whose stat is refused (inside a listable directory that cannot be searched) is a DetectorIOError record.
        local refused="$work/secrets-refused"
        mkdir -p "$refused/unsearchable-sub"
        printf 'password = Hunter12345\n' > "$refused/plain.env"
        printf 'password = Hunter12345\n' > "$refused/unsearchable-sub/refused.env"
        chmod 644 "$refused/unsearchable-sub"
        compare_records "generated/secrets tree with a file whose stat is refused" secrets secrets "$refused" "$@"
      fi
      local context_dir
      while IFS= read -r context_dir; do
        compare_records "$context_dir" secrets-context none "$context_dir" "$@"
      done < <(find tools/parity/cases/secrets-context -type d | LC_ALL=C sort)
      ;;
  esac
}

if [[ "$surface" == "detect" || "$surface" == "decode" ]]; then
  compare "fixtures" "fixtures" "$@"
  if [[ -d "tools/parity/cases/$surface" ]]; then
    compare "tools/parity/cases/$surface" "tools/parity/cases/$surface" "$@"
  fi

  # Generated file-system cases (the entries cannot be committed: git tracks neither mode 000 nor
  # dangling-target guarantees across checkouts).
  gen="$work/generated"
  mkdir -p "$gen/tree/readable-sub" "$gen/tree/locked-sub" "$gen/tree/unsearchable-sub" "$gen/locked"
  directives=$'alpha one\nbeta two\ngamma three\ndelta four\n'
  printf '%s' "$directives" > "$gen/tree/readable.conf"
  printf '%s' "$directives" > "$gen/tree/readable-sub/nested.conf"
  printf '%s' "$directives" > "$gen/tree/locked-sub/hidden.conf"
  printf '%s' "$directives" > "$gen/tree/unreadable.conf"
  printf '%s' "$directives" > "$gen/tree/unsearchable-sub/refused.conf"
  ln -s "$gen/tree/nowhere" "$gen/tree/dangling"
  ln -s "$gen/tree/readable.conf" "$gen/tree/linkfile"
  ln -s "$gen/tree/readable-sub" "$gen/tree/linkdir"
  make_special_files "$gen/tree/special"
  if [[ "$(id -u)" != "0" ]]; then
    chmod 000 "$gen/tree/unreadable.conf" "$gen/tree/locked-sub" "$gen/locked"
    # Listable but not searchable: is_file() on refused.conf raises EACCES, which scan_path reports.
    chmod 644 "$gen/tree/unsearchable-sub"
    compare "generated/tree (unreadable file, unreadable and unsearchable subdirectories, dangling link, linked file and directory)" "$gen/tree" "$@"
  else
    echo "skip $surface generated/tree: running as root, permission cases are not observable"
  fi
  compare "generated/missing root" "$work/does-not-exist" "$@"
  compare "generated/file root" "$gen/tree/readable.conf" "$@"
  compare "generated/file root with trailing separator" "$gen/tree/readable.conf/" "$@"
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
else
  run_phase4_surface "$@"
fi

echo "== $surface summary: $files_total files compared, $expected_total expected divergences, $([[ $status -eq 0 ]] && echo PASS || echo FAIL)"
exit "$status"
