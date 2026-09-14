"""Fixed Python against the port for a multi-server case declaring ``"divergences": ["python-surrogate-encode"]``.

Temporary; deleted together with the Python package once the C# port is proven.

Usage:
    multi_server_surrogates.py <case.json> <fixed dump> <fixed records> <port dump>

Python raises ``UnicodeEncodeError`` ("surrogates not allowed") where a str holding an unpaired surrogate reaches a strict UTF-8
encode; the port keeps working (expected_divergences.md, "Python raises on unpaired surrogates (not reproduced)"). The fixed dump
then differs from the port's in what the raise took away, and only there:

* A host that went offline on that encode (``Scan failed: 'utf-8' codec can't encode ...: surrogates not allowed``). The port
  must have scanned it (``succeeded`` / ``found``); its result and the final progress line of its plan are not compared. Every
  entry the host holds in the port (a present drilldown row) is dropped from both sides; in every other entry the host's row is
  compared without ``status`` and ``presence_status`` (Python's availability is ``offline``, the port's ``found``). The summary's
  ``configs_evaluated`` and ``drifting_configs`` are not compared once an entry was dropped. Cache entries named by the host
  (``sha1("{host}:{config}")`` over the ids either run assigned it, a surrogate encoded as U+FFFD as the port encodes it) are
  not compared: Python's write truncated the entry before its encode raised. Everything else is compared exactly, key order
  included.
* A ``run()`` that raised (a label heading a diff): the fixed records hold the progress, host results and cache entries the run
  left before the raise, which must equal the port's, with the offline hosts above excluded the same way (their results, final
  progress lines and cache entries); the port's catalog, drilldown and summary have no Python counterpart. When the raise ended
  an earlier run than the case's last (``--runs 2``, run 1 raised, so Python never started run 2), the harness passes the port
  dump of that run (the port over the same fresh cache with ``--runs`` set to the run that raised) and the later runs have no
  Python counterpart.
* ``_build_plans`` that raised (``os.path.expanduser`` of ``~<name>`` or ``os.path.expandvars`` of ``${<name>}`` encoding a name
  holding an unpaired surrogate): Python produced no response at all, so the port's is not compared with a Python response.
  The raise must be the one the case's first such root gives. The port must have left out exactly the roots holding an unpaired
  surrogate: a plan with another root (every such root must exist) succeeded (``found``) and its result lists exactly those
  roots, spelled as the runner spells them; a plan with none failed ``not_found`` with ``No accessible roots.`` and lists every
  plan root, as ``run()`` reports a plan none of whose roots exists.

Any other difference is printed to stderr and exits 1; each divergence is one stdout line.
"""

from __future__ import annotations

import copy
import hashlib
import json
import os
import re
import sys
from pathlib import Path

# A Python str holds a surrogate code point only unpaired (a pair is one astral code point), as in py_dump.
_LONE_SURROGATE = re.compile("[\ud800-\udfff]")
# How both dumps spell one (py_dump._escape_lone_surrogates).
_ESCAPED_SURROGATE = re.compile(r"\\ud[89a-f][0-9a-f]{2}")


def _load(path: str) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def _escape(text: str) -> str:
    """``py_dump._escape_lone_surrogates`` for one str: how both dumps spell a lone surrogate."""

    return _LONE_SURROGATE.sub(lambda match: f"\\u{ord(match.group()):04x}", text)


def _same(left: object, right: object) -> bool:
    """Equal values with equal key order at every level."""

    return json.dumps(left, ensure_ascii=False) == json.dumps(right, ensure_ascii=False)


def _is_surrogate_encode(message: object) -> bool:
    return isinstance(message, str) and "codec can't encode character" in message and "surrogates not allowed" in message


def _progress_plans(entries: list[dict]) -> list[int]:
    plans, index = [], -1
    for entry in entries:
        index += entry["status"] == "running"
        plans.append(index)
    return plans


def _masked_progress(entries: list[dict], quiet: set[int]) -> list[dict]:
    """The progress lines with the status and message of every final line of the ``quiet`` plans blanked."""

    return [
        {**entry, "status": None, "message": None} if plan in quiet and entry["status"] != "running" else entry
        for entry, plan in zip(entries, _progress_plans(entries), strict=True)
    ]


def _entry_name(host: str, config_id: str) -> str:
    text = f"{host}:{config_id}"
    return hashlib.sha1(_LONE_SURROGATE.sub("\ufffd", text).encode("utf-8")).hexdigest() + ".json"


def _runs(case: dict) -> int:
    args = [str(arg) for arg in case.get("args") or []]
    for index, arg in enumerate(args):
        if arg == "--runs" and index + 1 < len(args):
            return int(args[index + 1])
        if arg.startswith("--runs="):
            return int(arg.split("=", 1)[1])
    return 1


def _offline_plans(f_results: list[dict], p_results: list[dict], problems: list[str], divergences: list[str]) -> set[int]:
    """The plans whose Python host went offline on a surrogate encode, each of which the port must have scanned; every other
    result must be equal."""

    offline: set[int] = set()
    for index, (f, p) in enumerate(zip(f_results, p_results, strict=True)):
        if f["status"] == "failed" and f["availability"] == "offline" and _is_surrogate_encode(f["message"]):
            offline.add(index)
            if (p["status"], p["availability"]) != ("succeeded", "found"):
                problems.append(f"host {f['host_id']} went offline on a surrogate encode in Python but the port did not scan it: {p}")
            divergences.append(f"host {f['host_id']}: python '{f['message']}', port {p['status']} '{p['message']}'")
        elif not _same(f, p):
            problems.append(f"host {f['host_id']} (plan {index}) result differs: python {f} port {p}")
    return offline


def _pairs(response: dict) -> dict[str, tuple[dict, dict]]:
    return {entry["config_id"]: (entry, drill) for entry, drill in zip(response["catalog"], response["drilldown"], strict=True)}


def _offline_hosts(results: list[dict], offline: set[int]) -> set[str]:
    """The host ids whose last plan went offline: the catalog and drilldown hold a host id's last plan."""

    last = {result["host_id"]: index for index, result in enumerate(results)}
    return {host for host, index in last.items() if index in offline}


def _held(pairs: dict[str, tuple[dict, dict]], hosts: set[str]) -> set[str]:
    return {
        config_id for config_id, (_, drill) in pairs.items() if any(row["present"] and row["host_id"] in hosts for row in drill["servers"])
    }


def _offline_cache(case: dict, records: dict, offline: set[int], results: list[dict], p_pairs: dict, held: set[str],
                   port_cache: list[dict], problems: list[str]) -> set[str]:
    """The cache entry names of the offline plans: every id the Python run assigned them, and every entry an offline host holds
    in the port (each of which the port cache must contain)."""

    raw_hosts = {_escape(str(plan.get("host_id"))): str(plan.get("host_id")) for plan in case.get("plans") or [] if isinstance(plan, dict)}
    offline_ids = {results[index]["host_id"] for index in offline}
    names = {
        _entry_name(raw_hosts.get(record["host_id"], record["host_id"]), record["config_id"])
        for record in records["records"] if record["plan_index"] in offline
    }
    port_names = {
        _entry_name(raw_hosts.get(row["host_id"], row["host_id"]), config_id)
        for config_id in held for row in p_pairs[config_id][1]["servers"] if row["present"] and row["host_id"] in offline_ids
    }
    p_files = {entry["file"] for entry in port_cache}
    if not port_names <= p_files:
        problems.append(f"the port cache lacks entries of the offline hosts: {sorted(port_names - p_files)}")
    return names | port_names


def _compare_cache(f_cache: list[dict], p_cache: list[dict], names: set[str], problems: list[str], divergences: list[str]) -> None:
    f_kept = [entry for entry in f_cache if entry["file"] not in names]
    p_kept = [entry for entry in p_cache if entry["file"] not in names]
    if not _same(f_kept, p_kept):
        problems.append("cache entries differ outside the offline hosts")
    if names:
        skipped = (len(f_cache) - len(f_kept), len(p_cache) - len(p_kept))
        divergences.append(f"cache: {skipped[0]} python and {skipped[1]} port entries of offline hosts not compared")


def check_offline(case: dict, fixed: dict, records: dict, port: dict, problems: list[str]) -> list[str]:
    divergences: list[str] = []
    f_response, p_response = fixed["response"], port["response"]
    f_results, p_results = f_response["results"], p_response["results"]
    if [result["host_id"] for result in f_results] != [result["host_id"] for result in p_results]:
        problems.append("host results are not in the same order")
        return divergences
    if not _same(f_response["version"], p_response["version"]):
        problems.append("version differs")

    offline = _offline_plans(f_results, p_results, problems, divergences)
    if not offline:
        problems.append("the case declares python-surrogate-encode but no host went offline on a surrogate encode")
        return divergences
    offline_hosts = _offline_hosts(f_results, offline)
    if not _same(_masked_progress(fixed["progress"], offline), _masked_progress(port["progress"], offline)):
        problems.append("progress differs outside the final lines of the offline hosts")

    f_pairs, p_pairs = _pairs(f_response), _pairs(p_response)
    held = _held(p_pairs, offline_hosts)
    for config_id in sorted(_held(f_pairs, offline_hosts)):
        problems.append(f"python entry {config_id} holds a host that went offline")
    for config_id in sorted(held):
        side = "in" if config_id in f_pairs else "not in"
        divergences.append(f"config {config_id}: held by an offline host in the port, not compared ({side} python)")
    f_ids = [config_id for config_id in f_pairs if config_id not in held]
    p_ids = [config_id for config_id in p_pairs if config_id not in held]
    if f_ids != p_ids:
        problems.append(f"compared config ids differ: python {f_ids} port {p_ids}")
    else:
        for config_id in f_ids:
            f_catalog, f_drill = copy.deepcopy(f_pairs[config_id])
            p_catalog, p_drill = copy.deepcopy(p_pairs[config_id])
            for drill in (f_drill, p_drill):
                for row in drill["servers"]:
                    if row["host_id"] in offline_hosts:
                        row.pop("status", None)
                        row.pop("presence_status", None)
            if not _same(f_catalog, p_catalog):
                problems.append(f"catalog entry {config_id} differs")
            if not _same(f_drill, p_drill):
                problems.append(f"drilldown entry {config_id} differs")

    f_summary, p_summary = dict(f_response["summary"]), dict(p_response["summary"])
    if held:
        for key in ("configs_evaluated", "drifting_configs"):
            f_summary.pop(key, None)
            p_summary.pop(key, None)
    if not _same(f_summary, p_summary):
        problems.append(f"summary differs: python {f_summary} port {p_summary}")

    names = _offline_cache(case, records, offline, f_results, p_pairs, held, port["cache"], problems)
    _compare_cache(fixed["cache"], port["cache"], names, problems, divergences)
    return divergences


def check_abort(case: dict, records: dict, port: dict, problems: list[str]) -> list[str]:
    abort = records["abort"]
    if abort["type"] != "UnicodeEncodeError" or not _is_surrogate_encode(abort["message"]):
        problems.append(f"the fixed python run raised {abort['type']}: {abort['message']}, not a surrogate encode")
        return []
    divergences: list[str] = []
    response = port["response"]
    f_results, p_results = abort["results"], response["results"]
    if [result["host_id"] for result in f_results] != [result["host_id"] for result in p_results]:
        problems.append(f"host results before the raise are not the port's: python {f_results} port {p_results}")
        return divergences
    offline = _offline_plans(f_results, p_results, problems, divergences)
    if not _same(_masked_progress(abort["progress"], offline), _masked_progress(port["progress"], offline)):
        problems.append("progress before the raise differs" + (" outside the final lines of the offline hosts" if offline else ""))
    p_pairs = _pairs(response)
    held = _held(p_pairs, _offline_hosts(p_results, offline))
    names = _offline_cache(case, records, offline, f_results, p_pairs, held, port["cache"], problems) if offline else set()
    _compare_cache(abort["cache"], port["cache"], names, problems, divergences)
    divergences.append(
        f"python run() raised UnicodeEncodeError ({abort['message']}); the port's catalog ({len(response['catalog'])} entries),"
        " drilldown and summary not compared"
    )
    runs = _runs(case)
    if abort["run"] < runs:
        divergences.append(f"python raised in run {abort['run']} of {runs} and never started the rest; the port's later runs not compared")
    return divergences


def check_build_abort(case: dict, records: dict, port: dict, problems: list[str]) -> list[str]:
    abort = records["abort"]
    if abort["type"] != "UnicodeEncodeError" or not _is_surrogate_encode(abort["message"]):
        problems.append(f"python _build_plans raised {abort['type']}: {abort['message']}, not a surrogate encode")
        return []
    plans = [plan for plan in case.get("plans") or [] if isinstance(plan, dict)]
    results = port["response"]["results"]
    if len(results) != len(plans):
        problems.append(f"the port answered {len(results)} host result(s) for {len(plans)} plan(s)")
        return []

    trigger = None
    dropped = 0
    for index, (plan, result) in enumerate(zip(plans, results, strict=True)):
        host_id = _escape(str(plan.get("host_id") or "").strip())
        if result["host_id"] != host_id:
            problems.append(f"plan {index}: the port's host id is {result['host_id']!r}, the plan's {host_id!r}")
        spelled: list[str] = []
        kept: list[str] = []
        for entry in plan.get("roots") or []:
            text = str(entry or "").strip()
            if not text:
                continue
            try:
                expanded = os.path.expanduser(os.path.expandvars(text))
            except UnicodeEncodeError as exc:
                trigger = trigger or (index, text, str(exc))
                expanded = text
            root = str(Path(expanded))
            root = _escape(os.path.relpath(root) if os.path.isabs(root) else root)
            spelled.append(root)
            if _LONE_SURROGATE.search(expanded):
                dropped += 1
                continue
            if not Path(expanded).exists():
                problems.append(f"plan {index}: root {text!r} does not exist; a _build_plans abort case compares only existing roots")
            kept.append(root)
        # A plan with a root left lists the roots it scanned; one without lists every plan root, as run() reports a plan none of
        # whose roots exists.
        if kept:
            wanted = {"roots": kept, "status": "succeeded", "availability": "found"}
        else:
            wanted = {"roots": spelled, "status": "failed", "availability": "not_found", "message": "No accessible roots."}
        answered = {key: result.get(key) for key in wanted}
        if answered != wanted:
            problems.append(f"plan {index} ({host_id}): the port answered {answered}, expected {wanted}")
        elif kept and any(_ESCAPED_SURROGATE.search(root) for root in result["roots"]):
            problems.append(f"plan {index}: the port scanned a root holding an unpaired surrogate: {result['roots']}")
    if trigger is None:
        problems.append("python _build_plans raised a surrogate encode, but no root of the case raises one when expanded")
        return []
    if trigger[2] != abort["message"]:
        problems.append(f"python _build_plans raised '{abort['message']}', but the first raising root {trigger[1]!r} raises '{trigger[2]}'")
    return [
        f"python _build_plans raised UnicodeEncodeError ({abort['message']}) expanding root {_escape(trigger[1])!r} of plan {trigger[0]}:"
        f" no response; the port left out {dropped} root(s) holding an unpaired surrogate and scanned the rest"
    ]


def main(argv: list[str]) -> int:
    case = _load(argv[1])
    records = _load(argv[3])
    port = _load(argv[4])
    problems: list[str] = []
    abort = records.get("abort")
    if abort and abort["stage"] == "build_plans":
        divergences = check_build_abort(case, records, port, problems)
    elif abort:
        divergences = check_abort(case, records, port, problems)
    else:
        divergences = check_offline(case, _load(argv[2]), records, port, problems)
    for text in divergences:
        print(f"python-surrogate-encode: {text}")
    for problem in problems:
        print(problem, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
