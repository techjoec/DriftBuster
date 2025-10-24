"""Metadata-aware detection summaries for reporting adapters.

This module aggregates detector matches so reporting layers (CLI, HTML, JSON)
can expose variant-level severity and remediation guidance without reimplementing
catalog lookups. It focuses on normalised payloads returned by
:func:`driftbuster.core.types.summarise_metadata`.
"""

from __future__ import annotations

from collections import Counter
from typing import Iterable, Mapping, MutableMapping, Sequence

from ..core.types import DetectionMatch, summarise_metadata


def _is_sequence_of_mappings(value: object) -> bool:
    if isinstance(value, (str, bytes)):
        return False
    return isinstance(value, Sequence)


def summarise_detections(matches: Iterable[DetectionMatch]) -> Mapping[str, object]:
    """Return a metadata summary for ``matches`` grouped by format and variant."""

    total_matches = 0
    format_index: MutableMapping[str, MutableMapping[str, object]] = {}
    severity_counts: Counter[str] = Counter()
    remediation_index: MutableMapping[str, MutableMapping[str, object]] = {}

    for match in matches:
        total_matches += 1
        payload = summarise_metadata(match)
        format_name = str(payload.get("format") or "unknown")
        raw_variant = payload.get("variant")
        variant_name = str(raw_variant) if raw_variant else "—"
        metadata = payload.get("metadata")
        metadata_map: Mapping[str, object]
        if isinstance(metadata, Mapping):
            metadata_map = metadata
        else:
            metadata_map = {}

        format_bucket = format_index.setdefault(
            format_name,
            {
                "format": format_name,
                "total": 0,
                "variants": {},
            },
        )
        format_bucket["total"] = int(format_bucket.get("total", 0)) + 1

        variants = format_bucket.setdefault("variants", {})
        variant_bucket = variants.setdefault(
            variant_name,
            {
                "variant": None if variant_name == "—" else variant_name,
                "count": 0,
                "max_confidence": 0.0,
                "reasons": set(),
                "metadata_keys": set(),
                "severity": None,
                "severity_hint": None,
                "remediation_ids": [],
            },
        )
        variant_bucket["count"] = int(variant_bucket.get("count", 0)) + 1
        try:
            confidence = float(payload.get("confidence", 0.0) or 0.0)
        except (TypeError, ValueError):
            confidence = 0.0
        variant_bucket["max_confidence"] = max(float(variant_bucket["max_confidence"]), confidence)

        reasons = variant_bucket.setdefault("reasons", set())
        if isinstance(reasons, set):
            for reason in payload.get("reasons", []):
                reasons.add(str(reason))

        metadata_keys = variant_bucket.setdefault("metadata_keys", set())
        if isinstance(metadata_keys, set):
            for key in metadata_map.keys():
                metadata_keys.add(str(key))

        severity = metadata_map.get("catalog_severity")
        if isinstance(severity, str) and severity:
            severity_counts[severity] += 1
            if not variant_bucket.get("severity"):
                variant_bucket["severity"] = severity

        severity_hint = metadata_map.get("catalog_severity_hint")
        if isinstance(severity_hint, str) and severity_hint:
            if not variant_bucket.get("severity_hint"):
                variant_bucket["severity_hint"] = severity_hint

        remediations = metadata_map.get("catalog_remediations")
        if _is_sequence_of_mappings(remediations):
            stored_ids = variant_bucket.setdefault("remediation_ids", [])
            for entry in remediations:  # type: ignore[assignment]
                if not isinstance(entry, Mapping):
                    continue
                remediation_id = entry.get("id")
                remediation_summary = entry.get("summary")
                if remediation_id:
                    remediation_id = str(remediation_id)
                if remediation_summary:
                    remediation_summary = str(remediation_summary)
                if remediation_id and remediation_id not in stored_ids:
                    stored_ids.append(remediation_id)
                key = remediation_id or remediation_summary
                if not key:
                    continue
                record = remediation_index.setdefault(
                    key,
                    {
                        "id": remediation_id,
                        "category": entry.get("category"),
                        "summary": remediation_summary,
                        "documentation": entry.get("documentation"),
                        "formats": set(),
                        "variants": set(),
                    },
                )
                record.setdefault("summary", remediation_summary)
                if remediation_id and not record.get("id"):
                    record["id"] = remediation_id
                if entry.get("category") and not record.get("category"):
                    record["category"] = entry.get("category")
                if entry.get("documentation") and not record.get("documentation"):
                    record["documentation"] = entry.get("documentation")
                formats = record.setdefault("formats", set())
                variants_set = record.setdefault("variants", set())
                if isinstance(formats, set):
                    formats.add(format_name)
                if isinstance(variants_set, set) and variant_name != "—":
                    variants_set.add(str(variant_name))

    unique_formats = len(format_index)
    unique_variants = sum(len(entry["variants"]) for entry in format_index.values())

    formatted_formats = []
    for format_name in sorted(format_index.keys()):
        entry = format_index[format_name]
        variants = entry.get("variants", {})
        formatted_variants = []
        for variant_name in sorted(variants.keys()):
            bucket = variants[variant_name]
            formatted_variants.append(
                {
                    "variant": bucket.get("variant"),
                    "count": bucket.get("count", 0),
                    "max_confidence": float(bucket.get("max_confidence", 0.0)),
                    "reasons": sorted(bucket.get("reasons", [])),
                    "metadata_keys": sorted(bucket.get("metadata_keys", [])),
                    "severity": bucket.get("severity"),
                    "severity_hint": bucket.get("severity_hint"),
                    "remediation_ids": sorted(bucket.get("remediation_ids", [])),
                }
            )
        formatted_formats.append(
            {
                "format": format_name,
                "total": entry.get("total", 0),
                "variants": formatted_variants,
            }
        )

    formatted_remediations = []
    for key in sorted(remediation_index.keys()):
        entry = remediation_index[key]
        formats = entry.get("formats", set())
        variants = entry.get("variants", set())
        formatted_remediations.append(
            {
                "id": entry.get("id"),
                "category": entry.get("category"),
                "summary": entry.get("summary"),
                "documentation": entry.get("documentation"),
                "formats": sorted(formats) if isinstance(formats, set) else [],
                "variants": sorted(variants) if isinstance(variants, set) else [],
            }
        )

    return {
        "total_matches": total_matches,
        "unique_formats": unique_formats,
        "unique_variants": unique_variants,
        "severity_counts": dict(sorted(severity_counts.items())),
        "formats": formatted_formats,
        "remediations": formatted_remediations,
    }
