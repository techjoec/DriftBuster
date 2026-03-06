from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PORTABLE_ROOT = Path("/lap_temp/DriftBuster-Portabletest")


@dataclass
class ScenarioResult:
    name: str
    passed: bool
    details: dict[str, Any]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run multi-server end-to-end self-checks against sample configs."
    )
    parser.add_argument(
        "--portable-root",
        type=Path,
        default=DEFAULT_PORTABLE_ROOT,
        help=f"Portable root used for PYTHONPATH and samples (default: {DEFAULT_PORTABLE_ROOT}).",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=REPO_ROOT / "artifacts" / "selfcheck" / "multi_server_paths_report.json",
        help="Path to write JSON report.",
    )
    return parser.parse_args()


def resolve_pythonpath(portable_root: Path) -> Path:
    portable_src = portable_root / "src"
    if (portable_src / "driftbuster").exists():
        return portable_src

    repo_src = REPO_ROOT / "src"
    if (repo_src / "driftbuster").exists():
        return repo_src

    raise SystemExit("Could not resolve driftbuster source path for self-check.")


def resolve_samples(portable_root: Path) -> Path:
    portable_samples = portable_root / "Samples" / "MultiServer"
    if portable_samples.exists():
        return portable_samples

    fixture_samples = REPO_ROOT / "fixtures" / "multi-server"
    if fixture_samples.exists():
        return fixture_samples

    raise SystemExit("Could not locate multi-server sample directories.")


def run_multi_server(
    *,
    request: dict[str, Any],
    pythonpath: Path,
    cwd: Path,
) -> tuple[int, list[dict[str, Any]], str]:
    env = os.environ.copy()
    existing = env.get("PYTHONPATH")
    env["PYTHONPATH"] = (
        str(pythonpath)
        if not existing
        else os.pathsep.join((str(pythonpath), existing))
    )

    completed = subprocess.run(
        [sys.executable, "-m", "driftbuster.multi_server"],
        input=json.dumps(request),
        capture_output=True,
        text=True,
        env=env,
        cwd=cwd,
        check=False,
    )

    events: list[dict[str, Any]] = []
    for raw in completed.stdout.splitlines():
        line = raw.strip()
        if not line:
            continue
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError:
            events.append({"type": "decode-error", "line": line})

    return completed.returncode, events, completed.stderr.strip()


def last_event(events: list[dict[str, Any]], event_type: str) -> dict[str, Any] | None:
    for event in reversed(events):
        if event.get("type") == event_type:
            return event
    return None


def evaluate_response(name: str, returncode: int, events: list[dict[str, Any]], stderr: str) -> dict[str, Any]:
    result_event = last_event(events, "result")
    error_event = last_event(events, "error")
    payload = result_event.get("payload", {}) if result_event else {}
    results = payload.get("results", []) if isinstance(payload, dict) else []
    catalog = payload.get("catalog", []) if isinstance(payload, dict) else []
    drilldown = payload.get("drilldown", []) if isinstance(payload, dict) else []

    return {
        "name": name,
        "returncode": returncode,
        "event_count": len(events),
        "has_result": result_event is not None,
        "error_message": error_event.get("message") if isinstance(error_event, dict) else None,
        "stderr": stderr,
        "hosts": len(results) if isinstance(results, list) else 0,
        "catalog": len(catalog) if isinstance(catalog, list) else 0,
        "drilldown": len(drilldown) if isinstance(drilldown, list) else 0,
        "failed_hosts": sum(1 for entry in results if isinstance(entry, dict) and entry.get("status") == "failed") if isinstance(results, list) else 0,
        "drift_entries": sum(1 for entry in catalog if isinstance(entry, dict) and int(entry.get("drift_count", 0)) > 0) if isinstance(catalog, list) else 0,
        "payload": payload,
    }


def make_plan(host_id: str, label: str, root: Path, *, preferred: bool, priority: int, scope: str = "custom_roots") -> dict[str, Any]:
    return {
        "host_id": host_id,
        "label": label,
        "scope": scope,
        "roots": [str(root)],
        "baseline": {
            "is_preferred": preferred,
            "priority": priority,
        },
    }


def run_scenarios(samples_root: Path, pythonpath: Path, report_dir: Path) -> list[ScenarioResult]:
    scenarios: list[ScenarioResult] = []

    cache_dir = report_dir / "cache"
    cache_dir.mkdir(parents=True, exist_ok=True)

    def execute(name: str, request: dict[str, Any], judge) -> None:
        rc, events, stderr = run_multi_server(request=request, pythonpath=pythonpath, cwd=REPO_ROOT)
        details = evaluate_response(name, rc, events, stderr)
        passed = judge(details)
        scenarios.append(ScenarioResult(name=name, passed=passed, details=details))

    execute(
        "single_host",
        {
            "schema_version": "multi-server.v1",
            "cache_dir": str(cache_dir / "single"),
            "plans": [
                make_plan("host-01", "server01", samples_root / "server01", preferred=True, priority=10),
            ],
        },
        lambda d: d["returncode"] == 0 and d["has_result"] and d["hosts"] == 1 and d["catalog"] > 0 and d["drilldown"] > 0 and d["failed_hosts"] == 0,
    )

    execute(
        "two_host_drift",
        {
            "schema_version": "multi-server.v1",
            "cache_dir": str(cache_dir / "drift"),
            "plans": [
                make_plan("host-01", "server01", samples_root / "server01", preferred=True, priority=10),
                make_plan("host-02", "server02", samples_root / "server02", preferred=False, priority=5),
            ],
        },
        lambda d: d["returncode"] == 0 and d["has_result"] and d["hosts"] == 2 and d["drift_entries"] > 0,
    )

    execute(
        "missing_root_failure",
        {
            "schema_version": "multi-server.v1",
            "cache_dir": str(cache_dir / "missing"),
            "plans": [
                make_plan("host-01", "server01", samples_root / "server01", preferred=True, priority=10),
                make_plan("host-x", "missing", samples_root / "does-not-exist", preferred=False, priority=1),
            ],
        },
        lambda d: d["returncode"] == 0 and d["has_result"] and d["failed_hosts"] >= 1,
    )

    execute(
        "all_drives_scope",
        {
            "schema_version": "multi-server.v1",
            "cache_dir": str(cache_dir / "all-drives"),
            "plans": [
                make_plan("host-01", "server01", samples_root / "server01", preferred=True, priority=10, scope="all_drives"),
            ],
        },
        lambda d: d["returncode"] == 0 and d["has_result"] and d["hosts"] == 1 and d["catalog"] > 0,
    )

    execute(
        "cache_reuse_hot_run",
        {
            "schema_version": "multi-server.v1",
            "cache_dir": str(cache_dir / "hot-run"),
            "plans": [
                make_plan("host-01", "server01", samples_root / "server01", preferred=True, priority=10),
                make_plan("host-02", "server02", samples_root / "server02", preferred=False, priority=5),
            ],
        },
        lambda d: d["returncode"] == 0,
    )

    # Run same request again to verify cache reuse flags.
    execute(
        "cache_reuse_hot_run_repeat",
        {
            "schema_version": "multi-server.v1",
            "cache_dir": str(cache_dir / "hot-run"),
            "plans": [
                make_plan("host-01", "server01", samples_root / "server01", preferred=True, priority=10),
                make_plan("host-02", "server02", samples_root / "server02", preferred=False, priority=5),
            ],
        },
        lambda d: d["returncode"] == 0 and d["has_result"] and all(
            entry.get("used_cache") for entry in d["payload"].get("results", []) if entry.get("availability") == "found"
        ),
    )

    with TemporaryDirectory(prefix="driftbuster-selfcheck-") as tmp:
        temp_root = Path(tmp)
        log4net_file = temp_root / "log4net.config"
        log4net_file.write_text("\ufeff<log4net><appender name='A'>\u2603</appender></log4net>", encoding="utf-8")

        execute(
            "vendor_variant_unicode_payload",
            {
                "schema_version": "multi-server.v1",
                "cache_dir": str(cache_dir / "vendor-unicode"),
                "plans": [
                    make_plan("host-u", "unicode", temp_root, preferred=True, priority=1),
                ],
            },
            lambda d: d["returncode"] == 0 and d["has_result"] and d["failed_hosts"] == 0 and d["catalog"] > 0,
        )

    execute(
        "invalid_schema_rejected",
        {
            "schema_version": "multi-server.v0",
            "cache_dir": str(cache_dir / "schema"),
            "plans": [],
        },
        lambda d: d["returncode"] == 1 and d["error_message"] and "Unsupported schema version" in d["error_message"],
    )

    return scenarios


def main() -> int:
    args = parse_args()
    pythonpath = resolve_pythonpath(args.portable_root)
    samples_root = resolve_samples(args.portable_root)

    report_path = args.output
    report_path.parent.mkdir(parents=True, exist_ok=True)

    scenarios = run_scenarios(samples_root, pythonpath, report_path.parent)
    passed = sum(1 for scenario in scenarios if scenario.passed)
    total = len(scenarios)

    report = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "pythonpath": str(pythonpath),
        "samples_root": str(samples_root),
        "passed": passed,
        "total": total,
        "success": passed == total,
        "scenarios": [
            {
                "name": scenario.name,
                "passed": scenario.passed,
                "details": scenario.details,
            }
            for scenario in scenarios
        ],
    }

    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    for scenario in scenarios:
        status = "PASS" if scenario.passed else "FAIL"
        print(f"[{status}] {scenario.name}")

    print(f"\nSelf-check summary: {passed}/{total} passed")
    print(f"Report: {report_path}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    raise SystemExit(main())
