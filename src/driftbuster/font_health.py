"""Utilities for summarising headless font health telemetry."""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable, List, Optional, Sequence
import json


@dataclass(frozen=True)
class ScenarioHealth:
    """Represents a single scenario entry from the telemetry log."""

    name: str
    total_runs: int
    passes: int
    failures: int
    last_status: str
    last_updated: Optional[datetime]
    last_details: dict

    @property
    def failure_rate(self) -> float:
        total = self.total_runs
        if total <= 0:
            return 0.0
        return self.failures / total

    def summary(self) -> str:
        return (
            f"runs={self.total_runs} passes={self.passes} "
            f"failures={self.failures} last={self.last_status}"
        )


@dataclass(frozen=True)
class FontHealthReport:
    """Top-level telemetry snapshot."""

    generated_at: Optional[datetime]
    scenarios: Sequence[ScenarioHealth]


@dataclass(frozen=True)
class ScenarioEvaluation:
    """Computed drift information for a scenario."""

    scenario: ScenarioHealth
    failure_rate: float
    issues: Sequence[str]

    @property
    def status(self) -> str:
        return "ok" if not self.issues else "drift"


@dataclass(frozen=True)
class ReportEvaluation:
    report: FontHealthReport
    scenarios: Sequence[ScenarioEvaluation]

    @property
    def has_issues(self) -> bool:
        return any(e.issues for e in self.scenarios)


class FontHealthError(RuntimeError):
    """Raised when the telemetry file cannot be parsed."""


def _parse_datetime(value: Optional[str]) -> Optional[datetime]:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value)
    except ValueError as exc:  # pragma: no cover - defensive guard
        raise FontHealthError(f"Invalid ISO timestamp: {value}") from exc


def load_font_health_report(path: Path | str) -> FontHealthReport:
    """Load a :class:`FontHealthReport` from *path*."""

    candidate = Path(path)
    if not candidate.is_file():
        raise FontHealthError(f"Telemetry file not found: {candidate}")

    try:
        payload = json.loads(candidate.read_text())
    except json.JSONDecodeError as exc:  # pragma: no cover - defensive guard
        raise FontHealthError(f"Invalid JSON payload in {candidate}") from exc

    scenarios_payload = payload.get("scenarios") or []
    scenarios: List[ScenarioHealth] = []
    for raw in scenarios_payload:
        scenarios.append(
            ScenarioHealth(
                name=str(raw.get("name", "")),
                total_runs=int(raw.get("totalRuns", 0)),
                passes=int(raw.get("passes", 0)),
                failures=int(raw.get("failures", 0)),
                last_status=str(raw.get("lastStatus", "unknown")),
                last_updated=_parse_datetime(raw.get("lastUpdated")),
                last_details=dict(raw.get("lastDetails") or {}),
            )
        )

    generated_at = _parse_datetime(payload.get("generatedAt")) if payload else None
    return FontHealthReport(generated_at=generated_at, scenarios=scenarios)


def evaluate_scenarios(
    scenarios: Iterable[ScenarioHealth],
    *,
    max_failure_rate: float = 0.0,
    require_last_pass: bool = True,
    min_total_runs: int = 1,
) -> List[ScenarioEvaluation]:
    """Evaluate *scenarios* and flag drift signals.

    Args:
        max_failure_rate: Maximum acceptable failure rate (0.0 - 1.0).
        require_last_pass: Require the latest status to be a pass.
        min_total_runs: Minimum number of total runs expected per scenario.
    """

    if max_failure_rate < 0 or max_failure_rate > 1:
        raise ValueError("max_failure_rate must be between 0 and 1")
    if min_total_runs < 0:
        raise ValueError("min_total_runs must be non-negative")

    evaluations: List[ScenarioEvaluation] = []
    for scenario in scenarios:
        issues: List[str] = []
        failure_rate = scenario.failure_rate
        if scenario.total_runs < min_total_runs:
            issues.append(
                f"expected at least {min_total_runs} total runs; found {scenario.total_runs}"
            )
        if failure_rate > max_failure_rate:
            percent = f"{failure_rate * 100:.1f}%"
            issues.append(
                f"failure rate {percent} exceeds allowed {max_failure_rate * 100:.1f}%"
            )
        if require_last_pass and scenario.last_status.lower() != "pass":
            issues.append(f"latest status is '{scenario.last_status}'")

        evaluations.append(
            ScenarioEvaluation(
                scenario=scenario, failure_rate=failure_rate, issues=tuple(issues)
            )
        )
    return evaluations


def evaluate_report(
    report: FontHealthReport,
    *,
    max_failure_rate: float = 0.0,
    require_last_pass: bool = True,
    min_total_runs: int = 1,
) -> ReportEvaluation:
    """Evaluate a :class:`FontHealthReport`."""

    scenarios = evaluate_scenarios(
        report.scenarios,
        max_failure_rate=max_failure_rate,
        require_last_pass=require_last_pass,
        min_total_runs=min_total_runs,
    )
    return ReportEvaluation(report=report, scenarios=tuple(scenarios))


def format_report(evaluation: ReportEvaluation) -> List[str]:
    """Render the evaluation as a list of printable lines."""

    lines: List[str] = []
    header = "Scenario".ljust(65)
    header += "Status".ljust(8)
    header += "Failure%".rjust(11)
    lines.append(header)
    lines.append("-" * len(header))

    for item in evaluation.scenarios:
        name = item.scenario.name[:62] + ("..." if len(item.scenario.name) > 62 else "")
        name = name.ljust(65)
        status = item.status.upper().ljust(8)
        failure_percent = f"{item.failure_rate * 100:.1f}".rjust(11)
        lines.append(f"{name}{status}{failure_percent}")
        for issue in item.issues:
            lines.append(f"    ↳ {issue}")

    if evaluation.report.generated_at:
        lines.append("")
        lines.append(
            f"Generated at: {evaluation.report.generated_at.isoformat(timespec='seconds')}"
        )
    return lines


__all__ = [
    "FontHealthError",
    "FontHealthReport",
    "ScenarioHealth",
    "ScenarioEvaluation",
    "ReportEvaluation",
    "evaluate_report",
    "evaluate_scenarios",
    "format_report",
    "load_font_health_report",
]
