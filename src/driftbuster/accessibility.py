"""Accessibility evidence validation helpers."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Mapping, Sequence


class AccessibilityTranscriptError(RuntimeError):
    """Raised when an accessibility transcript cannot be processed."""


@dataclass(frozen=True)
class AccessibilityTranscript:
    """Parsed representation of a markdown transcript."""

    path: Path
    title: str
    sections: Mapping[str, str]


@dataclass(frozen=True)
class ScenarioExpectation:
    """Describes the evidence required for a scenario section."""

    title: str
    required_keywords: Sequence[str] = ()


@dataclass(frozen=True)
class ScenarioResult:
    """Evaluation outcome for a single scenario."""

    expectation: ScenarioExpectation
    present: bool
    missing_keywords: Sequence[str]

    @property
    def status(self) -> str:
        if not self.present:
            return "missing"
        if self.missing_keywords:
            return "incomplete"
        return "ok"


@dataclass(frozen=True)
class TranscriptEvaluation:
    """Aggregated evaluation for a transcript."""

    transcript: AccessibilityTranscript
    results: Sequence[ScenarioResult]

    @property
    def has_issues(self) -> bool:
        return any(result.status != "ok" for result in self.results)


DEFAULT_EXPECTATIONS: tuple[ScenarioExpectation, ...] = (
    ScenarioExpectation(
        title="Tool Versions",
        required_keywords=("Windows build", "Narrator", "Inspect"),
    ),
    ScenarioExpectation(
        title="Narrator — Server Selection Sweep",
        required_keywords=("Focus", "Announcements"),
    ),
    ScenarioExpectation(
        title="Narrator — Drilldown Scenario",
        required_keywords=("Drilldown", "Announcements"),
    ),
    ScenarioExpectation(
        title="Inspect — Automation Properties",
        required_keywords=("AutomationId",),
    ),
    ScenarioExpectation(
        title="Inspect — High Contrast Validation",
        required_keywords=("contrast", "High contrast"),
    ),
    ScenarioExpectation(
        title="Evidence",
        required_keywords=("artifacts/gui-accessibility",),
    ),
)


def load_accessibility_transcript(path: Path | str) -> AccessibilityTranscript:
    """Load and parse the transcript located at *path*."""

    candidate = Path(path)
    if not candidate.is_file():
        raise AccessibilityTranscriptError(
            f"Accessibility transcript not found: {candidate}"
        )

    text = candidate.read_text(encoding="utf-8")
    title = _extract_title(text)
    sections = _extract_sections(text)
    return AccessibilityTranscript(path=candidate, title=title, sections=sections)


def evaluate_transcript(
    transcript: AccessibilityTranscript,
    *,
    expectations: Iterable[ScenarioExpectation] = DEFAULT_EXPECTATIONS,
) -> TranscriptEvaluation:
    """Evaluate *transcript* against *expectations*."""

    sections = transcript.sections
    results: list[ScenarioResult] = []

    for expectation in expectations:
        section_text = sections.get(expectation.title)
        present = section_text is not None
        missing_keywords: list[str] = []
        if present:
            lowered = section_text.lower()
            for keyword in expectation.required_keywords:
                if keyword.lower() not in lowered:
                    missing_keywords.append(keyword)
        results.append(
            ScenarioResult(
                expectation=expectation,
                present=present,
                missing_keywords=tuple(missing_keywords),
            )
        )

    return TranscriptEvaluation(transcript=transcript, results=tuple(results))


def format_evaluation(evaluation: TranscriptEvaluation) -> list[str]:
    """Render *evaluation* into printable lines."""

    header = "Section".ljust(50) + "Status"
    lines = [header, "-" * len(header)]

    for result in evaluation.results:
        status = result.status.upper()
        lines.append(result.expectation.title.ljust(50) + status)
        for keyword in result.missing_keywords:
            lines.append(f"    ↳ missing keyword: {keyword}")

    if evaluation.transcript.title:
        lines.append("")
        lines.append(f"Transcript: {evaluation.transcript.title}")
        lines.append(f"Location: {evaluation.transcript.path}")

    return lines


def _extract_title(text: str) -> str:
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("# "):
            return stripped[2:].strip()
    return ""


def _extract_sections(text: str) -> dict[str, str]:
    sections: dict[str, list[str]] = {}
    current_title: str | None = None
    for line in text.splitlines():
        if line.startswith("## "):
            current_title = line[3:].strip()
            sections[current_title] = []
            continue
        if current_title is not None:
            sections[current_title].append(line)
    return {key: "\n".join(value).strip() for key, value in sections.items()}
