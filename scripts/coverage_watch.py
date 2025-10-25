from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Mapping

WATCH_TARGETS: Mapping[Path, float] = {
    Path("src/driftbuster/offline_compliance.py"): 90.0,
    Path("src/driftbuster/offline_runner.py"): 90.0,
    Path("src/driftbuster/reporting/_metadata.py"): 90.0,
    Path("src/driftbuster/reporting/diff.py"): 90.0,
    Path("src/driftbuster/reporting/html.py"): 90.0,
    Path("src/driftbuster/reporting/json.py"): 90.0,
    Path("src/driftbuster/reporting/json_lines.py"): 90.0,
    Path("src/driftbuster/reporting/redaction.py"): 90.0,
    Path("src/driftbuster/reporting/snapshot.py"): 90.0,
    Path("src/driftbuster/reporting/summary.py"): 90.0,
    Path("src/driftbuster/token_approvals.py"): 90.0,
}


def load_python_file_percentages(coverage_json: Path) -> Mapping[Path, float]:
    with coverage_json.open("r", encoding="utf-8") as fh:
        payload = json.load(fh)

    files: dict[Path, float] = {}
    for file_path, info in payload.get("files", {}).items():
        summary = info.get("summary") or {}
        percent = float(summary.get("percent_covered", 0.0))
        files[Path(file_path)] = percent
    return files


def evaluate_watch_targets(
    file_percentages: Mapping[Path, float],
    *,
    targets: Mapping[Path, float] = WATCH_TARGETS,
) -> tuple[list[str], float]:
    missing: list[str] = []
    lowest = 100.0
    for rel_path, threshold in targets.items():
        percent = file_percentages.get(rel_path)
        if percent is None:
            missing.append(f"{rel_path}: coverage data missing")
            continue
        lowest = min(lowest, percent)
        if percent < threshold:
            missing.append(
                f"{rel_path}: {percent:.2f}% (expected ≥ {threshold:.2f}%)"
            )
    return missing, lowest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Verify coverage thresholds for compliance-critical Python modules.",
    )
    parser.add_argument(
        "--python-json",
        type=Path,
        default=Path("coverage.json"),
        help="Path to coverage JSON generated via `coverage json`.",
    )
    args = parser.parse_args(argv)

    if not args.python_json.is_file():
        parser.error(f"coverage JSON not found: {args.python_json}")

    file_percentages = load_python_file_percentages(args.python_json)
    failures, lowest = evaluate_watch_targets(file_percentages)
    if failures:
        for failure in failures:
            print(f"[coverage-watch] {failure}")
        return 1

    print(
        "[coverage-watch] All compliance modules meet coverage target; "
        f"lowest = {lowest:.2f}%"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
