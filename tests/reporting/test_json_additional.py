from __future__ import annotations

import io

from driftbuster.core.types import DetectionMatch
from driftbuster.hunt import HuntHit, HuntRule
from driftbuster.reporting.json import iter_json_records, render_json_lines, write_json_lines
from driftbuster.reporting.redaction import RedactionFilter


def _match() -> DetectionMatch:
    return DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant="generic",
        confidence=0.9,
        reasons=["reason"],
        metadata={"key": "value"},
    )


def test_iter_json_records_applies_extra_metadata() -> None:
    class SimpleRedactor(RedactionFilter):
        def apply(self, text: str) -> str:
            return text.replace("value", "[MASK]")

    records = list(
        iter_json_records(
            [_match()],
            profile_summary={"total_profiles": 1},
            hunt_hits=[
                HuntHit(
                    rule=HuntRule(name="token", description="desc"),
                    path="file",
                    line_number=1,
                    excerpt="value",
                ),
                {"rule": {"name": "mapping"}, "path": "file", "line_number": 2, "excerpt": "value"},
            ],
            extra_metadata={"run_id": "abc"},
            redactor=SimpleRedactor(),
        )
    )
    assert any(record["type"] == "detection" for record in records)
    assert any(record["type"] == "profile_summary" for record in records)
    assert any(record["type"] == "hunt_hit" for record in records)
    assert all("run_id" in record["payload"]["metadata"] for record in records if record["type"] == "detection")


def test_render_and_write_json_lines() -> None:
    output = render_json_lines([_match()], extra_metadata={"env": "prod"})
    assert "env" in output

    buffer = io.StringIO()
    write_json_lines([_match()], stream=buffer, extra_metadata={"env": "prod"})
    buffer.seek(0)
    lines = buffer.readlines()
    assert len(lines) == 1
