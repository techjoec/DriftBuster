from __future__ import annotations

from driftbuster.core.types import DetectionMatch
from driftbuster.reporting.diff import DiffResult
from driftbuster.reporting.html import (
    _render_detection_summary,
    _render_diff_section,
    _render_hunt_section,
    _render_profile_summary,
    render_html_report,
)


def test_render_summary_handles_empty_data() -> None:
    assert _render_detection_summary([]) == ""
    assert _render_profile_summary({}) == ""
    assert _render_diff_section([]) == ""
    assert _render_hunt_section([]) == ""


def test_render_html_report_compiles_sections() -> None:
    diff = DiffResult(
        canonical_before="old",
        canonical_after="new",
        diff="@@\n-old\n+new",
        stats={"added_lines": 1, "removed_lines": 1, "changed_lines": 0},
        label="Config",
    )

    detection = DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant="generic",
        confidence=0.9,
        reasons=["reason"],
        metadata={"key": "value"},
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
        matches=[detection, corrupted],
        diffs=[{"label": "Direct", "diff": ""}, diff],
        hunt_hits=[
            {
                "rule": {"name": "token", "description": "desc", "token_name": "secret"},
                "path": "sample", "line_number": 1, "excerpt": "value",
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
