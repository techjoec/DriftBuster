from __future__ import annotations

import io
from pathlib import Path

from driftbuster.core.types import DetectionMatch
from driftbuster.hunt import HuntHit, HuntRule
from driftbuster.reporting import json as legacy_json
from driftbuster.reporting.json_lines import iter_json_records, render_json_lines, write_json_lines
from driftbuster.reporting.redaction import RedactionFilter


def _match(format_name: str, *, metadata: dict | None = None) -> DetectionMatch:
    return DetectionMatch(
        plugin_name="json",
        format_name=format_name,
        variant="generic",
        confidence=0.9,
        reasons=["synthetic"],
        metadata=metadata,
    )


def test_iter_json_records_enriches_metadata_and_applies_redaction() -> None:
    redactor = RedactionFilter(tokens=("SECRET",))
    matches = [_match("json", metadata={"token": "SECRET"})]

    rule = HuntRule(name="token", description="")
    hit = HuntHit(rule=rule, path=Path("/tmp/secret.txt"), line_number=1, excerpt="SECRET")

    records = list(
        iter_json_records(
            matches,
            profile_summary={"total": 1},
            hunt_hits=[hit],
            redactor=redactor,
            extra_metadata={"run_id": "abc"},
        )
    )

    detection = records[0]
    assert detection["type"] == "detection"
    payload = detection["payload"]
    assert payload["metadata"]["token"] == "[REDACTED]"
    assert payload["metadata"]["run_id"] == "abc"

    summary = records[1]
    assert summary["type"] == "profile_summary"
    assert summary["payload"]["run_metadata"]["run_id"] == "abc"

    hunt = records[2]
    assert hunt["payload"]["excerpt"] == "[REDACTED]"
    assert hunt["payload"]["run_metadata"]["run_id"] == "abc"


def test_legacy_module_reexports_new_helpers() -> None:
    assert legacy_json.iter_json_records is iter_json_records
    assert legacy_json.render_json_lines is render_json_lines
    assert legacy_json.write_json_lines is write_json_lines


def test_render_and_write_json_lines_preserve_ordering(tmp_path) -> None:
    matches = [_match("json", metadata={"token": "value"})]

    text = render_json_lines(matches, sort_keys=True)
    assert "\n" not in text or text.endswith("\n") is False

    stream = io.StringIO()
    write_json_lines(matches, stream, sort_keys=False)
    stream.seek(0)
    contents = stream.read()
    assert contents.endswith("\n")
    assert "token" in contents

