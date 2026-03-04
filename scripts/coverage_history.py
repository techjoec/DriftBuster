from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
from pathlib import Path
from typing import Mapping

from scripts.coverage_report import (
    find_cobertura_xml,
    load_changed_production_lines,
    load_cobertura_summary,
    load_python_coverage,
    summarise_changed_dotnet_lines,
)
from scripts.coverage_watch import (
    evaluate_watch_targets,
    load_python_file_percentages,
)


def compute_python_percent(coverage_path: Path) -> float:
    payload = load_python_coverage(str(coverage_path))
    if not payload:
        raise FileNotFoundError(f"Python coverage JSON not found: {coverage_path}")
    totals = payload.get("totals") or {}
    percent = totals.get("percent_covered")
    if percent is None:
        raise ValueError("coverage JSON missing totals.percent_covered")
    return float(percent)


def compute_dotnet_percent(dotnet_root: Path) -> float | None:
    xml_path = find_cobertura_xml(str(dotnet_root))
    if xml_path is None:
        return None
    line_rate, *_ = load_cobertura_summary(xml_path)
    return line_rate * 100.0


def compute_dotnet_changed_percent(dotnet_root: Path, diff_base: str) -> float | None:
    xml_path = find_cobertura_xml(str(dotnet_root))
    if xml_path is None:
        return None

    _line_rate, _classes, _files, line_hits = load_cobertura_summary(xml_path)
    try:
        changed_lines = load_changed_production_lines(diff_base)
    except RuntimeError:
        return None
    changed_ratio, _details, _skipped = summarise_changed_dotnet_lines(changed_lines, line_hits)
    if changed_ratio is None:
        return None
    return changed_ratio * 100.0


def append_history(
    output_path: Path,
    timestamp: str,
    python_percent: float,
    dotnet_percent: float | None,
    dotnet_changed_percent: float | None,
    watch_lowest: float,
    notes: str | None,
) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    current_header = [
        "timestamp_utc",
        "python_percent",
        "dotnet_percent",
        "dotnet_changed_percent",
        "python_watch_min",
        "notes",
    ]
    legacy_header = [
        "timestamp_utc",
        "python_percent",
        "dotnet_percent",
        "python_watch_min",
        "notes",
    ]
    existing_header: list[str] | None = None
    existing_rows: list[list[str]] = []
    if output_path.exists():
        with output_path.open("r", encoding="utf-8", newline="") as fh:
            reader = csv.reader(fh)
            existing_header = next(reader, None)
            existing_rows = list(reader)

    if existing_header == legacy_header:
        migrated_rows: list[list[str]] = []
        for row in existing_rows:
            padded = row + [""] * max(0, 5 - len(row))
            migrated_rows.append(
                [
                    padded[0],
                    padded[1],
                    padded[2],
                    "",
                    padded[3],
                    padded[4],
                ]
            )
        with output_path.open("w", encoding="utf-8", newline="") as fh:
            writer = csv.writer(fh)
            writer.writerow(current_header)
            writer.writerows(migrated_rows)
        existing_header = current_header

    needs_header = existing_header is None
    with output_path.open("a", encoding="utf-8", newline="") as fh:
        writer = csv.writer(fh)
        if needs_header:
            writer.writerow(current_header)

        writer.writerow(
            [
                timestamp,
                f"{python_percent:.2f}",
                "" if dotnet_percent is None else f"{dotnet_percent:.2f}",
                "" if dotnet_changed_percent is None else f"{dotnet_changed_percent:.2f}",
                f"{watch_lowest:.2f}",
                notes or "",
            ]
        )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Append the latest coverage results to artifacts/coverage/history.csv.",
    )
    parser.add_argument(
        "--python-json",
        type=Path,
        default=Path("coverage.json"),
        help="Path to coverage JSON output from `coverage json`.",
    )
    parser.add_argument(
        "--dotnet-root",
        type=Path,
        default=Path("artifacts/coverage-dotnet"),
        help="Directory containing Cobertura XML output from .NET runs.",
    )
    parser.add_argument(
        "--dotnet-diff-base",
        default="origin/main",
        help="Git base ref used for changed-line .NET coverage snapshots.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("artifacts/coverage/history.csv"),
        help="CSV file to update with coverage history entries.",
    )
    parser.add_argument(
        "--notes",
        help="Optional notes column for the appended row.",
    )
    args = parser.parse_args(argv)

    python_percent = compute_python_percent(args.python_json)
    file_percentages: Mapping[Path, float] = load_python_file_percentages(args.python_json)
    failures, watch_lowest = evaluate_watch_targets(file_percentages)
    if failures:
        raise SystemExit(
            "Python coverage thresholds failed: " + ", ".join(failures)
        )

    dotnet_percent = compute_dotnet_percent(args.dotnet_root)
    dotnet_changed_percent = compute_dotnet_changed_percent(args.dotnet_root, args.dotnet_diff_base)
    timestamp = datetime.now(timezone.utc).replace(microsecond=0).isoformat()

    append_history(
        args.output,
        timestamp,
        python_percent,
        dotnet_percent,
        dotnet_changed_percent,
        watch_lowest,
        args.notes,
    )
    print(
        f"[coverage-history] appended {timestamp} python={python_percent:.2f}% "
        + (
            "dotnet=N/A"
            if dotnet_percent is None
            else f"dotnet={dotnet_percent:.2f}%"
        )
        + " "
        + (
            "dotnet_changed=N/A"
            if dotnet_changed_percent is None
            else f"dotnet_changed={dotnet_changed_percent:.2f}%"
        )
        + f" watch_min={watch_lowest:.2f}%"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
