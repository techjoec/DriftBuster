#!/usr/bin/env python3
"""Collect GUI performance diagnostics and export a baseline snapshot.

The snapshot records perf smoke test outcomes, virtualization heuristics, and
fixture statistics so operators can compare future runs against a known-good
reference.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import pathlib
import shlex
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from typing import Any, Dict, List, Optional

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]
ARTIFACTS_DIR = REPO_ROOT / "artifacts" / "perf"
DEFAULT_OUTPUT = ARTIFACTS_DIR / "baseline.json"
PERF_PROJECT = REPO_ROOT / "gui" / "DriftBuster.Gui.Tests" / "DriftBuster.Gui.Tests.csproj"
SAMPLES_ROOT = REPO_ROOT / "samples" / "multi-server"
VIRTUALIZATION_THRESHOLD_DEFAULT = 400


def _parse_bool(value: Optional[str]) -> Optional[bool]:
    if value is None:
        return None

    candidate = value.strip().lower()
    truthy = {"1", "true", "yes", "on"}
    falsy = {"0", "false", "no", "off"}
    if candidate in truthy:
        return True
    if candidate in falsy:
        return False
    return None


def _parse_threshold(value: Optional[str]) -> int:
    if value is None:
        return VIRTUALIZATION_THRESHOLD_DEFAULT

    try:
        parsed = int(value)
    except ValueError:
        return VIRTUALIZATION_THRESHOLD_DEFAULT

    if parsed <= 0:
        return VIRTUALIZATION_THRESHOLD_DEFAULT
    return parsed


def _parse_duration_seconds(duration: Optional[str]) -> Optional[float]:
    if not duration:
        return None

    # TRX duration is formatted as HH:MM:SS.mmmmmmm
    try:
        hours_str, minutes_str, seconds_str = duration.split(":")
        hours = int(hours_str)
        minutes = int(minutes_str)
        seconds = float(seconds_str)
    except ValueError:
        return None

    return hours * 3600 + minutes * 60 + seconds


def _run_perf_smoke(results_dir: pathlib.Path) -> Dict[str, Any]:
    results_dir.mkdir(parents=True, exist_ok=True)
    trx_path = results_dir / "perf-smoke.trx"
    command = [
        "dotnet",
        "test",
        str(PERF_PROJECT),
        "--filter",
        "Category=PerfSmoke",
        "--logger",
        "trx;LogFileName=perf-smoke.trx",
        "--results-directory",
        str(results_dir),
    ]

    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    output = (completed.stdout or "") + (completed.stderr or "")

    if not trx_path.exists():
        raise FileNotFoundError(f"Perf smoke TRX output not found: {trx_path}")

    tree = ET.parse(trx_path)
    root = tree.getroot()
    namespace = ""
    if root.tag.startswith("{"):
        namespace = root.tag.split("}")[0].strip("{")
    ns = {"ns": namespace} if namespace else {}

    results: List[Dict[str, Any]] = []
    for unit_result in root.findall(".//ns:UnitTestResult" if namespace else ".//UnitTestResult", ns):
        duration_seconds = _parse_duration_seconds(unit_result.get("duration"))
        results.append(
            {
                "name": unit_result.get("testName"),
                "outcome": unit_result.get("outcome"),
                "duration_seconds": duration_seconds,
            }
        )

    total_duration = sum((entry["duration_seconds"] or 0.0) for entry in results)

    return {
        "command": " ".join(shlex.quote(part) for part in command),
        "exit_code": completed.returncode,
        "results": results,
        "total_duration_seconds": total_duration,
        "log_excerpt": [line for line in output.strip().splitlines()[-25:] if line],
    }


def _collect_fixture_stats() -> Dict[str, Any]:
    if not SAMPLES_ROOT.exists():
        return {"multi_server": {"available": False}}

    host_counts: List[int] = []
    hosts: List[str] = []
    for entry in sorted(SAMPLES_ROOT.iterdir()):
        if not entry.is_dir():
            continue
        file_count = 0
        for _dirpath, _dirnames, filenames in os.walk(entry):
            file_count += len(filenames)
        hosts.append(entry.name)
        host_counts.append(file_count)

    total_files = sum(host_counts)
    max_files = max(host_counts) if host_counts else 0
    min_files = min(host_counts) if host_counts else 0

    return {
        "multi_server": {
            "available": True,
            "host_count": len(hosts),
            "total_files": total_files,
            "max_files_per_host": max_files,
            "min_files_per_host": min_files,
            "hosts": [
                {"name": name, "file_count": count}
                for name, count in zip(hosts, host_counts)
            ],
        }
    }


def _build_virtualization_projection(
    threshold: int, force_override: Optional[bool], fixture_stats: Dict[str, Any]
) -> Dict[str, Any]:
    scenario_counts = {50, 200, 400, 512, 750, 1024, 1600}
    multi = fixture_stats.get("multi_server", {})
    for host in multi.get("hosts", []):
        scenario_counts.add(host.get("file_count", 0))

    decisions: List[Dict[str, Any]] = []
    for count in sorted(scenario_counts):
        decision = count >= threshold if force_override is None else force_override
        decisions.append(
            {
                "items": count,
                "virtualized": decision,
                "applied_threshold": threshold,
                "force_override": force_override,
            }
        )

    projections = {
        "default_threshold": threshold,
        "force_override": force_override,
        "scenarios": decisions,
    }

    if force_override is None:
        projections["with_force_override"] = {
            "force_true": [
                {"items": count, "virtualized": True}
                for count in sorted(scenario_counts)
            ],
            "force_false": [
                {"items": count, "virtualized": False}
                for count in sorted(scenario_counts)
            ],
        }

    return projections


def _git_head() -> Optional[str]:
    try:
        completed = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            capture_output=True,
            text=True,
            check=True,
            cwd=REPO_ROOT,
        )
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None
    return completed.stdout.strip()


def collect_baseline(output_path: pathlib.Path) -> Dict[str, Any]:
    with tempfile.TemporaryDirectory() as tmp:
        tmp_dir = pathlib.Path(tmp)
        perf_smoke = _run_perf_smoke(tmp_dir)

    fixture_stats = _collect_fixture_stats()
    threshold = _parse_threshold(os.getenv("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD"))
    force_override = _parse_bool(os.getenv("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION"))
    virtualization_projection = _build_virtualization_projection(
        threshold, force_override, fixture_stats
    )

    timestamp = _dt.datetime.now(_dt.UTC).replace(microsecond=0)
    baseline = {
        "captured_at_utc": timestamp.isoformat().replace("+00:00", "Z"),
        "git_head": _git_head(),
        "perf_smoke": perf_smoke,
        "virtualization": virtualization_projection,
        "fixtures": fixture_stats,
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as handle:
        json.dump(baseline, handle, indent=2, sort_keys=True)
        handle.write("\n")

    return baseline


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Collect GUI performance diagnostics and export a baseline snapshot.",
    )
    parser.add_argument(
        "--output",
        type=pathlib.Path,
        default=DEFAULT_OUTPUT,
        help=f"Destination JSON file (default: {DEFAULT_OUTPUT})",
    )
    return parser


def main(argv: List[str]) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)

    try:
        baseline = collect_baseline(args.output)
    except Exception as exc:  # noqa: BLE001 - explicit failure reporting
        print(f"error: {exc}", file=sys.stderr)
        return 1

    print(json.dumps({"output": str(args.output), "captured_at_utc": baseline["captured_at_utc"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
