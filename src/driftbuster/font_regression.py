from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import re
from typing import Iterable, Sequence

__all__ = [
    "FontRegressionError",
    "ExceptionRecord",
    "RegressionEvidence",
    "format_evidence",
    "load_regression_log",
    "regression_evidence_to_dict",
]


class FontRegressionError(RuntimeError):
    """Raised when the regression log cannot be parsed."""


@dataclass(frozen=True)
class ExceptionRecord:
    """Represents a captured exception in the regression evidence."""

    type_name: str
    message: str
    stack: Sequence[str]


@dataclass(frozen=True)
class RegressionEvidence:
    """Structured representation of the regression log."""

    captured_at: datetime
    header: str
    exceptions: Sequence[ExceptionRecord]


_TIMESTAMP_PATTERN = re.compile(r"^\[(?P<timestamp>[^\]]+)\]\s*(?P<header>.+)$")
_EXCEPTION_PATTERN = re.compile(r"^(?P<type>[\w\.`]+):\s*(?P<message>.+)$")


def _parse_timestamp(value: str) -> datetime:
    candidate = value.strip()
    if candidate.endswith("Z"):
        candidate = candidate[:-1] + "+00:00"

    try:
        parsed = datetime.fromisoformat(candidate)
    except ValueError as exc:  # pragma: no cover - defensive guard
        raise FontRegressionError(f"Invalid timestamp '{value}'.") from exc

    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)

    return parsed.astimezone(timezone.utc)


def load_regression_log(path: Path | str) -> RegressionEvidence:
    """Load regression evidence from the given log file."""

    candidate = Path(path)
    if not candidate.is_file():
        raise FontRegressionError(f"Regression log not found: {candidate}")

    lines = candidate.read_text().splitlines()
    if not lines:
        raise FontRegressionError(f"Regression log is empty: {candidate}")

    timestamp_match = _TIMESTAMP_PATTERN.match(lines[0])
    if not timestamp_match:
        raise FontRegressionError("Regression log missing timestamp header.")

    captured_at = _parse_timestamp(timestamp_match.group("timestamp"))
    header = timestamp_match.group("header").strip()

    exceptions = list(_parse_exception_sections(lines[1:]))
    if not exceptions:
        raise FontRegressionError("Regression log did not contain any exceptions.")

    return RegressionEvidence(
        captured_at=captured_at,
        header=header,
        exceptions=tuple(exceptions),
    )


def _parse_exception_sections(lines: Iterable[str]) -> Iterable[ExceptionRecord]:
    current: ExceptionRecord | None = None
    stack: list[str] = []

    def flush_current() -> ExceptionRecord | None:
        nonlocal current, stack
        if current is None:
            return None

        record = ExceptionRecord(
            type_name=current.type_name,
            message=current.message,
            stack=tuple(stack),
        )
        current = None
        stack = []
        return record

    for line in lines:
        stripped = line.rstrip("\n")
        if not stripped:
            if current is not None:
                stack.append(stripped)
            continue

        if stripped.strip().startswith("--- inner ---"):
            record = flush_current()
            if record is not None:
                yield record
            continue

        if not stripped.startswith("   "):
            match = _EXCEPTION_PATTERN.match(stripped)
            if match:
                record = flush_current()
                if record is not None:
                    yield record
                current = ExceptionRecord(
                    type_name=match.group("type"),
                    message=match.group("message"),
                    stack=(),
                )
                stack = []
                continue

        if current is not None:
            stack.append(stripped)

    record = flush_current()
    if record is not None:
        yield record


def regression_evidence_to_dict(evidence: RegressionEvidence) -> dict[str, object]:
    """Convert :class:`RegressionEvidence` into a JSON-serialisable mapping."""

    return {
        "captured_at": evidence.captured_at.isoformat(),
        "header": evidence.header,
        "exceptions": [
            {
                "type": record.type_name,
                "message": record.message,
                "stack": list(record.stack),
            }
            for record in evidence.exceptions
        ],
    }


def format_evidence(evidence: RegressionEvidence) -> list[str]:
    """Render a printable summary of the regression evidence."""

    lines = [
        f"Captured at: {evidence.captured_at.isoformat()}",
        f"Header: {evidence.header}",
        "",
    ]

    for index, record in enumerate(evidence.exceptions, start=1):
        lines.append(f"Exception #{index}: {record.type_name}")
        lines.append(f"  Message: {record.message}")
        if record.stack:
            lines.append("  Stack:")
            lines.extend(f"    {frame}" for frame in record.stack)
        lines.append("")

    return lines[:-1] if lines and not lines[-1] else lines
