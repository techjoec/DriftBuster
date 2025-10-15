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


def test_validate_detection_metadata_handles_strict_false() -> None:
    match = DetectionMatch(
        plugin_name="custom",
        format_name="Custom-Format",
        variant=" CustomVariant ",
        confidence=0.5,
        reasons=[],
        metadata={"bytes": b"data", "path": Path("/tmp/obj")},
    )

    metadata = validate_detection_metadata(match, DETECTION_CATALOG, strict=False)

    # Variant is lowercased when strict is disabled and bytes become text.
    assert metadata["catalog_format"] == "custom-format"
    assert metadata["catalog_variant"] == "customvariant"
    assert metadata["bytes"] == "data"
    assert metadata["path"] == "/tmp/obj"


def test_validate_detection_metadata_rejects_bad_metadata_type() -> None:
    match = DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant="generic",
        confidence=0.1,
        reasons=[],
        metadata=[("key", "value")],
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(match, DETECTION_CATALOG)


def test_validate_detection_metadata_requires_string_variant() -> None:
    match = DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant=123,  # type: ignore[arg-type]
        confidence=0.3,
        reasons=[],
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(match, DETECTION_CATALOG)
