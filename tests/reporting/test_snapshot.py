from __future__ import annotations

from pathlib import Path

from driftbuster.core.types import DetectionMatch
from driftbuster.reporting.redaction import RedactionFilter
from driftbuster.reporting.snapshot import build_snapshot_manifest, write_snapshot


def _match() -> DetectionMatch:
    return DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant="generic",
        confidence=0.8,
        reasons=["demo"],
        metadata={"token": "SECRET"},
    )


def test_build_snapshot_manifest_includes_redaction_stats() -> None:
    redactor = RedactionFilter(tokens=("SECRET",))
    manifest = build_snapshot_manifest(
        [_match()],
        output_name="report.json",
        operator="analyst",
        redactor=redactor,
        legal_metadata={"retention_days": 10},
    )

    assert manifest["output"] == "report.json"
    assert manifest["operator"] == "analyst"
    assert manifest["legal"]["retention_days"] == 10
    assert manifest["legal"]["redacted_tokens"] == {"SECRET": 1}
    assert manifest["matches"][0]["payload"]["metadata"]["token"] == "[REDACTED]"


def test_write_snapshot_creates_file(tmp_path: Path) -> None:
    destination = tmp_path / "snapshot.json"
    write_snapshot([_match()], destination, mask_tokens=("SECRET",), output_name="out")
    assert destination.exists()
    contents = destination.read_text(encoding="utf-8")
    assert "out" in contents
    assert "[REDACTED]" in contents

