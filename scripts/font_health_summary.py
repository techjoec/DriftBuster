"""Summarise headless font telemetry and flag drift."""
from __future__ import annotations

import argparse
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

from driftbuster.font_health import (
    FontHealthError,
    evaluate_report,
    format_report,
    load_font_health_report,
)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Summarise headless font telemetry and flag regressions."
    )
    parser.add_argument(
        "path",
        nargs="?",
        default="artifacts/logs/headless-font-health.json",
        help="Telemetry file to inspect (default: artifacts/logs/headless-font-health.json)",
    )
    parser.add_argument(
        "--max-failure-rate",
        type=float,
        default=0.0,
        help="Maximum acceptable failure rate (0.0-1.0).",
    )
    parser.add_argument(
        "--allow-last-failure",
        action="store_true",
        help="Allow the latest run to be non-passing without flagging drift.",
    )
    parser.add_argument(
        "--min-total-runs",
        type=int,
        default=1,
        help="Minimum number of total runs expected per scenario (default: 1).",
    )
    parser.add_argument(
        "--max-stale-hours",
        type=float,
        default=None,
        help=(
            "Maximum allowed age for scenario lastUpdated timestamps in hours; "
            "scenarios older than this threshold are flagged."
        ),
    )
    parser.add_argument(
        "--require-scenario",
        dest="required_scenarios",
        action="append",
        metavar="NAME",
        help="Scenario that must appear in the telemetry (repeat this flag for multiple names).",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        report = load_font_health_report(Path(args.path))
    except FontHealthError as exc:
        parser.error(str(exc))

    if args.max_stale_hours is not None and args.max_stale_hours < 0:
        parser.error("--max-stale-hours must be non-negative")

    max_age = (
        timedelta(hours=args.max_stale_hours)
        if args.max_stale_hours is not None
        else None
    )

    evaluation = evaluate_report(
        report,
        max_failure_rate=args.max_failure_rate,
        require_last_pass=not args.allow_last_failure,
        min_total_runs=args.min_total_runs,
        required_scenarios=args.required_scenarios,
        max_last_updated_age=max_age,
        now=datetime.now(timezone.utc),
    )

    for line in format_report(evaluation):
        print(line)

    return 1 if evaluation.has_issues else 0


if __name__ == "__main__":
    sys.exit(main())
