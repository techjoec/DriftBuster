"""Snapshot helper that embeds legal metadata and redaction state."""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Mapping, Sequence

from ..core.types import DetectionMatch
from .json import iter_json_records
from .redaction import RedactionFilter, resolve_redactor


def build_snapshot_manifest(
    matches: Iterable[DetectionMatch],
    *,
    output_name: str | None = None,
    operator: str | None = None,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    legal_metadata: Mapping[str, object] | None = None,
    extra_metadata: Mapping[str, object] | None = None,
) -> Mapping[str, object]:
    """Return a manifest describing ``matches`` with compliance metadata attached."""

    active_redactor = resolve_redactor(redactor=redactor, mask_tokens=mask_tokens, placeholder=placeholder)
    run_metadata = dict(extra_metadata or {})
    if operator:
        run_metadata.setdefault("operator", operator)
    if output_name:
        run_metadata.setdefault("snapshot_output", output_name)

    records = list(
        iter_json_records(
            matches,
            redactor=active_redactor,
            extra_metadata=run_metadata if run_metadata else None,
        )
    )
    legal_block: dict[str, object] = {
        "classification": "internal-only",
        "redaction_placeholder": placeholder,
        "retention_days": 30,
        "disposal": "Securely delete snapshots after review or when superseded.",
        "warnings": [
            "Do not forward snapshots outside the trusted review group.",
            "Confirm redaction before sharing derived artefacts.",
        ],
    }
    if legal_metadata:
        legal_block.update(legal_metadata)
    if active_redactor and active_redactor.has_hits:
        legal_block["redacted_tokens"] = active_redactor.stats()

    manifest: dict[str, object] = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "output": output_name,
        "operator": operator,
        "legal": legal_block,
        "matches": records,
    }
    if run_metadata:
        manifest["run_metadata"] = run_metadata
    return manifest


def write_snapshot(
    matches: Iterable[DetectionMatch],
    destination: Path,
    *,
    operator: str | None = None,
    output_name: str | None = None,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    legal_metadata: Mapping[str, object] | None = None,
    indent: int = 2,
    extra_metadata: Mapping[str, object] | None = None,
) -> None:
    """Write a snapshot manifest to ``destination`` including legal guardrails."""

    manifest = build_snapshot_manifest(
        matches,
        output_name=output_name,
        operator=operator,
        redactor=redactor,
        mask_tokens=mask_tokens,
        placeholder=placeholder,
        legal_metadata=legal_metadata,
        extra_metadata=extra_metadata,
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=indent)
        handle.write("\n")
