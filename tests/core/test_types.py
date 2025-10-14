from __future__ import annotations

from pathlib import Path

import pytest

from driftbuster.catalog import DETECTION_CATALOG
from driftbuster.core.types import (
    DetectionMatch,
    MetadataValidationError,
    summarise_metadata,
    validate_detection_metadata,
)


def test_validate_detection_metadata_adds_catalog_fields() -> None:
    match = DetectionMatch(
        plugin_name="xml",
        format_name="xml",
        variant="generic",
        confidence=0.7,
        reasons=["detected xml"],
        metadata={"bytes_sampled": 32},
    )

    metadata = validate_detection_metadata(match, DETECTION_CATALOG)

    assert metadata["catalog_version"] == DETECTION_CATALOG.version
    assert metadata["catalog_format"] == "xml"
    assert metadata["catalog_variant"] == "generic"


def test_validate_detection_metadata_rejects_unknown_format() -> None:
    match = DetectionMatch(
        plugin_name="custom",
        format_name="unknown-format",
        variant=None,
        confidence=0.2,
        reasons=[],
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(match, DETECTION_CATALOG)


def test_summarise_metadata_serialises_values() -> None:
    match = DetectionMatch(
        plugin_name="xml",
        format_name="xml",
        variant="generic",
        confidence=0.9,
        reasons=["detected"],
        metadata={"path": Path("/tmp/config.xml"), "values": {"key": {"nested"}}},
    )
    match.metadata = validate_detection_metadata(match, DETECTION_CATALOG)

    summary = summarise_metadata(match)

    assert summary["plugin"] == "xml"
    assert summary["metadata"]["catalog_format"] == "xml"


def test_summarise_metadata_highlights_namespace() -> None:
    match = DetectionMatch(
        plugin_name="xml",
        format_name="xml",
        variant="resource-xml",
        confidence=0.8,
        reasons=["detected"],
        metadata={
            "root_namespace": "urn:test",
            "schema_locations": [{"namespace": "urn:test", "location": "schema.xsd"}],
            "schema_no_namespace_location": "local.xsd",
        },
    )

    summary = summarise_metadata(match)

    assert "highlights" in summary
    labels = [entry["label"] for entry in summary["highlights"]]
    assert "root_namespace" in labels
    assert any(entry["value"] == "urn:test" for entry in summary["highlights"])
