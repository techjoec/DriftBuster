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

from ..catalog import DetectionCatalog, FormatClass, FormatSubtype

_VALID_ID = re.compile(r"^[a-z0-9][a-z0-9_-]*$")


class MetadataValidationError(ValueError):
    """Raised when detection metadata fails validation checks."""




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


def _collect_variant_ids(fmt: FormatClass) -> set[str]:
    variants: set[str] = set()
    if fmt.default_variant:
        variants.add(fmt.default_variant.strip().lower())
    for subtype in fmt.subtypes:
        if isinstance(subtype, FormatSubtype):
            if subtype.variant:
                variants.add(subtype.variant.strip().lower())
            if getattr(subtype, "aliases", None):
                variants.update(alias.strip().lower() for alias in subtype.aliases if alias)
    return {variant for variant in variants if variant}


def _build_format_lookup(
    catalog: DetectionCatalog,
) -> Dict[str, tuple[str, set[str]]]:
    lookup: Dict[str, tuple[str, set[str]]] = {}
    for fmt in catalog.classes:
        canonical = fmt.slug.strip().lower()
        variant_ids = _collect_variant_ids(fmt)
        keys = {canonical}
        name_key = fmt.name.strip().lower()
        if name_key:
            keys.add(name_key)
            try:
                keys.add(_normalise_identifier(fmt.name, field="format_name"))
            except MetadataValidationError:
                pass
        keys.update(alias.strip().lower() for alias in fmt.aliases if alias)
        for key in keys:
            if key:
                lookup.setdefault(key, (canonical, set(variant_ids)))
    fallback = catalog.fallback
    fallback_slug = fallback.slug.strip().lower()
    fallback_keys = {fallback_slug, fallback.name.strip().lower()}
    if getattr(fallback, "aliases", None):
        fallback_keys.update(alias.strip().lower() for alias in fallback.aliases if alias)
    for key in fallback_keys:
        if key:
            lookup.setdefault(key, (fallback_slug, set()))
    return lookup


def _find_format_class(catalog: DetectionCatalog, slug: str | None) -> FormatClass | None:
    if not slug:
        return None
    slug_key = slug.strip().lower()
    for fmt in catalog.classes:
        if fmt.slug.strip().lower() == slug_key:
            return fmt
    return None


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
    allowed_variants: set[str] = set()
    if format_id in lookup:
        canonical_format, allowed_variants = lookup[format_id]
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
            if strict and allowed_variants and variant_id not in allowed_variants:
                raise MetadataValidationError(
                    f"Unknown catalog variant '{variant_id}' for format "
                    f"'{canonical_format}'."
                )
            metadata["catalog_variant"] = variant_id
    else:
        metadata.pop("catalog_variant", None)

    fmt_entry = _find_format_class(catalog, canonical_format)
    if fmt_entry is not None:
        severity_value = getattr(fmt_entry, "default_severity", None)
        variant_key = metadata.get("catalog_variant")
        if variant_key:
            for subtype in fmt_entry.subtypes:
                if not isinstance(subtype, FormatSubtype):
                    continue
                subtype_variant = None
                if subtype.variant:
                    subtype_variant = subtype.variant.strip().lower()
                variant_aliases = [
                    alias.strip().lower()
                    for alias in getattr(subtype, "aliases", ())
                    if isinstance(alias, str)
                ]
                if subtype_variant == variant_key or variant_key in variant_aliases:
                    if subtype.severity:
                        severity_value = subtype.severity
                    break
        if severity_value:
            metadata.setdefault("catalog_severity", severity_value)
        severity_hint = getattr(fmt_entry, "severity_hint", None)
        if severity_hint:
            metadata.setdefault("catalog_severity_hint", severity_hint)
        remediation_hints = getattr(fmt_entry, "remediation_hints", ())
        if remediation_hints:
            remediation_payload = []
            for hint in remediation_hints:
                entry = {
                    "id": hint.id,
                    "category": hint.category,
                    "summary": hint.summary,
                }
                if hint.documentation:
                    entry["documentation"] = hint.documentation
                remediation_payload.append(entry)
            metadata.setdefault("catalog_remediations", remediation_payload)
        if getattr(fmt_entry, "references", None):
            metadata.setdefault(
                "catalog_references",
                [reference for reference in fmt_entry.references if reference],
            )

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
