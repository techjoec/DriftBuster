from __future__ import annotations

import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

from driftbuster.font_regression import (
    ExceptionRecord,
    FontRegressionError,
    RegressionEvidence,
    format_evidence,
    load_regression_log,
    regression_evidence_to_dict,
)


@pytest.fixture
def regression_log(tmp_path: Path) -> Path:
    log = tmp_path / "fontmanager-regression.txt"
    log.write_text(
        "[2025-02-14T08:12:32Z] Avalonia: headless font bootstrap failure captured under Release build\nSystem.InvalidOperationException: Could not resolve font alias 'fonts:SystemFonts#Inter'\n   at Avalonia.Media.FontManagerImpl.ThrowFontNotFound(String familyName)\n   at Avalonia.Media.FontManagerImpl.TryCreateGlyphTypeface(String familyName, FontStyle style, FontWeight weight, FontStretch stretch, IGlyphTypeface& glyphTypeface)\n--- inner ---\nSystem.ArgumentException: glyph alias fonts:SystemFonts missing expected Inter fallback entry\n   at DriftBuster.Gui.Headless.HeadlessFontManagerProxy.TryMatchCharacter(UInt32 codepoint, FontStyle style, FontWeight weight, FontStretch stretch, FontMatchOptions matchOptions, Typeface& typeface)\n"
    )
    return log


def test_load_regression_log_parses_exceptions(regression_log: Path) -> None:
    evidence = load_regression_log(regression_log)

    assert isinstance(evidence, RegressionEvidence)
    assert evidence.captured_at == datetime(2025, 2, 14, 8, 12, 32, tzinfo=timezone.utc)
    assert evidence.header.startswith("Avalonia: headless font bootstrap failure")
    assert len(evidence.exceptions) == 2

    first, second = evidence.exceptions
    assert isinstance(first, ExceptionRecord)
    assert first.type_name == "System.InvalidOperationException"
    assert "ThrowFontNotFound" in first.stack[0]
    assert second.type_name == "System.ArgumentException"
    assert "TryMatchCharacter" in second.stack[0]


def test_load_regression_log_errors_when_missing_file(tmp_path: Path) -> None:
    missing = tmp_path / "absent.txt"
    with pytest.raises(FontRegressionError):
        load_regression_log(missing)


def test_regression_evidence_to_dict_serialises(regression_log: Path) -> None:
    evidence = load_regression_log(regression_log)
    payload = regression_evidence_to_dict(evidence)

    assert payload["captured_at"] == "2025-02-14T08:12:32+00:00"
    assert payload["header"].startswith("Avalonia")
    assert payload["exceptions"][0]["type"] == "System.InvalidOperationException"


def test_format_evidence_renders_summary(regression_log: Path) -> None:
    evidence = load_regression_log(regression_log)
    lines = format_evidence(evidence)

    assert any("Exception #1" in line for line in lines)
    assert any("TryMatchCharacter" in line for line in lines)


def test_cli_writes_json_output(regression_log: Path, tmp_path: Path) -> None:
    destination = tmp_path / "out.json"
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "scripts.font_regression_capture",
            str(regression_log),
            "--output",
            str(destination),
        ],
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0
    assert destination.is_file()

    payload = json.loads(destination.read_text())
    assert payload["exceptions"][1]["type"] == "System.ArgumentException"
    assert "FontManagerProxy.TryMatchCharacter" in result.stdout
