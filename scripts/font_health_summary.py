"""Summarise headless font telemetry and flag drift."""
from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

from driftbuster.font_health import (
    FontHealthError,
    ReportEvaluation,
    ScenarioEvaluation,
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
    parser.add_argument(
        "--summary-path",
        metavar="PATH",
        help=(
            "Write aggregated staleness summary JSON to PATH; defaults to "
            "<log dir>/font-staleness-summary.json. Pass '-' to disable."
        ),
    )
    return parser


def _resolve_log_dir() -> Path:
    """Return the destination directory for structured staleness logs."""

    candidate = os.environ.get("FONT_STALENESS_LOG_DIR")
    if candidate:
        return Path(candidate)
    return Path("artifacts/logs/font-staleness")


def _coerce_utc(moment: datetime) -> datetime:
    if moment.tzinfo is None:
        return moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(timezone.utc)


def _scenario_payload(
    evaluation: ScenarioEvaluation,
    *,
    evaluation_time: datetime,
) -> dict:
    scenario = evaluation.scenario
    payload: dict[str, object] = {
        "name": scenario.name,
        "status": evaluation.status,
        "issues": list(evaluation.issues),
        "failureRate": evaluation.failure_rate,
        "totalRuns": scenario.total_runs,
        "passes": scenario.passes,
        "failures": scenario.failures,
        "lastStatus": scenario.last_status,
    }

    if scenario.last_updated is not None:
        last_updated = _coerce_utc(scenario.last_updated)
        payload["lastUpdated"] = last_updated.isoformat()
        payload["lastUpdatedAgeSeconds"] = (
            _coerce_utc(evaluation_time) - last_updated
        ).total_seconds()
    else:
        payload["lastUpdated"] = None
        payload["lastUpdatedAgeSeconds"] = None

    if scenario.last_details:
        payload["lastDetails"] = scenario.last_details

    return payload


def _build_summary_payload(
    evaluation: ReportEvaluation,
    *,
    evaluation_time: datetime,
    source_path: Path,
    max_last_updated_age: timedelta | None,
    max_failure_rate: float,
    min_total_runs: int,
    require_last_pass: bool,
) -> dict:
    scenario_counts: dict[str, int] = {}
    stale_names: set[str] = set()
    missing_last_updated_names: set[str] = set()
    latest_status_issue_names: set[str] = set()
    low_run_names: set[str] = set()

    for item in evaluation.scenarios:
        scenario_counts[item.status] = scenario_counts.get(item.status, 0) + 1
        scenario_name = item.scenario.name
        for issue in item.issues:
            if "lastUpdated" in issue:
                stale_names.add(scenario_name)
            if "missing lastUpdated" in issue:
                missing_last_updated_names.add(scenario_name)
            if "latest status" in issue:
                latest_status_issue_names.add(scenario_name)
            if "expected at least" in issue:
                low_run_names.add(scenario_name)

    report_generated_at = evaluation.report.generated_at
    if report_generated_at is not None:
        report_generated_at = _coerce_utc(report_generated_at)
        report_age_seconds = (
            _coerce_utc(evaluation_time) - report_generated_at
        ).total_seconds()
    else:
        report_age_seconds = None

    return {
        "generatedAt": _coerce_utc(evaluation_time).isoformat(),
        "source": str(source_path),
        "hasIssues": evaluation.has_issues,
        "issueCount": (
            len(evaluation.missing_scenarios)
            + sum(len(item.issues) for item in evaluation.scenarios)
        ),
        "missingScenarios": list(evaluation.missing_scenarios),
        "scenarioCounts": dict(sorted(scenario_counts.items())),
        "staleScenarioNames": sorted(stale_names),
        "missingLastUpdatedScenarioNames": sorted(missing_last_updated_names),
        "latestStatusIssueScenarioNames": sorted(latest_status_issue_names),
        "lowRunScenarioNames": sorted(low_run_names),
        "maxLastUpdatedAgeSeconds": (
            max_last_updated_age.total_seconds()
            if max_last_updated_age is not None
            else None
        ),
        "maxFailureRate": max_failure_rate,
        "minTotalRuns": min_total_runs,
        "requireLastPass": require_last_pass,
        "reportGeneratedAt": (
            report_generated_at.isoformat() if report_generated_at is not None else None
        ),
        "reportAgeSeconds": report_age_seconds,
    }


def _write_staleness_event(
    evaluation: ReportEvaluation,
    *,
    evaluation_time: datetime,
    source_path: Path,
    max_last_updated_age: timedelta | None,
    max_failure_rate: float,
    min_total_runs: int,
    require_last_pass: bool,
    log_dir: Path,
    summary_path: Path | None,
) -> None:
    log_dir.mkdir(parents=True, exist_ok=True)

    payload = {
        "generatedAt": _coerce_utc(evaluation_time).isoformat(),
        "source": str(source_path),
        "hasIssues": evaluation.has_issues,
        "missingScenarios": list(evaluation.missing_scenarios),
        "maxLastUpdatedAgeSeconds": (
            max_last_updated_age.total_seconds()
            if max_last_updated_age is not None
            else None
        ),
        "maxFailureRate": max_failure_rate,
        "minTotalRuns": min_total_runs,
        "requireLastPass": require_last_pass,
        "scenarios": [
            _scenario_payload(item, evaluation_time=evaluation_time)
            for item in evaluation.scenarios
        ],
    }

    timestamp = _coerce_utc(evaluation_time).strftime("%Y%m%dT%H%M%SZ")
    destination = log_dir / f"font-staleness-{timestamp}.json"
    destination.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n")

    if summary_path is not None:
        summary_payload = _build_summary_payload(
            evaluation,
            evaluation_time=evaluation_time,
            source_path=source_path,
            max_last_updated_age=max_last_updated_age,
            max_failure_rate=max_failure_rate,
            min_total_runs=min_total_runs,
            require_last_pass=require_last_pass,
        )
        summary_path.parent.mkdir(parents=True, exist_ok=True)
        summary_path.write_text(
            json.dumps(summary_payload, indent=2, sort_keys=True) + "\n"
        )


def _emit_staleness_event(
    evaluation: ReportEvaluation,
    *,
    evaluation_time: datetime,
    args: argparse.Namespace,
    source_path: Path,
) -> None:
    try:
        log_dir = _resolve_log_dir()
        if args.summary_path is None:
            summary_path = log_dir / "font-staleness-summary.json"
        elif args.summary_path.strip() in {"", "-"}:
            summary_path = None
        else:
            summary_path = Path(args.summary_path)
        _write_staleness_event(
            evaluation,
            evaluation_time=evaluation_time,
            source_path=source_path,
            max_last_updated_age=(
                timedelta(hours=args.max_stale_hours)
                if args.max_stale_hours is not None
                else None
            ),
            max_failure_rate=args.max_failure_rate,
            min_total_runs=args.min_total_runs,
            require_last_pass=not args.allow_last_failure,
            log_dir=log_dir,
            summary_path=summary_path,
        )
    except Exception as exc:  # pragma: no cover - defensive guard
        print(f"warning: failed to write staleness log: {exc}", file=sys.stderr)


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

    evaluation_time = datetime.now(timezone.utc)
    evaluation = evaluate_report(
        report,
        max_failure_rate=args.max_failure_rate,
        require_last_pass=not args.allow_last_failure,
        min_total_runs=args.min_total_runs,
        required_scenarios=args.required_scenarios,
        max_last_updated_age=max_age,
        now=evaluation_time,
    )

    for line in format_report(evaluation):
        print(line)

    _emit_staleness_event(
        evaluation,
        evaluation_time=evaluation_time,
        args=args,
        source_path=Path(args.path),
    )

    return 1 if evaluation.has_issues else 0


if __name__ == "__main__":
    sys.exit(main())
