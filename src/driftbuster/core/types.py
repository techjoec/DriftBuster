"""Shared data structures and metadata helpers for DriftBuster core.

This module centralises the detector dataclasses and the metadata validation
utilities used by downstream tooling.

Example
-------
>>> from driftbuster.catalog import DETECTION_CATALOG
>>> match = DetectionMatch(
...     plugin_name="xml",
...     format_name="xml",
...     variant="resource-xml",
...     confidence=0.9,
...     reasons=["detected root element <root>"]
... )
>>> match.metadata = validate_detection_metadata(match, DETECTION_CATALOG)
>>> summary = summarise_metadata(match)
>>> summary["metadata"]["catalog_format"]
'xml'
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re
from typing import Any, Dict, Iterable, List, Mapping, MutableMapping, Optional

from ..catalog import DetectionCatalog

_VALID_ID = re.compile(r"^[a-z0-9][a-z0-9_-]*$")


class MetadataValidationError(ValueError):
    """Raised when detection metadata fails validation checks."""


_CATALOG_FORMAT_IDS: Dict[str, str] = {
    "RegistryExport": "registry-export",
    "RegistryLive": "registry-live",
    "StructuredConfigXml": "structured-config-xml",
    "XmlGeneric": "xml",
    "Json": "json",
    "Yaml": "yaml",
    "Toml": "toml",
    "Ini": "ini",
    "KeyValueProperties": "properties",
    "UnixConf": "unix-conf",
    "ScriptConfig": "script-config",
    "EmbeddedSqlDb": "embedded-sql-db",
    "GenericBinaryDat": "binary-dat",
}

_FORMAT_ALIASES: Dict[str, str] = {
    "xml-generic": "xml",
    "registry-export": "registry-export",
    "structured-config": "structured-config-xml",
    "structured-config-xml": "structured-config-xml",
    "embedded-sql": "embedded-sql-db",
    "embedded-sqlite": "embedded-sql-db",
    "sqlite": "embedded-sql-db",
    "binary": "binary-dat",
}

_FALLBACK_FORMAT_ID = "unknown-text-or-binary"


def _ensure_mapping(metadata: Optional[Mapping[str, Any]]) -> Dict[str, Any]:
    if metadata is None:
        return {}
    if isinstance(metadata, MutableMapping):
        return dict(metadata)
    if isinstance(metadata, Mapping):
        return dict(metadata.items())
    raise MetadataValidationError(
        "Detection metadata must be a mapping when provided."
    )


def _json_safe(value: Any) -> Any:
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    if isinstance(value, Path):
        return str(value)
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    if isinstance(value, Mapping):
        return {str(key): _json_safe(val) for key, val in value.items()}
    if isinstance(value, (list, tuple, set, frozenset)):
        return [_json_safe(item) for item in value]
    if isinstance(value, Iterable) and not isinstance(value, (str, bytes)):
        return [_json_safe(item) for item in value]
    return str(value)


def _build_format_lookup(
    catalog: DetectionCatalog,
) -> Dict[str, tuple[str, Any]]:
    lookup: Dict[str, tuple[str, Any]] = {}
    for fmt in catalog.classes:
        canonical = _CATALOG_FORMAT_IDS.get(fmt.name)
        if canonical:
            lookup.setdefault(canonical, (canonical, fmt))
    fallback = catalog.fallback
    lookup.setdefault(_FALLBACK_FORMAT_ID, (_FALLBACK_FORMAT_ID, fallback))
    for alias, canonical in _FORMAT_ALIASES.items():
        if canonical in lookup:
            lookup.setdefault(alias, lookup[canonical])
    return lookup


def _normalise_identifier(raw: str, *, field: str) -> str:
    identifier = raw.strip().lower()
    if not identifier:
        raise MetadataValidationError(f"{field} cannot be empty.")
    if not _VALID_ID.match(identifier):
        raise MetadataValidationError(
            f"{field} must be a lowercase slug containing letters, numbers,"
            " hyphen, or underscore."
        )
    return identifier


def validate_detection_metadata(
    match: DetectionMatch,
    catalog: DetectionCatalog,
    *,
    strict: bool = True,
) -> Dict[str, Any]:
    """Validate and enrich ``match.metadata`` against ``catalog``.

    Args:
        match: Detection result to normalise.
        catalog: Detection catalog describing known formats.
        strict: When ``True`` enforces slug-style identifiers and catalog
            membership. ``False`` relaxes missing catalog lookups while still
            normalising metadata.

    Returns:
        Sanitised metadata dictionary ready for serialisation.

    Raises:
        MetadataValidationError: If required attributes fail validation while
            ``strict`` is enabled.
    """

    metadata = _ensure_mapping(match.metadata)
    metadata = {str(key): _json_safe(value) for key, value in metadata.items()}

    format_name = match.format_name
    if not isinstance(format_name, str):
        raise MetadataValidationError(
            "DetectionMatch.format_name must be a string."
        )
    format_id = _normalise_identifier(format_name, field="format_name")

    lookup = _build_format_lookup(catalog)
    canonical_format: Optional[str] = None
    if format_id in lookup:
        canonical_format = lookup[format_id][0]
    elif strict:
        raise MetadataValidationError(f"Unknown catalog format: {format_id}")
    else:
        canonical_format = format_id

    metadata["catalog_version"] = catalog.version
    metadata["catalog_format"] = canonical_format

    variant = match.variant
    if variant is not None:
        if not isinstance(variant, str):
            raise MetadataValidationError(
                "DetectionMatch.variant must be a string when provided."
            )
        if strict:
            variant_id = _normalise_identifier(variant, field="variant")
        else:
            variant_id = variant.strip().lower()
        if variant_id:
            metadata["catalog_variant"] = variant_id
    else:
        metadata.pop("catalog_variant", None)

    return metadata


def summarise_metadata(match: DetectionMatch) -> Mapping[str, Any]:
    """Return a JSON-ready mapping describing ``match`` and its metadata."""

    metadata = _ensure_mapping(match.metadata)
    normalised_metadata = {
        str(key): _json_safe(value)
        for key, value in metadata.items()
    }
    return {
        "plugin": match.plugin_name,
        "format": match.format_name,
        "variant": match.variant,
        "confidence": match.confidence,
        "reasons": list(match.reasons),
        "metadata": normalised_metadata,
    }


@dataclass
class DetectionMatch:
    plugin_name: str
    format_name: str
    variant: Optional[str]
    confidence: float
    reasons: List[str]
    metadata: Optional[Dict[str, Any]] = None

    def to_dict(self) -> dict:
        return {
            "plugin": self.plugin_name,
            "format": self.format_name,
            "variant": self.variant,
            "confidence": self.confidence,
            "reasons": list(self.reasons),
            "metadata": (
                dict(self.metadata) if self.metadata is not None else None
            ),
        }
