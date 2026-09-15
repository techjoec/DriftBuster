#!/usr/bin/env bash
# Parity harness: runs the Python and C# dumps of one engine surface and diffs them. detect and
# decode run over fixtures/, tools/parity/cases/<surface>/ and a set of generated file-system cases
# (unreadable file, unreadable subdirectory, dangling symlink, missing root, unreadable root, tiny
# sampling budget, a FIFO, a socket and a device link); diff, canon, hunt and secrets run over the inputs listed in
# run_phase4_surface; multi-server runs every plan file under tools/parity/cases/multi-server/ (run_multi_server_surface);
# profile-store, run-profile and schedule run every case under tools/parity/cases/<surface>/ (run_phase6_surface).
# Exit 1 on any difference that is not an expected divergence (see expected_divergences.md). Temporary; deleted with the
# Python tree.
#
# Usage: tools/parity/run_parity.sh <surface> [dump args...]
#   surfaces: detect, decode, diff, canon, hunt, secrets, multi-server, profile-store, run-profile, schedule
#   example:  tools/parity/run_parity.sh detect            (the full default registry on both sides)
#             PARITY_MULTI_SERVER_CASES='unreadable|root-' tools/parity/run_parity.sh multi-server   (cases whose path matches)
#             tools/parity/run_parity.sh detect --plugins text
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <surface> [dump args...]" >&2
  exit 2
fi

surface="$1"
shift
case "$surface" in
  detect|decode|diff|canon|hunt|secrets|multi-server|profile-store|run-profile|schedule) ;;
  *)
    echo "error: unknown surface '$surface' (detect, decode, diff, canon, hunt, secrets, multi-server, profile-store, run-profile, schedule)" >&2
    exit 2
    ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

# A case input the repository's ignore patterns match would be left out of a commit, so a fresh clone would compare different inputs.
ignored_cases="$(find tools/parity/cases \( -type f -o -type l \) -print0 | git check-ignore -z --stdin --no-index | tr '\0' '\n' || true)"
if [[ -n "$ignored_cases" ]]; then
  echo "error: git ignores these case inputs (re-include them in tools/parity/cases/.gitignore):" >&2
  echo "$ignored_cases" >&2
  exit 2
fi

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
SQLITE_URI_NAMES='def sqlite_uri_name: .plugin == "binary-hybrid" and .format == "embedded-sql-db" and (.path | test("%[0-9A-Fa-f]{2}|[?#]"));
  def drop_sqlite_uri_facts: if sqlite_uri_name then (.metadata |= del(.table_count)) | (.reasons |= map(select(startswith("Enumerated ") | not))) else . end;'
compare() {
  local label="$1" root="$2"
  shift 2
  local tag py_out cs_out fixc_count limit_count sqlite_count count
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
  fixc_count="$(wc -l < "$work/$tag.fixc")"
  limit_count="$(wc -l < "$work/$tag.limit")"
  sqlite_count="$(wc -l < "$work/$tag.sqlite")"
  jq -c --slurpfile fixc "$work/$tag.fixc.json" --slurpfile limit "$work/$tag.limit.json" "$SQLITE_URI_NAMES"'
    .path as $p
    | select(($limit[0] | index($p)) == null)
    | drop_sqlite_uri_facts
    | if ($fixc[0] | index($p)) != null then del(.error) else . end' "$py_out" > "$work/$tag.py.filtered"
  jq -c --slurpfile fixc "$work/$tag.fixc.json" --slurpfile limit "$work/$tag.limit.json" "$SQLITE_URI_NAMES"'
    .path as $p
    | select(($limit[0] | index($p)) == null)
    | drop_sqlite_uri_facts
    | if ($fixc[0] | index($p)) != null and .metadata != null
      then .metadata |= with_entries(select(.key | startswith("catalog_") | not)) else . end' "$cs_out" > "$work/$tag.cs.filtered"
  # A CPython limit is only an expected divergence when the port parsed the file as JSON.
  jq -r --slurpfile limit "$work/$tag.limit.json" '
    .path as $p | select(($limit[0] | index($p)) != null and (.plugin != "json" or .error != null)) | .path' "$cs_out" > "$work/$tag.limit.bad"

  count="$(wc -l < "$py_out")"
  files_total=$((files_total + count))
  local divergences=$((fixc_count + limit_count + sqlite_count))
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
    PARITY_SECRETS_TIMEOUT="$([[ "$surface_cmd" == secrets || "$surface_cmd" == run-profile ]] && echo "$secrets_timeout" || echo 0)" PYTHONPATH="$repo_root/src" \
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
    if ! jq -e -n '[inputs] | length >= 0' "$work/$tag.$side.jsonl" > /dev/null 2> "$work/$tag.$side.jq.err"; then
      echo "FAIL $surface $tag: $side dump is not valid JSON lines"
      sed 's/^/     /' "$work/$tag.$side.jq.err" | tail -n 5
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

# compare_records <label> <surface command> <normaliser: none|diff|canon|hunt|secrets|run-profile|profile-store> <args...>
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
      profile-store)
        # Detection profile text fields: where Python keeps a number, bool, list or dict the port holds its str() ("True", "1.0"); the
        # Python value is replaced by the port's text when it is exactly that str() (recorded divergence), everything else compared as is.
        if [[ "$side" == py ]]; then
          python tools/parity/profile_text_fields.py "$work/$tag.py.jsonl" "$work/$tag.cs.jsonl" > "$work/$tag.$side.filtered" 2> "$work/$tag.textfields"
        else
          cp "$work/$tag.cs.jsonl" "$work/$tag.$side.filtered"
        fi
        ;;
      run-profile)
        # Structured sources (plan decision): python ran the offline runner, so the collected file set, the error (type and message), the
        # secret findings and redaction guards (paths under the source directory, sorted) and profile.json compare; on an error only the
        # error does.
        jq -n -c --arg side "$side" --slurpfile py "$work/$tag.py.jsonl" '
          def findings: map({path, rule, line, snippet}) | sort_by(.path, .line, .rule, .snippet);
          def guards: map({path, rule, line}) | sort_by(.path, .line, .rule);
          def offline_py: if .error != null then {error} else {collected, error: null, findings: (.findings | findings),
            redaction_guard: (.redaction_guard | guards), profile_json} end;
          def offline_cs: if .error != null then {error} else {collected, error: null, findings: (.result.secrets.findings | findings),
            redaction_guard: ((.redaction_guard // []) | guards),
            profile_json: ([.profiles[] | select(.path | endswith("/profile.json")) | .text]
              | if length == 1 then .[0] else "<\(length) profile.json files>" end)} end;
          foreach inputs as $r (-1; . + 1; . as $i | $r
            | if ($py[$i].mode // null) == "offline-runner" then (if $side == "py" then offline_py else offline_cs end) else . end)' \
          "$work/$tag.$side.jsonl" > "$work/$tag.$side.filtered"
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
    profile-store)
      divergences="$(tail -n 1 "$work/$tag.textfields")"
      ;;
    run-profile)
      divergences=$(( $(jq -s '[.[] | select(.mode == "offline-runner")] | length' "$work/$tag.py.jsonl") + $(grep -c '^fix g: ' "$work/$tag.py.err" || true) \
        + $([[ "${PARITY_RUN_PROFILE_SURROGATE_NAMES:-0}" == 1 ]] && echo 1 || echo 0) ))
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
  if [[ "$normaliser" == "run-profile" ]]; then
    if jq -e 'select(.mode == "offline-runner")' "$work/$tag.py.jsonl" > /dev/null; then
      echo "     expected (structured sources, plan decision: compared against the offline runner, not execute_profile; collected files, error, findings, guards and profile.json)"
    fi
    sed -n 's/^fix g: /     expected (fix g, python never returned, the copy compared with the reference model): /p' "$work/$tag.py.err"
    if [[ "${PARITY_RUN_PROFILE_SURROGATE_NAMES:-0}" == 1 ]]; then
      if grep -q '^surrogate name lookup: ' "$work/$tag.py.err"; then
        echo "     expected (python-surrogate-name-lookup, decision R: stock python raised UnicodeEncodeError; compared with python looking up these names as not found)"
        sed -n 's/^surrogate name lookup: /       name /p' "$work/$tag.py.err"
      else
        status=1
        echo "FAIL $surface $label: declares python-surrogate-name-lookup but python looked up no name holding an unpaired surrogate"
      fi
    fi
    if grep -q '^fix g: ' "$work/$tag.py.err" && ! jq -e 'has("redaction_guard")' "$work/$tag.py.jsonl" > /dev/null; then
      status=1
      echo "FAIL $surface $label: fix g: python did not return but the reference model fired no guard"
    fi
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
# A root spelled shortcut/.. where shortcut -> real/app/nested: the kernel resolves it to real/app, a lexical collapse to the tree
# itself (whose lexical.* files must never be scanned).
make_dotdot_tree() {
  local tree="$1"
  mkdir -p "$tree/real/app/nested"
  printf 'server host=physical.corp.local\npassword = Physical12345\nalpha one\nbeta two\n' > "$tree/real/app/app.conf"
  printf 'server host=nested.corp.local\n' > "$tree/real/app/nested/deep.ini"
  printf 'server host=lexical.corp.local\npassword = Lexical12345\n' > "$tree/lexical.conf"
  ln -s real/app/nested "$tree/shortcut"
}

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
      make_dotdot_tree "$work/hunt-dotdot"
      compare_records "generated/hunt root with .. after a symlink" hunt hunt "$work/hunt-dotdot/shortcut/.." "$@"
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
      make_dotdot_tree "$work/secrets-dotdot"
      compare_records "generated/secrets root with .. after a symlink" secrets secrets "$work/secrets-dotdot/shortcut/.." "$@"
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

# ---------------------------------------------------------------------------------------------------------------------
# multi-server: every .json under tools/parity/cases/multi-server/ (at any depth, outside trees/ directories) holds a request (a
# "plans" value, decoded by both runners with multi_server._build_plans's coercions) plus harness keys the dumps ignore: "args"
# (extra dump arguments: --sample-budget, --sample-size, --runs), "requires" ("unprivileged": skipped as root), "fixes" (the plan
# fixes and platform limits the case exercises), "stock_aborts" (the exception the stock Python run raises out of run()),
# "aborts" (text every run, the port included, must abort with: a request coercion error or a throttle time.sleep refuses),
# "divergences" (recorded divergences the case must show: "empty-response-mappings", "diff-summary-replacement",
# "python-surrogate-encode", the latter compared by multi_server_surrogates.py) and "self_test" (a multi_server_stock.py --self-test run after the case: "wrong-drift-count" or "wrong-used-cache").
# Roots are repository-relative, or under $PARITY_MULTI_SERVER_WORK for trees generated here and by the generate_trees.sh scripts
# (modes, FIFOs, sockets, links, undecodable names, files past 128 KiB), which both runners expand.
#
# Per case, four dumps: the port, the fixed Python runner (PARITY_MULTI_SERVER_FIXES=1: fixes a and b applied as the port
# applies them), the fix b Python runner (PARITY_MULTI_SERVER_FIXES=b) and the stock Python runner. The Python runs canonicalise
# namespaced xml through the port (PARITY_PORT_CLI, fix d, counted from the records file) so everything after canonicalisation
# is CPython's own. The fixed dump must equal the port dump byte for byte (key_order included); multi_server_stock.py then proves
# the fix b dump differs from the stock one only where a skipped file or refused root (fix b) or an undecodable name explains
# it, and the fixed dump from the fix b one only where a collision (fix a) does; each such entry is an expected divergence.
# ---------------------------------------------------------------------------------------------------------------------
generate_multi_server_trees() {
  local tree="$1" i
  mkdir -p "$tree/unreadable-file/hostA/unsearchable" "$tree/unreadable-file/hostB" "$tree/unreadable-root/locked" \
    "$tree/large/hostA" "$tree/large/hostB" "$tree/fifo/hostB"
  printf '{"Server": "locked.corp.local", "Port": 8080}\n' > "$tree/unreadable-file/hostA/app.json"
  printf '{"Secret": "not readable"}\n' > "$tree/unreadable-file/hostA/locked.json"
  printf '[core]\nname = hidden\n' > "$tree/unreadable-file/hostA/unsearchable/refused.ini"
  printf '[core]\nname = visible\n' > "$tree/unreadable-file/hostA/settings.ini"
  printf '{"Server": "plain.corp.local", "Port": 8080}\n' > "$tree/unreadable-file/hostB/app.json"
  printf '[core]\nname = visible\n' > "$tree/unreadable-file/hostB/settings.ini"
  printf '{"Server": "inside.corp.local"}\n' > "$tree/unreadable-root/locked/app.json"
  printf '{"Server": "file.corp.local"}\n' > "$tree/unreadable-root/locked.json"
  # 6000 lines of about 28 bytes: past the 128 KiB sample; the hosts differ only on the last line.
  for i in A B; do
    awk -v last="$i" 'BEGIN { print "[settings]"; for (n = 1; n <= 6000; n++) printf "setting_%05d = value-%05d\n", n, n; print "tail = " last }' \
      > "$tree/large/host$i/big.ini"
  done
  # A root inside a directory that cannot be searched: its stat fails with EACCES, which Path.exists() raises.
  mkdir -p "$tree/root-lookup/sealed/root"
  printf '{"Server": "sealed.corp.local"}\n' > "$tree/root-lookup/sealed/root/app.json"
  make_special_files "$tree/fifo/hostA"
  printf '{"Server": "special.corp.local"}\n' > "$tree/fifo/hostA/app.json"
  printf 'server host=special.corp.local\n' > "$tree/fifo/hostB/regular.ini"
  printf '{"Server": "special.corp.local"}\n' > "$tree/fifo/hostB/app.json"
  if [[ "$(id -u)" != "0" ]]; then
    chmod 000 "$tree/unreadable-file/hostA/locked.json" "$tree/unreadable-root/locked" "$tree/unreadable-root/locked.json"
    chmod 644 "$tree/unreadable-file/hostA/unsearchable"
    chmod 000 "$tree/root-lookup/sealed"
  fi
}

compare_multi_server() {
  local case_file="$1" tag rc side count divergences fixd stock_lines stock_aborts aborts declared_divergences shaped self_test
  local surrogate=0 surrogate_lines=0 surrogate_rc=0 aborted=()
  shift
  tag="multi-server-$(printf '%s' "${case_file#tools/parity/cases/multi-server/}" | tr '/' '-')"
  tag="${tag%.json}"
  local args=() requires fields=()
  # The harness keys, read by Python: jq rejects the escaped unpaired surrogates a plan may hold. One line each for requires,
  # stock_aborts, aborts, divergences and self_test, then one line per dump argument.
  mapfile -t fields < <(python -c '
import json, sys
case = json.load(open(sys.argv[1], encoding="utf-8"))
print(",".join(case.get("requires") or []))
for key in ("stock_aborts", "aborts"):
    print(case.get(key) or "")
print(",".join(case.get("divergences") or []))
print(case.get("self_test") or "")
for arg in case.get("args") or []:
    print(arg)
' "$case_file")
  if [[ "${#fields[@]}" -lt 5 ]]; then
    status=1
    echo "FAIL $surface $case_file: the case file cannot be read"
    return
  fi
  requires="${fields[0]}"
  stock_aborts="${fields[1]}"
  aborts="${fields[2]}"
  declared_divergences="${fields[3]}"
  self_test="${fields[4]}"
  args=("${fields[@]:5}")
  if [[ ",$declared_divergences," == *",python-surrogate-encode,"* ]]; then
    surrogate=1
  fi
  if [[ ",$requires," == *",unprivileged,"* && "$(id -u)" == "0" ]]; then
    echo "skip $surface $case_file: running as root, permission cases are not observable"
    return
  fi
  if [[ -n "$aborts" ]]; then
    compare_multi_server_abort "$case_file" "$tag" "$aborts" "${args[@]}" "$@"
    return
  fi
  for side in fixed bonly stock; do
    rc=0
    PARITY_PORT_CLI="$cli_exe" PARITY_MULTI_SERVER_FIXES="$(case "$side" in fixed) echo 1 ;; bonly) echo b ;; *) echo 0 ;; esac)" \
      PARITY_MULTI_SERVER_RECORDS="$work/$tag.$side.records.json" PYTHONPATH="$repo_root/src" PYTHON_COLORS=0 \
      timeout "$port_timeout" python tools/parity/py_dump.py multi-server "$case_file" "${args[@]}" "$@" \
      > "$work/$tag.$side.json" 2> "$work/$tag.$side.err" || rc=$?
    if [[ "$side" == stock && -n "$stock_aborts" ]]; then
      if [[ "$rc" -eq 0 ]] || ! grep -qF -- "$stock_aborts" "$work/$tag.$side.err"; then
        status=1
        echo "FAIL $surface $case_file: the stock python dump was expected to abort with '$stock_aborts' (exit $rc)"
        sed 's/^/     /' "$work/$tag.$side.err" | tail -n 5
        return
      fi
      continue
    fi
    # python-surrogate-encode: run() raising UnicodeEncodeError on an unpaired surrogate is the recorded divergence, not a failure.
    if [[ "$rc" -eq 1 && "$surrogate" == 1 && ! -s "$work/$tag.$side.json" ]] \
      && tail -n 1 "$work/$tag.$side.err" | grep -q '^UnicodeEncodeError: .*surrogates not allowed'; then
      aborted+=("$side")
      continue
    fi
    if [[ "$rc" -ne 0 ]]; then
      status=1
      echo "FAIL $surface $case_file: $side python dump exited $rc"
      sed 's/^/     /' "$work/$tag.$side.err" | tail -n 5
      return
    fi
  done
  if [[ "${#aborted[@]}" -ne 0 && "${#aborted[@]}" -ne 3 ]]; then
    status=1
    echo "FAIL $surface $case_file: only the ${aborted[*]} python dump(s) raised on an unpaired surrogate"
    return
  fi
  rc=0
  timeout "$port_timeout" "$cli_exe" parity-dump multi-server "$case_file" "${args[@]}" "$@" > "$work/$tag.cs.json" 2> "$work/$tag.cs.err" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    status=1
    echo "FAIL $surface $case_file: port dump exited $rc"
    sed 's/^/     /' "$work/$tag.cs.err" | tail -n 5
    return
  fi
  for side in $([[ "${#aborted[@]}" -eq 0 ]] && echo fixed bonly) $([[ -z "$stock_aborts" && "${#aborted[@]}" -eq 0 ]] && echo stock) cs; do
    if [[ "$(wc -l < "$work/$tag.$side.json")" != "1" ]] || ! jq -e 'has("response") and has("progress") and has("cache") and has("key_order")' "$work/$tag.$side.json" > /dev/null 2>&1; then
      status=1
      echo "FAIL $surface $case_file: $side dump is not one multi-server document"
      return
    fi
  done
  count="$(jq '.response.results | length' "$work/$tag.cs.json")"
  files_total=$((files_total + count))
  fixd="$(jq '.port_xml_canonical | length' "$work/$tag.fixed.records.json")"
  rc=0
  if [[ "${#aborted[@]}" -eq 0 ]]; then
    python tools/parity/multi_server_stock.py "$case_file" "$work/$tag.stock.json" "$work/$tag.stock.records.json" \
      "$work/$tag.bonly.json" "$work/$tag.bonly.records.json" "$work/$tag.fixed.json" "$work/$tag.fixed.records.json" \
      > "$work/$tag.stock.divergences" 2> "$work/$tag.stock.problems" || rc=$?
  else
    : > "$work/$tag.stock.divergences"
    : > "$work/$tag.stock.problems"
  fi
  stock_lines="$(wc -l < "$work/$tag.stock.divergences")"
  shaped=0
  if ! apply_empty_response_mappings "$case_file" "$tag" "$declared_divergences"; then
    return
  fi
  summaries=0
  if [[ "${#aborted[@]}" -eq 0 ]] && ! apply_diff_summary_replacement "$case_file" "$tag" "$declared_divergences"; then
    return
  fi
  local port_equal=0 port_compared="$work/$tag.cs.json"
  if [[ "$surrogate" == 1 ]]; then
    # A python run that raised before the last run never started the rest: its records are compared with the port's dump of
    # that run over the same fresh cache.
    local abort_run runs=1 run_args=() i
    abort_run="$(python -c 'import json, sys; abort = json.load(open(sys.argv[1], encoding="utf-8")).get("abort"); print(abort["run"] if abort else "")' \
      "$work/$tag.fixed.records.json")"
    for ((i = 0; i < ${#args[@]}; i++)); do
      if [[ "${args[i]}" == --runs ]]; then
        runs="${args[i + 1]}"
        i=$((i + 1))
      elif [[ "${args[i]}" == --runs=* ]]; then
        runs="${args[i]#--runs=}"
      else
        run_args+=("${args[i]}")
      fi
    done
    if [[ -n "$abort_run" && "$abort_run" -lt "$runs" ]]; then
      port_compared="$work/$tag.cs.run$abort_run.json"
      rc=0
      timeout "$port_timeout" "$cli_exe" parity-dump multi-server "$case_file" "${run_args[@]}" --runs "$abort_run" "$@" \
        > "$port_compared" 2> "$work/$tag.cs.run$abort_run.err" || rc=$?
      if [[ "$rc" -ne 0 ]]; then
        status=1
        echo "FAIL $surface $case_file: port dump of run $abort_run exited $rc"
        sed 's/^/     /' "$work/$tag.cs.run$abort_run.err" | tail -n 5
        return
      fi
    fi
    python tools/parity/multi_server_surrogates.py "$case_file" "$work/$tag.fixed.json" "$work/$tag.fixed.records.json" "$port_compared" \
      > "$work/$tag.surrogates.divergences" 2> "$work/$tag.surrogates.problems" || surrogate_rc=$?
    surrogate_lines="$(wc -l < "$work/$tag.surrogates.divergences")"
    [[ "$surrogate_rc" -eq 0 ]] && port_equal=1
  elif cmp -s "$work/$tag.fixed.json" "$work/$tag.cs.json"; then
    port_equal=1
  fi
  divergences=$((stock_lines + fixd + shaped + summaries + surrogate_lines))
  expected_total=$((expected_total + divergences))
  if [[ "$port_equal" -eq 1 && "$rc" -eq 0 ]]; then
    echo "ok   $surface $case_file: $count hosts, $divergences expected divergences"
  else
    status=1
    echo "FAIL $surface $case_file: $count hosts, $divergences expected divergences"
    if [[ "$port_equal" -ne 1 && "$surrogate" == 1 ]]; then
      echo "     fixed python and port differ beyond python-surrogate-encode:"
      head -c 4000 "$work/$tag.surrogates.problems" | sed 's/^/     /'
    elif [[ "$port_equal" -ne 1 ]]; then
      echo "     fixed python dump and port dump differ:"
      # head closes the pipe early on a long diff; under pipefail that SIGPIPE must not end the whole run.
      diff -u <(jq . "$work/$tag.fixed.json") <(jq . "$work/$tag.cs.json") | head -c 20000 || true
      echo
    fi
    if [[ "$rc" -ne 0 ]]; then
      echo "     the python runs differ beyond the declared fixes:"
      head -c 4000 "$work/$tag.stock.problems" | sed 's/^/     /'
    fi
  fi
  sed 's/^/     expected (/; s/: /, python runs differ): /' "$work/$tag.stock.divergences"
  if [[ "$surrogate_lines" -gt 0 ]]; then
    sed 's/^/     expected (/; s/: /, fixed python against the port): /' "$work/$tag.surrogates.divergences"
  fi
  if [[ "$fixd" -gt 0 ]]; then
    echo "     expected (fix d, $fixd namespaced xml text(s) canonicalised by the port for both python runs)"
  fi
  if [[ "$shaped" -gt 0 ]]; then
    echo "     expected (empty-response-mappings: python run([]) returns catalog and drilldown as {}, the port's model holds arrays)"
  fi
  if [[ "$summaries" -gt 0 ]]; then
    echo "     expected (diff-summary-replacement: $summaries drilldown diff_summary value(s) keep an unpaired surrogate in python, U+FFFD in the port's JsonElement)"
  fi
  if [[ -n "$self_test" ]]; then
    rc=0
    python tools/parity/multi_server_stock.py --self-test "$case_file" "$work/$tag.stock.json" "$work/$tag.stock.records.json" \
      "$work/$tag.bonly.json" "$work/$tag.bonly.records.json" "$work/$tag.fixed.json" "$work/$tag.fixed.records.json" \
      > "$work/$tag.self-test" 2>&1 || rc=$?
    if [[ "$rc" -eq 0 ]]; then
      sed 's/^/     ok   /' "$work/$tag.self-test"
    else
      status=1
      echo "FAIL $surface $case_file: harness self-test $self_test"
      sed 's/^/     /' "$work/$tag.self-test"
    fi
  fi
}

# The recorded "Empty plans" divergence: run([]) returns catalog and drilldown as empty mappings, the port's model as empty
# arrays. Only when the case declares it, the fixed dump's response has no results and both values are exactly {}, and the port's
# are exactly [], are the two {} respelled [] in a copy of the fixed dump (key_order is [] for both shapes); the copy then goes
# through the byte comparison. A case that shows the shape without declaring it fails that comparison; a declared case that does
# not show it fails here.
apply_empty_response_mappings() {
  local case_file="$1" tag="$2" declared="$3" fixed_shape port_shape
  fixed_shape="$(jq -c '[.response.results, .response.catalog, .response.drilldown]' "$work/$tag.fixed.json")"
  port_shape="$(jq -c '[.response.results, .response.catalog, .response.drilldown]' "$work/$tag.cs.json")"
  local name
  for name in ${declared//,/ }; do
    if [[ "$name" != "empty-response-mappings" && "$name" != "python-surrogate-encode" && "$name" != "diff-summary-replacement" ]]; then
      status=1
      echo "FAIL $surface $case_file: unknown divergences '$declared' (known: empty-response-mappings, python-surrogate-encode, diff-summary-replacement)"
      return 1
    fi
  done
  if [[ ",$declared," != *",empty-response-mappings,"* ]]; then
    return 0
  fi
  if [[ "$fixed_shape" != '[[],{},{}]' || "$port_shape" != '[[],[],[]]' ]]; then
    status=1
    echo "FAIL $surface $case_file: declares empty-response-mappings but python has $fixed_shape and the port $port_shape"
    return 1
  fi
  if [[ "$(grep -o '"catalog": {}' "$work/$tag.fixed.json" | wc -l)" != "1" || "$(grep -o '"drilldown": {}' "$work/$tag.fixed.json" | wc -l)" != "1" ]]; then
    status=1
    echo "FAIL $surface $case_file: the fixed dump does not spell each empty mapping exactly once"
    return 1
  fi
  sed 's/"catalog": {}/"catalog": []/; s/"drilldown": {}/"drilldown": []/' "$work/$tag.fixed.json" > "$work/$tag.fixed.shaped.json"
  mv "$work/$tag.fixed.shaped.json" "$work/$tag.fixed.json"
  shaped=1
}

# The recorded "diff_summary holding an unpaired surrogate" port-model limit: ConfigDrilldown.DiffSummary is a JsonElement, which
# holds UTF-8, so a lone surrogate Python keeps there (a label heading only empty diffs, which raises nothing) is U+FFFD in the
# port. Only when the case declares it: in each drilldown entry whose fixed diff_summary holds an escaped lone surrogate, every
# such escape is replaced by U+FFFD; when that equals the port's diff_summary exactly (key order included), the port's value is
# written into a copy of the fixed dump, which then goes through the byte comparison. No other field is touched. A declared case
# that shows no such entry fails here; an undeclared one fails the byte comparison.
apply_diff_summary_replacement() {
  local case_file="$1" tag="$2" declared="$3" count
  if [[ ",$declared," != *",diff-summary-replacement,"* ]]; then
    return 0
  fi
  count="$(python -c '
import json, re, sys
escaped = re.compile(r"\\ud[89a-f][0-9a-f]{2}")
def replaced(value):
    if isinstance(value, str):
        return escaped.sub("\ufffd", value)
    if isinstance(value, dict):
        return {key: replaced(item) for key, item in value.items()}
    if isinstance(value, list):
        return [replaced(item) for item in value]
    return value
fixed_path, port_path = sys.argv[1], sys.argv[2]
fixed = json.load(open(fixed_path, encoding="utf-8"))
port = json.load(open(port_path, encoding="utf-8"))
spell = lambda value: json.dumps(value, ensure_ascii=False)
count = 0
for f_entry, p_entry in zip(fixed["response"]["drilldown"], port["response"]["drilldown"]):
    summary = f_entry.get("diff_summary")
    if replaced(summary) != summary and spell(replaced(summary)) == spell(p_entry.get("diff_summary")):
        f_entry["diff_summary"] = p_entry["diff_summary"]
        count += 1
with open(fixed_path + ".summaries", "w", encoding="utf-8") as handle:
    handle.write(json.dumps(fixed, sort_keys=True, ensure_ascii=False) + "\n")
print(count)
' "$work/$tag.fixed.json" "$work/$tag.cs.json")" || count=""
  if [[ -z "$count" || "$count" == "0" ]]; then
    status=1
    echo "FAIL $surface $case_file: declares diff-summary-replacement but no drilldown diff_summary differs from the port's only by U+FFFD for an unpaired surrogate"
    return 1
  fi
  mv "$work/$tag.fixed.json.summaries" "$work/$tag.fixed.json"
  summaries="$count"
}

# A case whose every run aborts: the three Python dumps and the port dump must each exit non-zero (a timeout is not an abort)
# with the case's "aborts" text in its stderr; nothing else exists to compare.
compare_multi_server_abort() {
  local case_file="$1" tag="$2" aborts="$3" side rc
  shift 3
  for side in fixed bonly stock cs; do
    rc=0
    if [[ "$side" == cs ]]; then
      timeout "$port_timeout" "$cli_exe" parity-dump multi-server "$case_file" "$@" > "$work/$tag.$side.json" 2> "$work/$tag.$side.err" || rc=$?
    else
      PARITY_PORT_CLI="$cli_exe" PARITY_MULTI_SERVER_FIXES="$(case "$side" in fixed) echo 1 ;; bonly) echo b ;; *) echo 0 ;; esac)" \
        PYTHONPATH="$repo_root/src" PYTHON_COLORS=0 \
        timeout "$port_timeout" python tools/parity/py_dump.py multi-server "$case_file" "$@" \
        > "$work/$tag.$side.json" 2> "$work/$tag.$side.err" || rc=$?
    fi
    if [[ "$rc" -eq 0 || "$rc" -eq 124 ]] || [[ -s "$work/$tag.$side.json" ]] || ! grep -qF -- "$aborts" "$work/$tag.$side.err"; then
      status=1
      echo "FAIL $surface $case_file: the $side dump was expected to abort with '$aborts' (exit $rc)"
      sed 's/^/     /' "$work/$tag.$side.err" | tail -n 5
      return
    fi
  done
  echo "ok   $surface $case_file: every run aborts with '$aborts'"
}

run_multi_server_surface() {
  local tree="$work/multi-server-trees" case_file
  generate_multi_server_trees "$tree"
  tools/parity/cases/multi-server/runtime-r1/generate_trees.sh "$tree"
  tools/parity/cases/multi-server/runtime-r2/generate_trees.sh "$tree"
  tools/parity/cases/multi-server/runtime-r3/generate_trees.sh "$tree"
  tools/parity/cases/multi-server/close-r1/generate_trees.sh "$tree"
  export PARITY_MULTI_SERVER_WORK="$tree"
  while IFS= read -r case_file; do
    compare_multi_server "$case_file" "$@"
  done < <(find tools/parity/cases/multi-server -type d -name trees -prune -o -type f -name '*.json' -print | LC_ALL=C sort \
    | grep -E -- "${PARITY_MULTI_SERVER_CASES:-.}")
}

# ---------------------------------------------------------------------------------------------------------------------
# Phase 6 surfaces. Every record is compared byte for byte (sorted keys) together with its key_order.
#   Cases are found at any depth (a group directory such as profiles-r1/ holds cases); a directory holding no case fails the surface.
#   profile-store: every *.json payload under tools/parity/cases/profile-store/ outside diff/ directories (extra dump arguments, one per
#          line, in <name>.args), then every <case>/ directory below a diff/ directory holding {baseline,current}.json through profile-diff.
#   run-profile: every directory under tools/parity/cases/run-profile/ holding profile.json (and workdir/); both dumps copy workdir/ (symlinks
#          kept as links) to the same scratch directory (--scratch), so absolute paths inside metadata.json and its digest agree. A profile
#          with a structured source (a mapping that sets alias, optional or exclude, names a registry_scan or sql_snapshot, or that
#          OfflineCollectionSource.from_dict refuses) has no execute_profile counterpart: py_dump.py runs the offline runner instead
#          ("mode": "offline-runner") and the normaliser keeps the collected file set (source, alias directory, relative path, size,
#          SHA-256), the error type and message, the secret findings and redaction guards with each path under its source directory,
#          sorted, and profile.json as the port must write it (structured sources, plan decision); on an error only the error. A copy
#          Python never finishes (fix g) is redone by secrets_guard.py's reference model inside py_dump.py (reported on its stderr) and
#          compared exactly, redaction_guard included. A case's optional "divergences" file may declare python-surrogate-name-lookup
#          (run_profile_declared).
#   schedule: every directory under tools/parity/cases/schedule/ holding config.json, an optional state.json and steps (one command with
#          its arguments per line); each step runs on both sides against that side's state file from the previous step, and the first
#          step that differs fails the case. Two recorded rows are handled (expected_divergences.md, "Scheduling" and "CPython limits
#          inside json.loads"): a step whose records differ only in the JSONDecodeError text (SCHEDULE_DECODE_TEXT), and a case whose
#          "divergences" file declares json-call-budget (schedule_declared).
# ---------------------------------------------------------------------------------------------------------------------
# SCHEDULE_DECODE_TEXT, with $cs the port's record: Python's record with its error message replaced by the port's when the two differ only
# where the "Failed to parse schedules / scheduler state" row says. Both errors are SystemExit; Python's message is that prefix and a
# JSONDecodeError text ("<reason>: line L column C (char N)", one of the decoder's reasons); the port's is the same prefix and "invalid JSON
# document", the state file's scratch directory (each dump makes its own) being the only part of the prefix allowed to differ, and only in
# the state message; neither record has output, and everything else (state, key_order) is equal. Any other pair comes back unchanged.
SCHEDULE_DECODE_TEXT='
def reasons: "Expecting value|Expecting property name enclosed in double quotes|Expecting .:. delimiter|Expecting .,. delimiter|Extra data|Unterminated string starting at|Invalid control character at|Invalid \\\\escape|Invalid \\\\uXXXX escape|Unexpected UTF-8 BOM \\(decode using utf-8-sig\\)";
def parts: capture("^(?<prefix>Failed to parse (?<what>schedules|scheduler state) from .+?): (?<text>(?:" + reasons + "): line [0-9]+ column [0-9]+ \\(char [0-9]+\\))$");
def scratch: if .what == "scheduler state" then .prefix | sub("/driftbuster-parity-schedule-[A-Za-z0-9_]+/state\\.json$"; "/<scratch>/state.json") else .prefix end;
. as $py
| if ($py | has("output")) or ($cs | has("output")) or ($py.error.type != "SystemExit") or ($cs.error.type != "SystemExit")
    or (($py | del(.error.message)) != ($cs | del(.error.message))) then $py
  else ([$py.error.message | parts] | .[0]) as $p
  | if $p == null or ($cs.error.message | endswith(": invalid JSON document") | not) then $py
    else ($cs.error.message | .[0:length - (": invalid JSON document" | length)]) as $port_prefix
    | if ($p | scratch) == ({prefix: $port_prefix, what: $p.what} | scratch) then $py | .error.message = $cs.error.message else $py end
    end
  end'

# schedule_decode_text_self_test: SCHEDULE_DECODE_TEXT must accept a manifest and a state pair that differ only in the decoder's text and
# refuse every pair that differs anywhere else.
schedule_decode_text_self_test() {
  local failures=0 name py cs want got
  local manifest='Failed to parse schedules from cases/x/config.json'
  local state_py='Failed to parse scheduler state from /tmp/driftbuster-parity-schedule-ab_12xyz/state.json'
  local state_cs='Failed to parse scheduler state from /tmp/driftbuster-parity-schedule-Q0mAc3/state.json'
  record() { jq -n -c --arg type "$1" --arg message "$2" --arg state "$3" '{error: {type: $type, message: $message}, key_order: [], state: $state}'; }
  while IFS='|' read -r name want py cs; do
    got="$(jq -c --argjson cs "$cs" "$SCHEDULE_DECODE_TEXT" <<< "$py")"
    if [[ "$want" == accept && "$(jq -S -c . <<< "$got")" != "$(jq -S -c . <<< "$cs")" ]] \
      || [[ "$want" == refuse && "$got" != "$(jq -c . <<< "$py")" ]]; then
      echo "FAIL schedule harness self-test of SCHEDULE_DECODE_TEXT: $name"
      failures=1
    fi
  done <<EOF_CASES
extra data in the manifest|accept|$(record SystemExit "$manifest: Extra data: line 1 column 19 (char 18)" "")|$(record SystemExit "$manifest: invalid JSON document" "")
BOM in the state file|accept|$(record SystemExit "$state_py: Unexpected UTF-8 BOM (decode using utf-8-sig): line 1 column 1 (char 0)" s)|$(record SystemExit "$state_cs: invalid JSON document" s)
port text is not the recorded one|refuse|$(record SystemExit "$manifest: Extra data: line 1 column 19 (char 18)" "")|$(record SystemExit "$manifest: invalid JSON documents" "")
python text is not a JSONDecodeError|refuse|$(record SystemExit "$manifest: Exceeds the limit (4300 digits)" "")|$(record SystemExit "$manifest: invalid JSON document" "")
manifest paths differ|refuse|$(record SystemExit "$manifest: Extra data: line 1 column 19 (char 18)" "")|$(record SystemExit "${manifest/x/y}: invalid JSON document" "")
manifest scratch-like paths differ|refuse|$(record SystemExit "${state_py/scheduler state/schedules}: Extra data: line 1 column 2 (char 1)" "")|$(record SystemExit "${state_cs/scheduler state/schedules}: invalid JSON document" "")
state paths differ outside the scratch directory|refuse|$(record SystemExit "$state_py: Expecting value: line 1 column 1 (char 0)" s)|$(record SystemExit "${state_cs/tmp/var}: invalid JSON document" s)
state file text differs|refuse|$(record SystemExit "$manifest: Extra data: line 1 column 19 (char 18)" a)|$(record SystemExit "$manifest: invalid JSON document" b)
error types differ|refuse|$(record ValueError "$manifest: Extra data: line 1 column 19 (char 18)" "")|$(record SystemExit "$manifest: invalid JSON document" "")
the port printed output|refuse|$(record SystemExit "$manifest: Extra data: line 1 column 19 (char 18)" "")|$(jq -c '. + {output: []}' <<< "$(record SystemExit "$manifest: invalid JSON document" "")")
EOF_CASES
  [[ "$failures" -eq 0 ]] && echo "ok   schedule harness self-test of SCHEDULE_DECODE_TEXT: 2 accepted, 8 refused"
  return "$failures"
}

# schedule_declared <case dir>: reads the case's optional "divergences" file (one name per line). The only name is json-call-budget (see
# _JsonCallBudget in py_dump.py): Python's steps then run with PARITY_SCHEDULE_JSON_CALL_BUDGET=1, and every step on which that seam
# reports a decode must, run stock, end with exactly RecursionError "maximum recursion depth exceeded while calling a Python object".
# Sets schedule_call_budget to 1 or 0.
schedule_declared() {
  local dir="$1" name
  schedule_call_budget=0
  [[ -f "$dir/divergences" ]] || return 0
  while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    if [[ "$name" != "json-call-budget" ]]; then
      echo "FAIL $surface $dir: unknown divergence '$name' (known: json-call-budget)"
      return 1
    fi
    schedule_call_budget=1
  done < "$dir/divergences"
}

compare_schedule() {
  local dir="$1" tag step line side rc steps=0 failed=0 divergences=0 budget_steps=0
  shift
  tag="schedule-$(echo "${dir#tools/parity/cases/schedule/}" | tr '/ ' '__')"
  if ! schedule_declared "$dir"; then
    status=1
    return
  fi
  for side in py cs; do
    rm -f "$work/$tag.$side.state"
    [[ -f "$dir/state.json" ]] && cp "$dir/state.json" "$work/$tag.$side.state"
  done
  while IFS= read -r line; do
    [[ -z "$line" || "$line" == \#* ]] && continue
    steps=$((steps + 1))
    step="$tag.step$steps"
    local args=()
    read -r -a args <<< "$line"
    rc=0
    if [[ "$schedule_call_budget" -eq 1 && -f "$work/$tag.py.state" ]]; then
      cp "$work/$tag.py.state" "$work/$step.py.state-before"
    fi
    PARITY_SCHEDULE_JSON_CALL_BUDGET="$schedule_call_budget" PYTHONPATH="$repo_root/src" \
      python tools/parity/py_dump.py schedule "$dir/config.json" "$work/$tag.py.state" "${args[@]}" "$@" \
      > "$work/$step.py.jsonl" 2> "$work/$step.py.err" || rc=$?
    if [[ "$rc" -eq 0 ]]; then
      timeout "$port_timeout" "$cli_exe" parity-dump schedule "$dir/config.json" "$work/$tag.cs.state" "${args[@]}" "$@" \
        > "$work/$step.cs.jsonl" 2> "$work/$step.cs.err" || rc=$?
      [[ "$rc" -ne 0 ]] && side=cs
    else
      side=py
    fi
    if [[ "$rc" -ne 0 ]]; then
      echo "FAIL $surface $dir: step $steps ($line): $side dump exited $rc"
      sed 's/^/     /' "$work/$step.$side.err" | tail -n 5
      failed=1
      break
    fi
    for side in py cs; do
      if [[ "$(wc -l < "$work/$step.$side.jsonl")" != "1" ]] || ! jq -e 'has("state") and has("key_order")' "$work/$step.$side.jsonl" > /dev/null 2>&1; then
        echo "FAIL $surface $dir: step $steps ($line): $side dump is not one schedule record"
        failed=1
      fi
    done
    [[ "$failed" -eq 1 ]] && break
    if grep -q '^json call budget: ' "$work/$step.py.err"; then
      # The same step run stock (from the same state file) must end with the call-budget RecursionError.
      rm -f "$work/$step.stock.state"
      [[ -f "$work/$step.py.state-before" ]] && cp "$work/$step.py.state-before" "$work/$step.stock.state"
      PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py schedule "$dir/config.json" "$work/$step.stock.state" "${args[@]}" "$@" \
        > "$work/$step.stock.jsonl" 2> "$work/$step.stock.err" || true
      if ! jq -e -s 'length == 1 and .[0].error == {type: "RecursionError", message: "maximum recursion depth exceeded while calling a Python object"}' \
          "$work/$step.stock.jsonl" > /dev/null 2>&1; then
        echo "FAIL $surface $dir: step $steps ($line): the call-budget seam decoded, but the stock python dump does not end with its RecursionError"
        head -c 2000 "$work/$step.stock.jsonl" "$work/$step.stock.err" | sed 's/^/     /'
        failed=1
        break
      fi
      budget_steps=$((budget_steps + 1))
    fi
    if ! cmp -s "$work/$step.py.jsonl" "$work/$step.cs.jsonl"; then
      jq -c --slurpfile cs "$work/$step.cs.jsonl" '$cs[0] as $cs | '"$SCHEDULE_DECODE_TEXT" "$work/$step.py.jsonl" > "$work/$step.py.decode-text"
      if ! cmp -s "$work/$step.py.decode-text" <(jq -c . "$work/$step.py.jsonl") \
          && [[ "$(jq -S -c . "$work/$step.py.decode-text")" == "$(jq -S -c . "$work/$step.cs.jsonl")" ]]; then
        divergences=$((divergences + 1))
      else
        diff -u <(jq . "$work/$step.py.jsonl") <(jq . "$work/$step.cs.jsonl") > "$work/$step.diff" || true
        echo "FAIL $surface $dir: step $steps ($line) differs"
        head -c 20000 "$work/$step.diff"
        echo
        failed=1
        break
      fi
    fi
    for side in py cs; do
      if jq -e '.state == null' "$work/$step.$side.jsonl" > /dev/null; then
        rm -f "$work/$tag.$side.state"
      else
        jq -j '.state' "$work/$step.$side.jsonl" > "$work/$tag.$side.state"
      fi
    done
  done < "$dir/steps"
  files_total=$((files_total + steps))
  if [[ "$failed" -eq 0 && "$schedule_call_budget" -eq 1 && "$budget_steps" -eq 0 ]]; then
    failed=1
    echo "FAIL $surface $dir: declares json-call-budget but no step decoded through the seam"
  fi
  if [[ "$failed" -eq 1 ]]; then
    status=1
  elif [[ "$steps" -eq 0 ]]; then
    status=1
    echo "FAIL $surface $dir: no steps"
  else
    divergences=$((divergences + budget_steps))
    expected_total=$((expected_total + divergences))
    if [[ "$divergences" -gt 0 ]]; then
      echo "ok   $surface $dir: $steps steps, $divergences expected divergences (JSONDecodeError text $((divergences - budget_steps)), json call budget $budget_steps)"
    else
      echo "ok   $surface $dir: $steps steps"
    fi
  fi
}

# phase6_case_dirs <listing file> <surface root> <marker>...: writes every directory at any depth below the root that holds one of the
# marker files (a case), outside workdir/ trees, in C-locale order. A directory that is neither a case, inside one, nor above one (an
# empty group) is reported and fails the listing, so a misplaced case is never silently skipped.
phase6_case_dirs() {
  local listing="$1" root="$2" marker dir case_dir held bad=0
  shift 2
  local names=()
  for marker in "$@"; do
    names+=(${names[@]:+-o} -name "$marker")
  done
  find "$root" -type d -name workdir -prune -o -type f \( "${names[@]}" \) -print | while IFS= read -r dir; do dirname "$dir"; done \
    | LC_ALL=C sort -u > "$listing"
  while IFS= read -r dir; do
    held=0
    while IFS= read -r case_dir; do
      if [[ "$case_dir" == "$dir" || "$case_dir" == "$dir/"* || "$dir" == "$case_dir/"* ]]; then
        held=1
        break
      fi
    done < "$listing"
    if [[ "$held" -eq 0 ]]; then
      echo "FAIL $surface $dir: holds no case (no $* at any depth)"
      bad=1
    fi
  done < <(find "$root" -mindepth 1 -type d -name workdir -prune -o -type d -print | LC_ALL=C sort)
  return "$bad"
}

# run_profile_declared <case dir> <dump args...>: reads the case's optional "divergences" file (one name per line). The only name is
# python-surrogate-name-lookup (decision R, "Run profiles" in expected_divergences.md): a variable or user name holding an unpaired
# surrogate raises UnicodeEncodeError out of Python's expansion, where the port finds no such variable or user. The stock Python dump must
# end with exactly that error; the case is then compared against a Python dump whose lookups of such names find nothing
# (PARITY_RUN_PROFILE_SURROGATE_NAMES=1, which must report at least one such lookup). Sets run_profile_surrogate_names to 1 or 0.
run_profile_declared() {
  local dir="$1" name tag rc=0
  shift
  run_profile_surrogate_names=0
  [[ -f "$dir/divergences" ]] || return 0
  while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    if [[ "$name" != "python-surrogate-name-lookup" ]]; then
      echo "FAIL $surface $dir: unknown divergence '$name' (known: python-surrogate-name-lookup)"
      return 1
    fi
    run_profile_surrogate_names=1
  done < "$dir/divergences"
  tag="run-profile-stock-$(echo "$dir" | tr '/ ' '__')"
  PARITY_SECRETS_TIMEOUT="$secrets_timeout" PYTHONPATH="$repo_root/src" python tools/parity/py_dump.py run-profile \
    "$dir/profile.json" "$dir/workdir" --scratch "$work/run-profile-scratch" "$@" > "$work/$tag.jsonl" 2> "$work/$tag.err" || rc=$?
  if [[ "$rc" -ne 0 ]] || ! jq -e -s 'length == 1 and .[0].error.type == "UnicodeEncodeError"
      and (.[0].error.message | test("^.utf-8. codec can.t encode character .\\\\ud[89a-f][0-9a-f]{2}. in position [0-9]+: surrogates not allowed$"))' \
      "$work/$tag.jsonl" > /dev/null; then
    echo "FAIL $surface $dir: declares python-surrogate-name-lookup but the stock python dump does not end with that UnicodeEncodeError"
    head -c 2000 "$work/$tag.jsonl" "$work/$tag.err" | sed 's/^/     /'
    return 1
  fi
}

run_phase6_surface() {
  local payload dir listing
  listing="$work/phase6-listing"
  case "$surface" in
    profile-store)
      # The text-field normaliser must replace only at text-field positions: a stringified metadata value under a text field's name fails.
      if python tools/parity/profile_text_fields.py --self-test > "$work/profile-text-fields.self-test" 2>&1; then
        sed 's/^/ok   profile-store harness /' "$work/profile-text-fields.self-test"
      else
        status=1
        echo "FAIL profile-store: harness self-test of profile_text_fields.py"
        sed 's/^/     /' "$work/profile-text-fields.self-test"
      fi
      # Payloads at any depth outside diff/ directories; diff pairs are the <case>/ directories below any diff/ directory.
      while IFS= read -r payload; do
        local extra=() index
        [[ -f "${payload%.json}.args" ]] && mapfile -t extra < "${payload%.json}.args"
        # An argument line "json:<JSON string>" is that string decoded, so an argument can hold a line break.
        for index in "${!extra[@]}"; do
          if [[ "${extra[$index]}" == json:* ]]; then
            extra[index]="$(jq -j -n --argjson value "${extra[$index]#json:}" '$value')"
          fi
        done
        compare_records "$payload" profile-store profile-store "$payload" "${extra[@]}" "$@"
      done < <(find tools/parity/cases/profile-store -type d -name diff -prune -o -type f -name '*.json' -print | LC_ALL=C sort)
      while IFS= read -r dir; do
        if [[ ! -f "$dir/baseline.json" || ! -f "$dir/current.json" ]]; then
          status=1
          echo "FAIL $surface $dir: a diff case needs baseline.json and current.json"
          continue
        fi
        compare_records "$dir" profile-diff none "$dir/baseline.json" "$dir/current.json" "$@"
      done < <(find tools/parity/cases/profile-store -type d -path '*/diff/*' -prune -print | LC_ALL=C sort)
      ;;
    run-profile)
      if ! phase6_case_dirs "$listing" tools/parity/cases/run-profile profile.json; then
        status=1
      fi
      while IFS= read -r dir; do
        if ! run_profile_declared "$dir" "$@"; then
          status=1
          continue
        fi
        PARITY_RUN_PROFILE_SURROGATE_NAMES="$run_profile_surrogate_names" \
          compare_records "$dir" run-profile run-profile "$dir/profile.json" "$dir/workdir" --scratch "$work/run-profile-scratch" "$@"
      done < "$listing"
      ;;
    schedule)
      if ! schedule_decode_text_self_test; then
        status=1
      fi
      if ! phase6_case_dirs "$listing" tools/parity/cases/schedule steps config.json; then
        status=1
      fi
      while IFS= read -r dir; do
        compare_schedule "$dir" "$@"
      done < "$listing"
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
  make_dotdot_tree "$gen/dotdot"
  compare "generated/root with .. after a symlink" "$gen/dotdot/shortcut/.." "$@"
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
elif [[ "$surface" == "multi-server" ]]; then
  run_multi_server_surface "$@"
elif [[ "$surface" == "profile-store" || "$surface" == "run-profile" || "$surface" == "schedule" ]]; then
  run_phase6_surface "$@"
else
  run_phase4_surface "$@"
fi

echo "== $surface summary: $files_total files compared, $expected_total expected divergences, $([[ $status -eq 0 ]] && echo PASS || echo FAIL)"
exit "$status"
