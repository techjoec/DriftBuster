from __future__ import annotations

from driftbuster.catalog import DETECTION_CATALOG
from driftbuster.core.types import DetectionMatch, validate_detection_metadata
from driftbuster.reporting.summary import summarise_detections


def _build_match(
    format_name: str,
    variant: str | None,
    *,
    confidence: float = 0.8,
    plugin_name: str | None = None,
) -> DetectionMatch:
    match = DetectionMatch(
        plugin_name=plugin_name or format_name,
        format_name=format_name,
        variant=variant,
        confidence=confidence,
        reasons=["synthetic"],
    )
    match.metadata = validate_detection_metadata(match, DETECTION_CATALOG)
    return match


def test_summary_captures_variant_metadata_and_remediations() -> None:
    dotenv = _build_match("ini", "dotenv")
    unix_conf = _build_match(
        "unix-conf",
        "generic-directive-text",
        confidence=0.6,
        plugin_name="conf",
    )

    summary = summarise_detections([dotenv, unix_conf])

    assert summary["total_matches"] == 2
    assert summary["unique_formats"] == 2
    assert summary["severity_counts"] == {"high": 1, "medium": 1}

    formats = {entry["format"]: entry for entry in summary["formats"]}
    ini_summary = formats["ini"]
    dotenv_variant = ini_summary["variants"][0]

    assert dotenv_variant["severity"] == "high"
    assert "catalog_severity" in dotenv_variant["metadata_keys"]
    remediation_ids = set(dotenv_variant["remediation_ids"])
    assert {"ini-dotenv-rotate-secrets", "ini-secret-rotation"}.issubset(remediation_ids)

    remediation_index = {entry["id"] for entry in summary["remediations"]}
    assert {"ini-secret-rotation", "ini-dotenv-rotate-secrets"}.issubset(remediation_index)

    dotenv_remediation = {
        entry["id"]: entry for entry in summary["remediations"]
    }["ini-dotenv-rotate-secrets"]
    assert dotenv_remediation["formats"] == ["ini"]
    assert dotenv_remediation["variants"] == ["dotenv"]


def test_summary_handles_matches_without_metadata() -> None:
    plain_match = DetectionMatch(
        plugin_name="text",
        format_name="text",
        variant=None,
        confidence=0.4,
        reasons=[],
    )

    summary = summarise_detections([plain_match])

    assert summary["total_matches"] == 1
    text_summary = summary["formats"][0]
    variant_entry = text_summary["variants"][0]

    assert variant_entry["variant"] is None
    assert variant_entry["remediation_ids"] == []
    assert summary["remediations"] == []
    assert summary["severity_counts"] == {}
