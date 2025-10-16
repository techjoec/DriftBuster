from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from types import MappingProxyType
from typing import Any, Iterable, List, Mapping, Sequence

import pytest

from driftbuster.core.types import (
    DetectionMatch,
    MetadataValidationError,
    _ensure_mapping,
    _json_safe,
    _normalise_identifier,
    validate_detection_metadata,
    summarise_metadata,
)


@dataclass
class DummyCatalogFormat:
    name: str


@dataclass
class DummyCatalog:
    version: str
    classes: Sequence[DummyCatalogFormat]
    fallback: DummyCatalogFormat


def _catalog() -> DummyCatalog:
    return DummyCatalog(
        version="1.0.0",
        classes=[DummyCatalogFormat("Json"), DummyCatalogFormat("StructuredConfigXml")],
        fallback=DummyCatalogFormat("unknown"),
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


def test_validate_detection_metadata_variant_type() -> None:
    catalog = _catalog()
    match = DetectionMatch(
        plugin_name="plugin",
        format_name="json",
        variant=123,  # type: ignore[arg-type]
        confidence=0.5,
        reasons=[],
        metadata=None,
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(match, catalog)


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


def test_validate_detection_metadata_rejects_non_mapping_metadata() -> None:
    catalog = _catalog()
    match = DetectionMatch(
        plugin_name="plugin",
        format_name="json",
        variant=None,
        confidence=0.9,
        reasons=[],
        metadata=[("invalid", "value")],  # type: ignore[list-as-mapping]
    )

    with pytest.raises(MetadataValidationError):
        validate_detection_metadata(match, catalog)


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
    payload["metadata"]["key"] = "changed"
    assert match.metadata["key"] == "value"


def test_internal_helpers_cover_edge_cases(tmp_path: Path) -> None:
    with pytest.raises(MetadataValidationError):
        _ensure_mapping([("invalid", "value")])  # type: ignore[list-as-mapping]

    proxy = MappingProxyType({"key": "value"})
    assert _ensure_mapping(proxy) == {"key": "value"}

    assert _json_safe((item for item in [1, 2, 3])) == [1, 2, 3]
    assert _json_safe({tmp_path}) == [str(tmp_path)]
    class Custom:
        def __str__(self) -> str:
            return "custom-object"

    assert _json_safe(Custom()) == "custom-object"

    with pytest.raises(MetadataValidationError):
        _normalise_identifier("Invalid Name", field="variant")
    with pytest.raises(MetadataValidationError):
        _normalise_identifier("  ", field="variant")
