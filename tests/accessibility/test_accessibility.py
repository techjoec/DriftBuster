from __future__ import annotations

from pathlib import Path

import pytest

from driftbuster.accessibility import (
    AccessibilityTranscriptError,
    DEFAULT_EXPECTATIONS,
    evaluate_transcript,
    format_evaluation,
    load_accessibility_transcript,
)


@pytest.fixture
def sample_transcript(tmp_path: Path) -> Path:
    payload = """# DriftBuster Accessibility Evidence — Test

## Tool Versions
- Windows build: 22631.3155
- Narrator: 2025.106.1
- Inspect: 1.0.1.0

## Narrator — Server Selection Sweep
Focus traversal: Server list -> Run profile list.
Announcements: All list entries labelled.

## Narrator — Drilldown Scenario
Steps recorded for Drilldown.
Announcements include drilldown context.

## Inspect — Automation Properties
AutomationId values captured for key controls.

## Inspect — High Contrast Validation
High contrast guidance recorded with contrast ratios.

## Evidence
Artifacts stored under artifacts/gui-accessibility/test.
"""
    path = tmp_path / "transcript.txt"
    path.write_text(payload, encoding="utf-8")
    return path


def test_load_accessibility_transcript_parses_sections(sample_transcript: Path) -> None:
    transcript = load_accessibility_transcript(sample_transcript)

    assert transcript.title.startswith("DriftBuster Accessibility Evidence")
    assert "Tool Versions" in transcript.sections
    assert "Inspect — Automation Properties" in transcript.sections


def test_evaluate_transcript_detects_missing_keywords(sample_transcript: Path) -> None:
    text = sample_transcript.read_text(encoding="utf-8").replace(
        "Announcements include drilldown context.",
        "Drilldown context captured.",
    )
    sample_transcript.write_text(text, encoding="utf-8")

    transcript = load_accessibility_transcript(sample_transcript)
    evaluation = evaluate_transcript(transcript, expectations=DEFAULT_EXPECTATIONS)

    drilldown = next(
        result
        for result in evaluation.results
        if result.expectation.title == "Narrator — Drilldown Scenario"
    )
    assert drilldown.status == "incomplete"
    assert "Announcements" in drilldown.missing_keywords

    rendered = "\n".join(format_evaluation(evaluation))
    assert "missing keyword" in rendered


def test_load_accessibility_transcript_errors_for_missing(tmp_path: Path) -> None:
    missing = tmp_path / "absent.txt"
    with pytest.raises(AccessibilityTranscriptError):
        load_accessibility_transcript(missing)


def test_load_accessibility_transcript_allows_missing_title(tmp_path: Path) -> None:
    payload = """Tool Versions only

## Tool Versions
- Windows build: 22631.3155
- Narrator: 2025.106.1
- Inspect: 1.0.1.0
"""
    path = tmp_path / "no-title.txt"
    path.write_text(payload, encoding="utf-8")

    transcript = load_accessibility_transcript(path)
    assert transcript.title == ""
