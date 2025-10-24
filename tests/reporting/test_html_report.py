from __future__ import annotations

from pathlib import Path

from driftbuster.core.types import DetectionMatch
from driftbuster.hunt import HuntHit, HuntRule
from driftbuster.reporting.diff import DiffResult
from driftbuster.reporting.html import render_html_report
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

