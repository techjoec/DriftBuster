from __future__ import annotations

from io import StringIO
from pathlib import Path

from driftbuster.core.types import DetectionMatch
from driftbuster.hunt import HuntHit, HuntRule
from driftbuster.reporting.diff import DiffResult
from driftbuster.reporting.html import render_html_report, write_html_report
from driftbuster.reporting.redaction import RedactionFilter


def _match() -> DetectionMatch:
    return DetectionMatch(
        plugin_name="xml",
        format_name="xml",
        variant="resource",
        confidence=0.75,
        reasons=["demo"],
        metadata={"token": "SECRET", "format": "xml"},
    )


def test_render_html_report_includes_sections() -> None:
    redactor = RedactionFilter(tokens=("SECRET", "token"), placeholder="***")
    diff = DiffResult(
        canonical_before="a",
        canonical_after="b",
        diff="-a\n+b",
        stats={"added_lines": 1},
        content_type="text",
        from_label="before",
        to_label="after",
        label="config",
    )

    rule = HuntRule(name="rule", description="")
    hit = HuntHit(rule=rule, path=Path("/tmp/file.txt"), line_number=3, excerpt="SECRET value")

    html = render_html_report(
        [_match()],
        title="Example",
        diffs=[diff],
        profile_summary={
            "total_profiles": 1,
            "profiles": [
                {
                    "name": "default",
                    "config_count": 1,
                    "config_ids": ["cfg1"],
                }
            ],
        },
        hunt_hits=[hit],
        redactor=redactor,
        extra_metadata={"run_id": "XYZ"},
        warnings=["Check manually"],
        legal_notice="Handle with care",
    )

    assert "Example" in html
    assert "Detection Summary" in html
    assert "***" in html  # redacted token
    assert "Run saved" not in html  # ensure we didn't accidentally leak other strings
    assert "Profile Summary" in html
    assert "Configuration Diffs" in html
    assert "Hunt Highlights" in html
    assert "Redaction Summary" in html
    assert "Handle with care" in html


def test_render_html_report_handles_no_redaction_hits() -> None:
    html = render_html_report([_match()], warnings=["Only sample"])
    assert "Derived data only" in html
    assert "No configured tokens were encountered" in html


def test_write_html_report_accepts_stream_and_path(tmp_path: Path) -> None:
    buffer = StringIO()
    write_html_report([_match()], buffer, title="Stream Output")
    contents = buffer.getvalue()
    assert "Stream Output" in contents
    assert contents.strip().startswith("<!doctype html>")

    target = tmp_path / "report.html"
    write_html_report([_match()], target, title="Disk Output")
    written = target.read_text(encoding="utf-8")
    assert "Disk Output" in written
    assert "DriftBuster" not in target.name  # ensure file naming left to caller


def test_render_html_report_tolerates_mapping_inputs_and_corrupt_entries() -> None:
    diff = DiffResult(
        canonical_before="old",
        canonical_after="new",
        diff="@@\n-old\n+new",
        stats={"added_lines": 1, "removed_lines": 1, "changed_lines": 0},
        content_type="text",
        from_label="before",
        to_label="after",
        label="Config",
    )
    corrupted = DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant="generic",
        confidence="invalid",  # type: ignore[arg-type]
        reasons=[],
        metadata={},
    )

    html = render_html_report(
        matches=[_match(), corrupted],
        diffs=[{"label": "Direct", "diff": ""}, diff],
        hunt_hits=[
            {
                "rule": {"name": "token", "description": "desc", "token_name": "secret"},
                "path": "sample",
                "line_number": 1,
                "excerpt": "value",
            }
        ],
        profile_summary={
            "total_profiles": 1,
            "profiles": [
                {"name": "demo", "config_count": 1, "config_ids": ("cfg",)},
                "invalid",
            ],
        },
    )

    assert "Detection Summary" in html
    assert "Configuration Diffs" in html
    assert "Hunt Highlights" in html
    assert "Profile Summary" in html


def test_render_html_report_shows_diff_safety_notice() -> None:
    html = render_html_report(
        [_match()],
        diffs=[
            {
                "label": "Large",
                "diff": "-old\n+new",
                "stats": {"added_lines": 1},
                "safety_limits": {
                    "diff": {
                        "total_lines": 6,
                        "total_bytes": 120,
                        "truncated_lines": 2,
                        "truncated_bytes": 16,
                        "digest": "sha256:abc",
                    },
                    "thresholds": {
                        "canonical_bytes": 10,
                        "diff_bytes": 20,
                        "diff_lines": 2,
                    },
                },
            }
        ],
    )

    assert "Diff output truncated for safety" in html
    assert "diff truncated 2 lines" in html
