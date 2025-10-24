"""Regression tests covering shared adapter metadata behaviour."""

from __future__ import annotations

from driftbuster.core.types import DetectionMatch
from driftbuster.reporting._metadata import iter_detection_payloads
from driftbuster.reporting.html import render_html_report
from driftbuster.reporting.json_lines import iter_json_records


def _match() -> DetectionMatch:
    return DetectionMatch(
        plugin_name="demo",
        format_name="json",
        variant="default",
        confidence=0.5,
        reasons=["synthetic"],
        metadata={"token": "value"},
    )


def test_iter_detection_payloads_merges_extra_metadata() -> None:
    match = _match()
    payloads = list(iter_detection_payloads([match], extra_metadata={"run_id": "abc"}))
    assert len(payloads) == 1
    metadata = payloads[0]["metadata"]
    assert metadata["token"] == "value"
    assert metadata["run_id"] == "abc"
    assert match.metadata == {"token": "value"}


def test_json_records_reuse_detection_payloads() -> None:
    match = _match()
    expected = next(iter_detection_payloads([match], extra_metadata={"run_id": "abc"}))
    record = next(iter_json_records([match], extra_metadata={"run_id": "abc"}))
    assert record["type"] == "detection"
    assert record["payload"] == expected


def test_render_html_report_includes_shared_metadata() -> None:
    html = render_html_report([_match()], extra_metadata={"run_id": "abc"})
    assert "run_id" in html
    assert "abc" in html
