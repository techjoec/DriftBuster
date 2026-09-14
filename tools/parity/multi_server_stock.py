"""Stock-versus-fixed check of the multi-server parity surface (plan fixes a and b, and the undecodable-name platform limit).

Temporary; deleted together with the Python package once the C# port is proven.

Usage:
    multi_server_stock.py [--self-test] <case.json> <stock dump> <stock records> <fix-b dump> <fix-b records> <fixed dump> <fixed records>

``py_dump.py multi-server`` runs the Python runner three times per case: stock, with fix b alone (``PARITY_MULTI_SERVER_FIXES=b``)
and with fixes a and b (``PARITY_MULTI_SERVER_FIXES=1``), which must equal the port byte for byte. This script proves each step
changes only what its fix explains, and prints one line per expected divergence. Any other difference is printed to stderr and
exits 1. A case whose ``stock_aborts`` names an exception has a stock run that raised out of ``run()`` (a root whose lookup
fails): its step b checks the abort instead (below), and the abort is one fix b divergence.

Step b, stock against fix b, plan by plan. The fix b run's records name the files it skipped (with the cause) and the roots it
refused; the stock run's name the path each failing host's scan raised on and every ``scan_path`` call with its match count.
Every record names its plan's position in the request, so plans sharing a host id never explain each other; the catalog and
drilldown hold a host id's last plan, so a host id's rows are masked only when fix b explains its last plan.

* A host whose results are equal must have no skipped file.
* A fix b host: the stock host failed and the path its scan raised on is a file the fix b run skipped or a root it refused, or the
  fix b host is ``Permission denied: <root>`` for a root it refused that the stock run scanned with no match at all (the stock
  glob silently scans a root directory it cannot list as empty); one divergence.
* Catalog and drilldown entries: each side's aggregates (drift count, severity, present and missing hosts, coverage, secrets,
  masked and validation flags) must equal what its own server rows give (the drift count over unique host ids, the rest per
  row). An entry held by fix b hosts alone, in only one run, or
  whose baseline record is a fix b host's, is dropped from both sides (one divergence per side). In every other entry the rows of
  the fix b hosts present in either run are removed and the aggregates are not compared across the runs (one divergence), nor is
  the diff pane when it shows a fix b host's diff; everything else, the other hosts' rows included, is compared. A fix b host that
  holds no configuration in the entry keeps its row, without ``status`` and ``presence_status`` (its availability).
* An undecodable-name host: the fix b run skipped only files whose names are not UTF-8, both runs succeeded, and the stock run's
  records hold exactly those files; the result message (its count) and the entries of those files are not compared (one
  divergence each). Every other entry is compared whole, the host included.
* Everything else is compared exactly: every other host result, the summary (its counts only once an entry was dropped or a
  masked drift count changed) and the progress lines (a fix b host's final line excepted).
* A stock run that aborted (``stock_aborts``): the recorded exception must be the declared type, raised on a root the fix b run
  refused while that root's host was being scanned, and every progress line stock emitted before it must equal the fix b run's
  line, except the final line of a host fix b explains as above.

Step a, fix b against fixed, both with fix b's skips (the skipped files and refused roots must be identical):

* Over the plans both runs scanned (both succeeded), a fix b id is clean when its files are exactly the files of one fixed id; a
  clean entry is renamed to that id (``config_id``, every ``{label}:{id}`` string, and the ``---`` / ``+++`` header lines of the
  unified diff) and compared with the fixed entry, catalog and drilldown re-sorted by id. Every other id collided (files of
  different fixed ids shared it, or one file set was split): its entries, and the fixed entries its files went to, are dropped
  (each a divergence). Exemptions are per plan, never per host id: a plan whose own scan gave two files one fix b id, and only
  such a plan, may show a fix b result message counting fewer configurations with the same suffix (one divergence; its final
  progress line is not compared either), and, when the cache already held the host's entries (``--runs`` above 1, or an earlier
  plan of the same host id), ``used_cache`` false in fix b where the fixed run has true: the collided stock ids shared one cache
  entry (one divergence). Any other difference in a result, those fields included, is a problem.
* The summary's ``configs_evaluated`` and ``drifting_configs`` are not compared once an entry was dropped. The cache listing is
  not compared: its file names and signatures are functions of the config id, and the fixed run's cache is compared with the
  port byte for byte.

A case that declares a fix (``a``, ``b``, ``undecodable-name``) in its ``fixes`` list must show at least one divergence of it; a
case that does not must show none.

``--self-test`` runs the case's ``self_test`` instead: ``wrong-drift-count`` requires the untampered runs to pass and, on an entry
where plans share a host id, a stock drift count one below the right one, a drift count counted per drifting row, and a wrong
severity to be rejected, each tamper on its own; ``wrong-used-cache`` requires the untampered runs to pass and ``used_cache``
flipped in the fixed dump to be rejected on every plan outside the step a exemption, among them a failed plan and a succeeded
plan without a collision of one host id planned more than once.
"""

from __future__ import annotations

import copy
import json
import os
import re
import sys
from pathlib import Path

FIXES = ("a", "b", "undecodable-name")
_ESCAPED_SURROGATE = re.compile(r"\\ud[89a-f][0-9a-f]{2}")


def _load(path: str) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def _rename(entry: object, old: str, new: str, labels: list[str]) -> object:
    targets = {f"{label}:{old}": f"{label}:{new}" for label in labels}
    headers = {f"{prefix} {key}": f"{prefix} {value}" for key, value in targets.items() for prefix in ("---", "+++")}

    def walk(value: object, key: str | None = None) -> object:
        if isinstance(value, dict):
            return {name: walk(item, name) for name, item in value.items()}
        if isinstance(value, list):
            return [walk(item) for item in value]
        if isinstance(value, str):
            if key == "config_id" and value == old:
                return new
            if key == "unified_diff":
                lines = value.split("\n")
                for index in range(min(2, len(lines))):
                    lines[index] = headers.get(lines[index], lines[index])
                return "\n".join(lines)
            return targets.get(value, value)
        return value

    return walk(copy.deepcopy(entry))


def _compare_entries(problems: list[str], name: str, s_part: dict, f_part: dict) -> None:
    if s_part != f_part:
        keys = sorted(key for key in set(s_part) | set(f_part) if s_part.get(key) != f_part.get(key))
        problems.append(f"{name} entry {f_part.get('config_id')} differs in {keys}")


def _fix_b_explains(plan: int, stock_status: str, bonly_message: str, raised: dict[int, str], skips: list[dict],
                    refused: set[tuple[int, str]], scans: list[dict]) -> str | None:
    """Why a plan's stock and fix b outcomes differ, or None when fix b does not explain it: stock failed on exactly a path the fix
    b run skipped or a root it refused; or fix b refused a root the stock run scanned without a single match (``Path.glob``
    swallows the PermissionError of a root directory that cannot be listed) and the fix b message names that root. Everything is
    matched by the plan's position in the request, so plans sharing a host id never explain each other."""

    path = raised.get(plan)
    if stock_status == "failed" and path is not None and (any(entry["path"] == path for entry in skips) or (plan, path) in refused):
        return "failed-on-skip"
    if stock_status == "succeeded":
        for refused_plan, root in refused:
            # Result messages carry the root respelled relative to the working directory (py_dump._respell_roots); progress does not.
            spellings = {f"Permission denied: {root}", f"Permission denied: {os.path.relpath(root) if os.path.isabs(root) else root}"}
            silent = [scan for scan in scans if scan["plan_index"] == plan and scan["root"] == root]
            if refused_plan == plan and bonly_message in spellings and silent and all(scan["matches"] == 0 for scan in silent):
                return "silent-root"
    return None


def _by_plan(entries: list[dict]) -> dict[int, list[dict]]:
    grouped: dict[int, list[dict]] = {}
    for entry in entries:
        grouped.setdefault(entry["plan_index"], []).append(entry)
    return grouped


def _last_plans(results: list[dict], plans: set[int]) -> set[str]:
    """The host ids whose last plan is one of ``plans``: that plan's configs, availability and label are the ones the catalog and
    drilldown hold for the host id (``_build_catalog_and_drilldown`` keys them by host id, later plans overwriting earlier ones)."""

    last = {result["host_id"]: index for index, result in enumerate(results)}
    return {host for host, index in last.items() if index in plans}


def _holder(drilldown: dict, baseline_host_id: str) -> str | None:
    """The host whose record is the entry's baseline: the global baseline when it holds the entry, else the smallest host id."""

    present = [server["host_id"] for server in drilldown["servers"] if server["present"]]
    if baseline_host_id in present:
        return baseline_host_id
    return min(present) if present else None


def _pane_host(drilldown: dict, baseline_host_id: str) -> str | None:
    """The host whose diff the drilldown pane shows: the global baseline's self-diff when it holds the entry, else the first
    present host in plan order."""

    present = [server["host_id"] for server in drilldown["servers"] if server["present"]]
    if baseline_host_id in present:
        return baseline_host_id
    return present[0] if present else None


_AGGREGATES = ("drift_count", "severity", "present_hosts", "missing_hosts", "has_secrets", "has_masked_tokens",
               "has_validation_issues", "coverage_status")
_PANE = ("diff_before", "diff_after", "unified_diff", "diff_summary")


def _check_aggregates(problems: list[str], name: str, catalog: dict, drilldown: dict) -> None:
    """Every per-entry aggregate recomputed from the entry's own server rows, as ``_build_catalog_and_drilldown`` derives it: an
    entry whose rows are masked keeps a proven relation to the rows that are compared."""

    rows = drilldown["servers"]
    present = [row for row in rows if row["present"]]
    total = len(rows)
    # drift_stats is keyed by host id: plans sharing a host id share one record and count once; every other aggregate is per plan.
    drift = len({row["host_id"] for row in present if row["drift_lines"] > 0})
    expected = {
        "drift_count": drift,
        "severity": "high" if drift >= max(1, total // 2) else ("medium" if drift > 0 else "none"),
        "present_hosts": [row["label"] for row in present],
        "missing_hosts": [row["label"] for row in rows if not row["present"]],
        "has_secrets": any(row["has_secrets"] for row in present),
        "has_masked_tokens": any(row["masked"] for row in present),
        "has_validation_issues": len(present) != total,
        "coverage_status": "missing" if not present else ("partial" if len(present) != total else "full"),
    }
    for key, value in expected.items():
        if catalog.get(key) != value:
            problems.append(f"{name} catalog entry {catalog['config_id']} {key} is {catalog.get(key)!r}, its rows give {value!r}")
    for key in ("drift_count", "has_secrets", "has_masked_tokens", "has_validation_issues"):
        if drilldown.get(key) != catalog.get(key):
            problems.append(f"{name} drilldown entry {drilldown['config_id']} {key} differs from its catalog entry")


def check_abort(case: dict, stock_records: dict, bonly: dict, bonly_records: dict, problems: list[str]) -> list[tuple[str, str]]:
    """Step b for a stock run that raised out of ``run()``: the exception is the declared type, on a root the fix b run refused,
    raised while the host that owns that root was being scanned; every progress line stock emitted before it equals the fix b
    run's, except the final line of a host fix b explains."""

    abort = stock_records.get("abort")
    if not abort:
        problems.append("step b: the stock run was declared to abort but recorded no exception")
        return []
    refused = {(entry["plan_index"], entry["path"]) for entry in bonly_records["root_errors"]}
    s_progress, b_progress = abort["progress"], bonly["progress"]
    host = s_progress[-1]["host_id"] if s_progress and s_progress[-1]["status"] == "running" else None
    aborted_plan = len(abort["results"])
    if abort["type"] != case["stock_aborts"].split(":")[0] or host is None or (aborted_plan, abort["filename"]) not in refused:
        problems.append(f"step b: stock aborted with {abort['type']} on {abort['filename']!r} while scanning {host}, which is not a"
                        f" {case['stock_aborts']} on a root fix b refused")
        return []
    skipped = _by_plan(bonly_records["skipped"])
    raised = {entry["plan_index"]: entry["path"] for entry in stock_records["stock_errors"]}
    explained: set[str] = set()
    if len(b_progress) < len(s_progress):
        problems.append("step b: the fix b run emitted fewer progress lines than stock did before aborting")
        return []
    s_plans, b_plans = _progress_plans(s_progress), _progress_plans(b_progress)
    for s_line, b_line, s_plan, b_plan in zip(s_progress, b_progress, s_plans, b_plans, strict=False):
        if s_line == b_line:
            continue
        line_host = s_line["host_id"]
        reason = (
            _fix_b_explains(s_plan, s_line["status"], b_line["message"], raised, skipped.get(s_plan, []), refused,
                            stock_records["scans"])
            if s_plan == b_plan and "running" not in (s_line["status"], b_line["status"]) else None
        )
        if reason is None:
            problems.append(f"step b: progress before the stock abort differs and fix b does not explain it: stock {s_line} fix b {b_line}")
        else:
            explained.add(line_host)
    compared = len({line["host_id"] for line in s_progress[:-1]})
    divergences = [("b", f"stock run aborted with {abort['type']} on refused root {abort['filename']} of host {host}; progress of"
                         f" {compared} earlier host(s) compared, {len(explained)} explained by fix b")]
    return divergences


def check_b(stock: dict, stock_records: dict, bonly: dict, bonly_records: dict, problems: list[str]) -> list[tuple[str, str]]:
    divergences: list[tuple[str, str]] = []
    s_response, b_response = stock["response"], bonly["response"]
    s_results, b_results = s_response["results"], b_response["results"]
    if [r["host_id"] for r in s_results] != [r["host_id"] for r in b_results]:
        problems.append("step b: host results are not in the same order")
        return divergences
    skipped = _by_plan(bonly_records["skipped"])
    refused = {(entry["plan_index"], entry["path"]) for entry in bonly_records["root_errors"]}
    raised = {entry["plan_index"]: entry["path"] for entry in stock_records["stock_errors"]}

    fix_b_plans: set[int] = set()
    undecodable_plans: set[int] = set()
    for index, (s, b) in enumerate(zip(s_results, b_results, strict=True)):
        host = b["host_id"]
        plan_skips = skipped.get(index, [])
        if s == b:
            if plan_skips:
                problems.append(f"step b: fix b skipped {len(plan_skips)} file(s) on host {host} (plan {index}), whose result equals stock")
            continue
        causes = {entry["cause"] for entry in plan_skips}
        if causes == {"undecodable-name"} and s["status"] == b["status"] == "succeeded":
            undecodable_plans.add(index)
            divergences.append(("undecodable-name", f"host {host} evaluated count: stock '{s['message']}', fix b '{b['message']}'"))
            if {**s, "message": None} != {**b, "message": None}:
                problems.append(f"step b: host {host} result differs beyond its evaluated count")
            continue
        if _fix_b_explains(index, s["status"], b["message"], raised, plan_skips, refused, stock_records["scans"]) is not None:
            fix_b_plans.add(index)
            divergences.append(("b", f"host {host} result: stock {s['status']} '{s['message']}', fix b {b['status']} '{b['message']}'"))
        else:
            problems.append(
                f"step b: host {host} (plan {index}) result differs and no skipped file or refused root explains it: stock {s} fix b {b}"
            )
    fix_b_hosts = _last_plans(b_results, fix_b_plans)

    undecodable_ids = {
        record["config_id"] for record in stock_records["records"]
        if record["plan_index"] in undecodable_plans and _ESCAPED_SURROGATE.search(record["relative_path"])
    }
    baseline = b_response["summary"]["baseline_host_id"]
    s_pairs = {entry["config_id"]: (entry, drill) for entry, drill in zip(s_response["catalog"], s_response["drilldown"], strict=True)}
    b_pairs = {entry["config_id"]: (entry, drill) for entry, drill in zip(b_response["catalog"], b_response["drilldown"], strict=True)}
    for name, pairs in (("stock", s_pairs), ("fix b", b_pairs)):
        for catalog, drilldown in pairs.values():
            _check_aggregates(problems, f"step b {name}", catalog, drilldown)
    dropped = False
    for config_id in sorted(set(s_pairs) | set(b_pairs)):
        if config_id in undecodable_ids:
            dropped = True
            if config_id in s_pairs:
                divergences.append(("undecodable-name", f"stock config {config_id} from a name that is not UTF-8, not compared"))
            if config_id in b_pairs:
                divergences.append(("undecodable-name", f"fix b config {config_id} from a name that is not UTF-8, not compared"))
            continue
        sides = [pairs[config_id] for pairs in (s_pairs, b_pairs) if config_id in pairs]
        holders = {_holder(drilldown, baseline) for _, drilldown in sides}
        present_hosts = {row["host_id"] for _, drilldown in sides for row in drilldown["servers"] if row["present"]}
        if len(sides) == 1 or holders & fix_b_hosts:
            if not present_hosts & fix_b_hosts:
                problems.append(f"step b: config {config_id} is not in both runs and holds no fix b host")
            dropped = True
            for side, pairs in (("stock", s_pairs), ("fix b", b_pairs)):
                if config_id in pairs:
                    text = f"{side} config {config_id}: held by fix b hosts alone or on a fix b baseline record, not compared"
                    divergences.append(("b", text))
            continue
        (s_catalog, s_drill), (b_catalog, b_drill) = sides
        masked = present_hosts & fix_b_hosts
        s_catalog, s_drill, b_catalog, b_drill = (copy.deepcopy(item) for item in (s_catalog, s_drill, b_catalog, b_drill))
        for drill in (s_drill, b_drill):
            rows = []
            for row in drill["servers"]:
                if row["host_id"] in masked:
                    continue
                if row["host_id"] in fix_b_hosts:
                    row.pop("status", None)
                    row.pop("presence_status", None)
                rows.append(row)
            drill["servers"] = rows
        if masked:
            if s_catalog["drift_count"] != b_catalog["drift_count"]:
                dropped = True
            panes = {_pane_host(drilldown, baseline) for _, drilldown in sides}
            hidden = list(_AGGREGATES) + (list(_PANE) if len(panes) != 1 or panes & fix_b_hosts else [])
            for item in (s_catalog, s_drill, b_catalog, b_drill):
                for key in hidden:
                    item.pop(key, None)
            text = f"config {config_id}: rows of fix b host(s) {sorted(masked)} and the aggregates they feed, not compared"
            divergences.append(("b", text))
        _compare_entries(problems, "step b catalog", s_catalog, b_catalog)
        _compare_entries(problems, "step b drilldown", s_drill, b_drill)
    _compare_summary(problems, "step b", s_response["summary"], b_response["summary"], dropped)

    quiet = fix_b_plans | undecodable_plans
    if _progress(problems, "step b stock", stock["progress"], quiet, len(s_results)) != _progress(
        problems, "step b fix b", bonly["progress"], quiet, len(b_results)
    ):
        problems.append("step b: progress differs outside fix b and undecodable-name hosts")
    return divergences


_EVALUATED = re.compile(r"Evaluated (\d+) configuration\(s\)\.(.*)", re.DOTALL)


def _collided_plans(scanned: set[int], b_keys: dict[tuple, str]) -> set[int]:
    """The plans (both runs succeeded) whose own scan gave two of its files one fix b id: fix a disambiguates ids per scan."""

    return {
        plan for plan in scanned
        if sum(1 for key in b_keys if key[0] == plan) != len({config_id for key, config_id in b_keys.items() if key[0] == plan})
    }


def _fewer_evaluated(bonly_message: str, fixed_message: str) -> bool:
    """A collided plan's messages: the fix b run evaluated fewer configurations, with the same budget suffix."""

    b_match, f_match = _EVALUATED.fullmatch(bonly_message), _EVALUATED.fullmatch(fixed_message)
    return bool(b_match and f_match and int(b_match[1]) < int(f_match[1]) and b_match[2] == f_match[2])


def _runs(case: dict) -> int:
    args = [str(arg) for arg in case.get("args") or []]
    for index, arg in enumerate(args):
        if arg == "--runs" and index + 1 < len(args):
            return int(args[index + 1])
        if arg.startswith("--runs="):
            return int(arg.split("=", 1)[1])
    return 1


def check_a(
    bonly: dict, bonly_records: dict, fixed: dict, fixed_records: dict, problems: list[str], runs: int = 1
) -> list[tuple[str, str]]:
    divergences: list[tuple[str, str]] = []
    for key in ("skipped", "root_errors"):
        if bonly_records[key] != fixed_records[key]:
            problems.append(f"step a: fix b and fixed runs recorded different {key}")
    b_response, f_response = bonly["response"], fixed["response"]
    if b_response["version"] != f_response["version"]:
        problems.append("step a: version differs")
    b_results, f_results = b_response["results"], f_response["results"]
    hosts = [result["host_id"] for result in f_results]
    if [result["host_id"] for result in b_results] != hosts:
        problems.append("step a: host results are not in the same order")
        return divergences
    labels = [result["label"] for result in f_results]
    scanned = {index for index, (b, f) in enumerate(zip(b_results, f_results, strict=True)) if b["status"] == f["status"] == "succeeded"}

    def files(records: list[dict]) -> tuple[dict[str, set], dict[tuple, str]]:
        by_id: dict[str, set] = {}
        by_key: dict[tuple, str] = {}
        for record in records:
            if record["plan_index"] not in scanned:
                continue
            key = (record["plan_index"], record["root_index"], record["relative_path"])
            by_id.setdefault(record["config_id"], set()).add(key)
            by_key[key] = record["config_id"]
        return by_id, by_key

    b_ids, b_keys = files(bonly_records["records"])
    f_ids, f_keys = files(fixed_records["records"])
    if set(b_keys) != set(f_keys):
        problems.append(f"step a: the runs assigned ids to different files: {sorted(set(b_keys) ^ set(f_keys))[:5]}")
    renames: dict[str, str] = {}
    for b_id, keys in b_ids.items():
        targets = {f_keys.get(key) for key in keys}
        if len(targets) == 1:
            (target,) = targets
            if target is not None and f_ids.get(target) == keys:
                renames[b_id] = target
    collided = _collided_plans(scanned, b_keys)

    for index, (b, f) in enumerate(zip(b_results, f_results, strict=True)):
        host = f["host_id"]
        if index in collided:
            b, f = dict(b), dict(f)
            if _fewer_evaluated(b["message"], f["message"]):
                divergences.append(("a", f"host {host} (plan {index}) evaluated count: fix b '{b['message']}', fixed '{f['message']}'"))
                b["message"] = f["message"] = None
            # The cache already holds this host's entries (an earlier run over the same cache, or an earlier plan of the host id in
            # this run): stock ids that collided on the plan shared one cache entry, which the last file written overwrote, so the
            # fix b run misses the cache for the others where the fixed run hits it for every file.
            primed = runs > 1 or any(result["host_id"] == host for result in f_results[:index])
            if primed and b["used_cache"] is False and f["used_cache"] is True:
                text = f"host {host} (plan {index}) used_cache: fix b False, fixed True (collided ids share a cache entry)"
                divergences.append(("a", text))
                b["used_cache"] = f["used_cache"] = None
        if b != f:
            problems.append(f"step a: host {host} (plan {index}) result differs: fix b {b} fixed {f}")

    kept_b, kept_f = [], []
    for catalog, drilldown in zip(b_response["catalog"], b_response["drilldown"], strict=True):
        b_id = catalog["config_id"]
        if b_id not in b_ids:
            problems.append(f"step a: entry {b_id} has no recorded file on a host both runs scanned")
        elif b_id in renames:
            target = renames[b_id]
            kept_b.append((_rename(catalog, b_id, target, labels), _rename(drilldown, b_id, target, labels)))
        else:
            divergences.append(("a", f"fix b config {b_id} ({len(b_ids[b_id])} file(s)) not compared"))
    for catalog, drilldown in zip(f_response["catalog"], f_response["drilldown"], strict=True):
        if catalog["config_id"] in renames.values():
            kept_f.append((catalog, drilldown))
        else:
            divergences.append(("a", f"fixed config {catalog['config_id']} not compared"))
    kept_b.sort(key=lambda pair: pair[0]["config_id"])
    if [pair[0]["config_id"] for pair in kept_b] != [pair[0]["config_id"] for pair in kept_f]:
        problems.append("step a: compared config ids differ")
    else:
        for (b_catalog, b_drill), (f_catalog, f_drill) in zip(kept_b, kept_f, strict=True):
            _compare_entries(problems, "step a catalog", b_catalog, f_catalog)
            _compare_entries(problems, "step a drilldown", b_drill, f_drill)
    _compare_summary(problems, "step a", b_response["summary"], f_response["summary"], bool(divergences))

    if _progress(problems, "step a fix b", bonly["progress"], collided, len(b_results)) != _progress(
        problems, "step a fixed", fixed["progress"], collided, len(f_results)
    ):
        problems.append("step a: progress differs outside fix a hosts")
    return divergences


def _compare_summary(problems: list[str], step: str, left: dict, right: dict, dropped: bool) -> None:
    left, right = dict(left), dict(right)
    if dropped:
        for key in ("configs_evaluated", "drifting_configs"):
            left.pop(key, None)
            right.pop(key, None)
    if left != right:
        problems.append(f"{step}: summary differs: {left} against {right}")


def _progress_plans(entries: list[dict]) -> list[int]:
    """The plan each progress line belongs to: every plan opens with its ``running`` line, which the throttle never drops (the
    host's last emitted line is always the previous plan's final line or nothing)."""

    plans, index = [], -1
    for entry in entries:
        index += entry["status"] == "running"
        plans.append(index)
    return plans


def _progress(problems: list[str], name: str, entries: list[dict], quiet: set[int], plan_count: int) -> list[dict]:
    """The progress lines with the final lines of the ``quiet`` plans' status and message blanked."""

    plans = _progress_plans(entries)
    if (plans[-1] + 1 if plans else 0) != plan_count:
        problems.append(f"{name}: {plans[-1] + 1 if plans else 0} running progress line(s) for {plan_count} plan(s)")
    return [
        {**entry, "status": None, "message": None} if plan in quiet and entry["status"] != "running" else entry
        for entry, plan in zip(entries, plans, strict=True)
    ]


def run_checks(case: dict, stock: dict | None, stock_records: dict, bonly: dict, bonly_records: dict, fixed: dict,
               fixed_records: dict) -> tuple[list[tuple[str, str]], list[str]]:
    declared = set(case.get("fixes") or [])
    unknown = declared - set(FIXES)
    problems: list[str] = [f"the case declares unknown fixes {sorted(unknown)}"] if unknown else []
    divergences: list[tuple[str, str]] = []
    if case.get("stock_aborts"):
        divergences += check_abort(case, stock_records, bonly, bonly_records, problems)
    else:
        assert stock is not None
        divergences += check_b(stock, stock_records, bonly, bonly_records, problems)
    divergences += check_a(bonly, bonly_records, fixed, fixed_records, problems, _runs(case))

    seen = {fix for fix, _ in divergences}
    for fix in FIXES:
        if fix in declared and fix not in seen:
            problems.append(f"the case declares {fix} but the runs do not differ by it")
        if fix in seen and fix not in declared:
            problems.append(f"the runs differ by {fix}, which the case does not declare")
    return divergences, problems


def self_test_wrong_drift_count(case: dict, documents: list[dict | None]) -> list[str]:
    """``"self_test": "wrong-drift-count"``: the untampered runs pass, and on the first entry where plans sharing a host id make
    the per-plan count of drifting rows differ, three separately tampered stock dumps are each rejected by the aggregate check:
    drift_count (catalog and drilldown alike) one below the right count, drift_count the per-plan count, and a wrong catalog
    severity. Returns the self-test's failures."""

    failures: list[str] = []
    _, problems = run_checks(case, *documents)
    if problems:
        return [f"self-test wrong-drift-count: the untampered runs fail: {problems[:3]}"]
    stock = documents[0]
    assert stock is not None
    response = stock["response"]
    per_plan = next(
        (
            (index, count) for index, (entry, drill) in enumerate(zip(response["catalog"], response["drilldown"], strict=True))
            if (count := sum(1 for row in drill["servers"] if row["present"] and row["drift_lines"] > 0)) != entry["drift_count"]
        ),
        None,
    )
    if per_plan is None:
        failures.append("self-test wrong-drift-count: no entry where plans sharing a host id make the per-plan count differ")
        return failures
    index, per_plan_count = per_plan
    entry = response["catalog"][index]
    count = entry["drift_count"]
    # The per-plan count only ever exceeds the unique-host count, so one below it (the count is at least one where they differ)
    # is a wrong value distinct from the per-plan tamper.
    tampers = [("off by one", "drift_count", count - 1), ("per-plan count", "drift_count", per_plan_count),
               ("wrong severity", "severity", "medium" if entry["severity"] == "high" else "high")]
    for name, key, value in tampers:
        tampered = copy.deepcopy(stock)
        tampered["response"]["catalog"][index][key] = value
        if key == "drift_count":
            tampered["response"]["drilldown"][index][key] = value
        _, problems = run_checks(case, tampered, *documents[1:])
        config_id = entry["config_id"]
        if not any(f"step b stock catalog entry {config_id} {key} is {value!r}," in problem for problem in problems):
            failures.append(f"self-test wrong-drift-count: a {name} {key} ({value!r}) on {config_id} was not rejected: {problems[:3]}")
        else:
            print(f"self-test wrong-drift-count: {name} {key} {value!r} on {config_id} (untampered {entry[key]!r}) rejected")
    return failures


def self_test_wrong_used_cache(case: dict, documents: list[dict | None]) -> list[str]:
    """``"self_test": "wrong-used-cache"``: the untampered runs pass, and a fixed dump whose ``used_cache`` is flipped on one plan
    is rejected by step a unless that flip is the exemption itself (a collided, primed plan whose fix b ``used_cache`` is false
    becoming true). The flips rejected must include a failed plan and a succeeded plan without a collision of one host id planned
    more than once, where an exemption keyed by host id would have accepted them. Returns the self-test's failures."""

    _, problems = run_checks(case, *documents)
    if problems:
        return [f"self-test wrong-used-cache: the untampered runs fail: {problems[:3]}"]
    bonly, bonly_records, fixed = documents[2], documents[3], documents[4]
    assert bonly is not None and bonly_records is not None and fixed is not None
    b_results, f_results = bonly["response"]["results"], fixed["response"]["results"]
    scanned = {index for index, (b, f) in enumerate(zip(b_results, f_results, strict=True)) if b["status"] == f["status"] == "succeeded"}
    b_keys = {
        (record["plan_index"], record["root_index"], record["relative_path"]): record["config_id"]
        for record in bonly_records["records"] if record["plan_index"] in scanned
    }
    collided = _collided_plans(scanned, b_keys)
    hosts = [result["host_id"] for result in f_results]
    repeated = {host for host in hosts if hosts.count(host) > 1}
    failures: list[str] = []
    rejected_failed = rejected_clean = False
    for index, result in enumerate(f_results):
        flipped = not result["used_cache"]
        primed = _runs(case) > 1 or any(other["host_id"] == result["host_id"] for other in f_results[:index])
        if index in collided and primed and flipped and b_results[index]["used_cache"] is False:
            continue
        tampered = copy.deepcopy(fixed)
        tampered["response"]["results"][index]["used_cache"] = flipped
        _, problems = run_checks(case, *documents[:4], tampered, documents[5])
        if not any(f"step a: host {result['host_id']} (plan {index}) result differs" in problem for problem in problems):
            failures.append(f"self-test wrong-used-cache: used_cache {flipped} on plan {index} ({result['host_id']}) was not rejected")
            continue
        print(f"self-test wrong-used-cache: used_cache {flipped} on plan {index} ({result['host_id']}, {result['status']}) rejected")
        if result["host_id"] in repeated:
            rejected_failed |= result["status"] == "failed"
            rejected_clean |= index in scanned and index not in collided
    if not rejected_failed or not rejected_clean:
        failures.append(
            "self-test wrong-used-cache: no failed plan and no succeeded plan without a collision of a repeated host id was rejected"
        )
    return failures


SELF_TESTS = {"wrong-drift-count": self_test_wrong_drift_count, "wrong-used-cache": self_test_wrong_used_cache}


def main(argv: list[str]) -> int:
    self_test = len(argv) > 1 and argv[1] == "--self-test"
    if self_test:
        argv = argv[:1] + argv[2:]
    case = _load(argv[1])
    stock_path, stock_records_path, bonly, bonly_records, fixed, fixed_records = argv[2:8]
    documents = [
        None if case.get("stock_aborts") else _load(stock_path),
        _load(stock_records_path), _load(bonly), _load(bonly_records), _load(fixed), _load(fixed_records),
    ]
    if self_test:
        name = case.get("self_test")
        failures = SELF_TESTS[name](case, documents) if name in SELF_TESTS else [f"unknown self_test {name!r}"]
        for failure in failures:
            print(failure, file=sys.stderr)
        return 1 if failures else 0
    divergences, problems = run_checks(case, *documents)
    for fix, text in divergences:
        print(f"fix {fix}: {text}")
    for problem in problems:
        print(problem, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
