from __future__ import annotations

from collections.abc import Mapping
from pathlib import Path

import pytest

from driftbuster.catalog import DETECTION_CATALOG, DetectionCatalog, FallbackClass, FormatClass
from driftbuster.core.types import (
    DetectionMatch,
    MetadataValidationError,
    summarise_metadata,
    validate_detection_metadata,
)
from typed_payloads import as_dict


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


def test_validate_detection_metadata_resolves_alias_format() -> None:
    match = DetectionMatch(
        plugin_name="dockerfile",
        format_name="dockerfile",
        variant="generic",
        confidence=0.8,
        reasons=["Dockerfile heuristics matched"],
    )

    metadata = validate_detection_metadata(match, DETECTION_CATALOG)

    assert metadata["catalog_format"] == "script-config"
    assert metadata["catalog_variant"] == "generic"


def test_validate_detection_metadata_rejects_unknown_variant_for_known_format() -> None:
    match = DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant="mystery",
        confidence=0.4,
        reasons=["unknown"],
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(match, DETECTION_CATALOG)


@pytest.mark.parametrize(
    ("variant",),
    [
        ("nlog-config",),
        ("log4net-config",),
        ("serilog-config",),
    ],
)
def test_validate_detection_metadata_accepts_structured_xml_vendor_variants(variant: str) -> None:
    match = DetectionMatch(
        plugin_name="xml",
        format_name="structured-config-xml",
        variant=variant,
        confidence=0.8,
        reasons=["vendor logging config"],
    )

    metadata = validate_detection_metadata(match, DETECTION_CATALOG)
    assert metadata["catalog_format"] == "structured-config-xml"
    assert metadata["catalog_variant"] == variant


def test_validate_detection_metadata_rejects_bad_metadata_type() -> None:
    match = DetectionMatch(
        plugin_name="json",
        format_name="json",
        variant="generic",
        confidence=0.1,
        reasons=[],
        metadata=[("key", "value")],  # pyright: ignore[reportArgumentType]  # exercises metadata coercion
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


def _format(name: str, slug: str) -> FormatClass:
    return FormatClass(name=name, slug=slug, priority=0, default_severity="low", extensions=())


def _catalog() -> DetectionCatalog:
    return DetectionCatalog(
        version="1.0.0",
        updated="",
        notes=(),
        classes=(
            _format("Json", "json"),
            _format("StructuredConfigXml", "structured-config-xml"),
        ),
        fallback=FallbackClass(
            name="unknown", slug="unknown-text-or-binary", priority=0, default_severity="low", mime_hints=()
        ),
    )


def test_validate_detection_metadata_enforces_types() -> None:
    catalog = _catalog()
    match = DetectionMatch(
        plugin_name="plugin",
        format_name="json",
        variant="Custom",
        confidence=0.5,
        reasons=["reason"],
        metadata={"path": Path("demo"), "values": {"enabled": True}},
    )

    metadata = validate_detection_metadata(match, catalog)
    assert metadata["catalog_format"] == "json"
    assert metadata["catalog_variant"] == "custom"
    assert metadata["path"] == "demo"
    assert metadata["values"]["enabled"] is True
    assert metadata["catalog_version"] == "1.0.0"


def test_validate_detection_metadata_strict_checks() -> None:
    catalog = _catalog()
    match = DetectionMatch(
        plugin_name="plugin",
        format_name="unknown-format",
        variant=None,
        confidence=0.5,
        reasons=[],
        metadata=None,
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(match, catalog)

    relaxed = validate_detection_metadata(match, catalog, strict=False)
    assert relaxed["catalog_format"] == "unknown-format"

    bad_format = DetectionMatch(
        plugin_name="plugin",
        format_name=123,  # type: ignore[arg-type]
        variant=None,
        confidence=0.1,
        reasons=[],
        metadata=None,
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(bad_format, catalog)


def test_summarise_metadata_normalises_values(tmp_path: Path) -> None:
    match = DetectionMatch(
        plugin_name="plugin",
        format_name="json",
        variant="Test",
        confidence=0.5,
        reasons=["reason"],
        metadata={
            "path": tmp_path / "file.txt",
            "bytes": b"data",
            "sequence": {1, 2, 3},
        },
    )

    summary = summarise_metadata(match)
    assert isinstance(summary["metadata"], Mapping)
    assert summary["metadata"]["path"].endswith("file.txt")
    assert "data" in summary["metadata"]["bytes"]


def test_detection_match_to_dict_returns_copy() -> None:
    match = DetectionMatch(
        plugin_name="plugin",
        format_name="json",
        variant=None,
        confidence=0.1,
        reasons=["reason"],
        metadata={"key": "value"},
    )
    payload = match.to_dict()
    assert payload["metadata"] == {"key": "value"}
    as_dict(payload["metadata"])["key"] = "changed"
    assert match.metadata is not None
    assert match.metadata["key"] == "value"
