from __future__ import annotations

from pathlib import Path

import pytest

import scripts.accessibility_summary as accessibility_summary


@pytest.fixture
def transcript_content() -> str:
    return """# DriftBuster Accessibility Evidence — Test

## Tool Versions
- Windows build: 22631.3155
- Narrator: 2025.106.1
- Inspect: 1.0.1.0

## Narrator — Server Selection Sweep
Focus traversal: Server list -> Run profile list.
Announcements: All list entries labelled.

## Narrator — Drilldown Scenario
Announcements describe drilldown context.

## Inspect — Automation Properties
AutomationId values captured for key controls.

## Inspect — High Contrast Validation
High contrast workflow recorded with contrast ratios.

## Evidence
Artifacts stored under artifacts/gui-accessibility/test.
"""


def write_transcript(tmp_path: Path, content: str) -> Path:
    path = tmp_path / "transcript.txt"
    path.write_text(content, encoding="utf-8")
    return path


def test_main_reports_success(tmp_path: Path, transcript_content: str, capsys: pytest.CaptureFixture[str]) -> None:
    path = write_transcript(tmp_path, transcript_content)

    exit_code = accessibility_summary.main([str(path)])

    output = capsys.readouterr().out
    assert exit_code == 0
    assert "Narrator — Server Selection Sweep" in output
    assert "OK" in output


def test_main_flags_missing_sections(tmp_path: Path, transcript_content: str, capsys: pytest.CaptureFixture[str]) -> None:
    trimmed = transcript_content.replace(
        "## Narrator — Drilldown Scenario\nAnnouncements describe drilldown context.\n\n",
        "",
    )
    path = write_transcript(tmp_path, trimmed)

    exit_code = accessibility_summary.main([str(path)])

    output = capsys.readouterr().out
    assert exit_code == 1
    assert "Narrator — Drilldown Scenario" in output
    assert "MISSING" in output
