"""Newline-delimited JSON helpers for DriftBuster reports.

These helpers power the reporting backlog tracked under CLOUDTASKS area
``A11``.  They expose a streaming friendly API that keeps the existing JSON
payload structure but makes it trivial to serialise large detection batches
without loading them into memory first.  The functions mirror the historical
``reporting.json`` helpers so legacy imports keep working after the module
split.
"""

from __future__ import annotations

import json
from typing import Any, Iterable, Iterator, Mapping, Sequence, TextIO

from ..core.types import DetectionMatch
from ._metadata import iter_detection_payloads
from ..hunt import HuntHit
from .redaction import RedactionFilter, redact_data, resolve_redactor

__all__ = ["iter_json_records", "render_json_lines", "write_json_lines"]


def _prepare_record(
    kind: str,
    payload: Mapping[str, Any],
    *,
    redactor: RedactionFilter | None,
) -> dict[str, Any]:
    record = {"type": kind, "payload": dict(payload)}
    if redactor:
        record = redact_data(record, redactor)
    return record


def _serialise_hunt_hit(hit: HuntHit | Mapping[str, Any]) -> Mapping[str, Any]:
    if isinstance(hit, Mapping):
        return dict(hit)
    rule = hit.rule
    return {
        "rule": {
            "name": rule.name,
            "description": rule.description,
            "token_name": rule.token_name,
            "keywords": tuple(getattr(pattern, "pattern", pattern) for pattern in rule.patterns),
        },
        "path": str(hit.path),
        "line_number": hit.line_number,
        "excerpt": hit.excerpt,
    }


def iter_json_records(
    matches: Iterable[DetectionMatch],
    *,
    profile_summary: Mapping[str, Any] | None = None,
    hunt_hits: Iterable[HuntHit | Mapping[str, Any]] | None = None,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    extra_metadata: Mapping[str, object] | None = None,
) -> Iterator[dict[str, Any]]:
    """Yield JSON-compatible records for detections and related payloads."""

    active_redactor = resolve_redactor(redactor=redactor, mask_tokens=mask_tokens, placeholder=placeholder)
    run_metadata = dict(extra_metadata or {})

    for payload in iter_detection_payloads(matches, extra_metadata=run_metadata):
        yield _prepare_record("detection", payload, redactor=active_redactor)

    if profile_summary:
        summary_payload = dict(profile_summary)
        if run_metadata:
            summary_payload.setdefault("run_metadata", {}).update(run_metadata)
        yield _prepare_record("profile_summary", summary_payload, redactor=active_redactor)

    if hunt_hits:
        for raw_hit in hunt_hits:
            hit_payload = dict(_serialise_hunt_hit(raw_hit))
            if run_metadata:
                hit_payload.setdefault("run_metadata", {}).update(run_metadata)
            yield _prepare_record("hunt_hit", hit_payload, redactor=active_redactor)


def render_json_lines(
    matches: Iterable[DetectionMatch],
    *,
    profile_summary: Mapping[str, Any] | None = None,
    hunt_hits: Iterable[HuntHit | Mapping[str, Any]] | None = None,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    extra_metadata: Mapping[str, object] | None = None,
    sort_keys: bool = True,
) -> str:
    """Return newline-delimited JSON with optional token redaction."""

    lines = [
        json.dumps(record, ensure_ascii=False, sort_keys=sort_keys)
        for record in iter_json_records(
            matches,
            profile_summary=profile_summary,
            hunt_hits=hunt_hits,
            redactor=redactor,
            mask_tokens=mask_tokens,
            placeholder=placeholder,
            extra_metadata=extra_metadata,
        )
    ]
    return "\n".join(lines)


def write_json_lines(
    matches: Iterable[DetectionMatch],
    stream: TextIO,
    *,
    profile_summary: Mapping[str, Any] | None = None,
    hunt_hits: Iterable[HuntHit | Mapping[str, Any]] | None = None,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    extra_metadata: Mapping[str, object] | None = None,
    sort_keys: bool = True,
) -> None:
    """Write newline-delimited JSON to ``stream`` honouring the redaction policy."""

    for record in iter_json_records(
        matches,
        profile_summary=profile_summary,
        hunt_hits=hunt_hits,
        redactor=redactor,
        mask_tokens=mask_tokens,
        placeholder=placeholder,
        extra_metadata=extra_metadata,
    ):
        stream.write(json.dumps(record, ensure_ascii=False, sort_keys=sort_keys))
        stream.write("\n")

