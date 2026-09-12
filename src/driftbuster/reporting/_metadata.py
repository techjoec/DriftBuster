"""Shared helpers for reporting adapters to consume detection metadata consistently."""

from __future__ import annotations

from collections.abc import Iterable, Iterator, Mapping

from ..core.types import DetectionMatch, summarise_metadata

__all__ = ["iter_detection_payloads"]


def iter_detection_payloads(
    matches: Iterable[DetectionMatch],
    *,
    extra_metadata: Mapping[str, object] | None = None,
) -> Iterator[dict[str, object]]:
    """Yield normalised detection payloads with optional metadata enrichment."""

    run_metadata = dict(extra_metadata or {})
    for match in matches:
        summary = dict(summarise_metadata(match))
        metadata = summary.get("metadata")
        metadata_map = dict(metadata) if isinstance(metadata, Mapping) else {}
        if run_metadata:
            metadata_map.update(run_metadata)
        summary["metadata"] = metadata_map
        yield summary
