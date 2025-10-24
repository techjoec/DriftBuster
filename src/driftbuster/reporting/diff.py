"""Diff helpers that canonicalise input before rendering output."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from difflib import SequenceMatcher, unified_diff
from datetime import datetime, timezone
from hashlib import sha256
import re
import xml.etree.ElementTree as ET
from typing import Callable, Iterable, Mapping, MutableMapping, Sequence

from .redaction import RedactionFilter, resolve_redactor


def _apply_redaction(lines: Iterable[str], redactor: RedactionFilter | None) -> list[str]:
    if not redactor:
        return list(lines)
    return [redactor.apply(line) for line in lines]


def _digest(value: str) -> str:
    return f"sha256:{sha256(value.encode('utf-8')).hexdigest()}"


def _digest_bytes(payload: bytes) -> str:
    return f"sha256:{sha256(payload).hexdigest()}"


_BOM = "\ufeff"
_UNICODE_NEWLINES = ("\u2028", "\u2029", "\u0085")

_SAFE_DIFF_MAX_CANONICAL_BYTES = 256 * 1024  # 256 KiB clamp per canonical payload.
_SAFE_DIFF_MAX_DIFF_BYTES = 128 * 1024  # 128 KiB clamp for unified diff output.
_SAFE_DIFF_MAX_DIFF_LINES = 600  # Hard limit for rendered diff lines.


def canonicalise_text(payload: str) -> str:
    """Return ``payload`` with normalised newlines and trimmed trailing spaces."""

    if not payload:
        return ""

    working = payload
    if working.startswith(_BOM):
        working = working.lstrip(_BOM)

    for separator in _UNICODE_NEWLINES:
        if separator in working:
            working = working.replace(separator, "\n")

    normalised = working.replace("\r\n", "\n").replace("\r", "\n")
    lines = [line.rstrip() for line in normalised.split("\n")]
    return "\n".join(lines)


def canonicalise_json(payload: str) -> str:
    """Return JSON payload with deterministically ordered keys.

    When ``payload`` does not contain valid JSON the function falls back to
    :func:`canonicalise_text` so diff generation continues gracefully without
    discarding the original formatting. Valid JSON payloads are normalised
    using ``json.dumps`` with sorted keys and a stable indentation style so
    reviewers see predictable diffs regardless of the source ordering.
    """

    if not payload:
        return ""

    stripped = payload.strip()
    if not stripped:
        return ""

    try:
        parsed = json.loads(stripped)
    except json.JSONDecodeError:
        return canonicalise_text(payload)

    return json.dumps(parsed, ensure_ascii=False, sort_keys=True, indent=2)


_XML_DECLARATION_PATTERN = re.compile(r"<\?xml[^>]*\?>", re.IGNORECASE)


def canonicalise_xml(payload: str) -> str:
    """Return canonical XML with insignificant whitespace stripped.

    The XML declaration and DOCTYPE strings are preserved verbatim when
    present. They are captured before parsing so the normalised body can be
    prefixed with the original prolog, ensuring canonical diffs retain those
    contextual lines for reviewers.
    """

    if not payload:
        return ""

    if payload.startswith(_BOM):
        payload = payload.lstrip(_BOM)

    xml_declaration = ""
    doctype = ""
    working = payload.lstrip()

    declaration_match = _XML_DECLARATION_PATTERN.match(working)
    if declaration_match:
        xml_declaration = declaration_match.group(0)
        working = working[declaration_match.end() :].lstrip()

    if working.upper().startswith("<!DOCTYPE"):
        end = 0
        depth = 0
        for index, character in enumerate(working):
            if character == "[":
                depth += 1
            elif character == "]" and depth:
                depth -= 1
            elif character == ">" and depth == 0 and index:
                end = index + 1
                break
        if end:
            doctype = working[:end]
            working = working[end:].lstrip()
        else:
            # Fall back to the original payload if the DOCTYPE appears malformed.
            working = payload
            xml_declaration = ""
            doctype = ""

    try:
        parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
        root = ET.fromstring(working, parser=parser)
    except ET.ParseError:
        return canonicalise_text(payload)

    def _normalise(element: ET.Element) -> None:
        # Sort attributes but only collapse values that are pure whitespace so
        # intentional padding survives canonicalisation.
        attribute_items = sorted(element.attrib.items())
        element.attrib.clear()
        for key, value in attribute_items:
            stripped = value.strip()
            element.attrib[key] = value if stripped else stripped
        if element.text is not None:
            stripped = element.text.strip()
            if not stripped:
                # Whitespace-only text nodes collapse, otherwise padding stays.
                element.text = stripped
        for child in list(element):
            _normalise(child)
            if child.tail is not None:
                stripped = child.tail.strip()
                if not stripped:
                    # Only trim tails that are entirely whitespace.
                    child.tail = stripped

    _normalise(root)
    serialised = ET.tostring(root, encoding="unicode")
    prolog_parts = [part for part in (xml_declaration, doctype) if part]
    if prolog_parts:
        prolog = "\n".join(prolog_parts)
        return f"{prolog}\n{serialised}"
    return serialised


_NORMALISERS: Mapping[str, Callable[[str], str]] = {
    "text": canonicalise_text,
    "json": canonicalise_json,
    "xml": canonicalise_xml,
}


def _append_notice(text: str, notice: str) -> str:
    stripped = text.rstrip("\n")
    if stripped:
        return f"{stripped}\n{notice}"
    return notice


def _truncate_canonical_payload(
    payload: str,
    *,
    label: str,
    limits: MutableMapping[str, object],
) -> tuple[str, Mapping[str, object] | None]:
    encoded = payload.encode("utf-8")
    size_bytes = len(encoded)
    if size_bytes <= _SAFE_DIFF_MAX_CANONICAL_BYTES:
        return payload, None

    digest = _digest(payload)
    truncated_bytes = size_bytes - _SAFE_DIFF_MAX_CANONICAL_BYTES
    safe_bytes = encoded[:_SAFE_DIFF_MAX_CANONICAL_BYTES]
    safe_payload = safe_bytes.decode("utf-8", "ignore")
    notice = (
        f"… [canonical {label} truncated {truncated_bytes} bytes for safety; digest={digest}]"
    )
    clamped = _append_notice(safe_payload, notice)

    canonical_limits = limits.get("canonical")
    if canonical_limits is None:
        canonical_limits = {}
        limits["canonical"] = canonical_limits
    canonical_limits[label] = {
        "size_bytes": size_bytes,
        "truncated_bytes": truncated_bytes,
        "digest": digest,
    }
    return clamped, canonical_limits[label]


def _truncate_diff_output(
    diff_text: str,
    *,
    limits: MutableMapping[str, object],
) -> tuple[str, Mapping[str, object] | None]:
    if not diff_text:
        return diff_text, None

    lines = diff_text.splitlines()
    total_lines = len(lines)
    total_bytes = len(diff_text.encode("utf-8"))
    digest = _digest(diff_text)

    truncated_lines = 0
    truncated_bytes = 0

    if total_lines > _SAFE_DIFF_MAX_DIFF_LINES:
        truncated_lines = total_lines - _SAFE_DIFF_MAX_DIFF_LINES
        lines = lines[:_SAFE_DIFF_MAX_DIFF_LINES]

    working_text = "\n".join(lines)
    working_bytes = working_text.encode("utf-8")
    if len(working_bytes) > _SAFE_DIFF_MAX_DIFF_BYTES:
        truncated_bytes = len(working_bytes) - _SAFE_DIFF_MAX_DIFF_BYTES
        working_bytes = working_bytes[:_SAFE_DIFF_MAX_DIFF_BYTES]
        working_text = working_bytes.decode("utf-8", "ignore")

    if truncated_lines == 0 and truncated_bytes == 0:
        if total_bytes > _SAFE_DIFF_MAX_DIFF_BYTES:
            truncated_bytes = total_bytes - _SAFE_DIFF_MAX_DIFF_BYTES
            working_bytes = diff_text.encode("utf-8")[:_SAFE_DIFF_MAX_DIFF_BYTES]
            working_text = working_bytes.decode("utf-8", "ignore")
        else:
            return diff_text, None

    segments = []
    if truncated_lines:
        segments.append(f"{truncated_lines} lines")
    if truncated_bytes:
        segments.append(f"{truncated_bytes} bytes")

    notice_detail = " and ".join(segments) if segments else "output"
    notice = f"… [diff truncated {notice_detail} for safety; digest={digest}]"
    clamped = _append_notice(working_text, notice)

    limits["diff"] = {
        "total_lines": total_lines,
        "total_bytes": total_bytes,
        "truncated_lines": truncated_lines,
        "truncated_bytes": truncated_bytes,
        "digest": digest,
    }
    return clamped, limits["diff"]


def _enforce_diff_safety_limits(
    canonical_before: str,
    canonical_after: str,
    diff_text: str,
) -> tuple[str, str, str, Mapping[str, object] | None]:
    limits: MutableMapping[str, object] = {
        "thresholds": {
            "canonical_bytes": _SAFE_DIFF_MAX_CANONICAL_BYTES,
            "diff_bytes": _SAFE_DIFF_MAX_DIFF_BYTES,
            "diff_lines": _SAFE_DIFF_MAX_DIFF_LINES,
        }
    }

    clamped_before, before_info = _truncate_canonical_payload(
        canonical_before, label="before", limits=limits
    )
    clamped_after, after_info = _truncate_canonical_payload(
        canonical_after, label="after", limits=limits
    )

    diff_clamped, diff_info = _truncate_diff_output(diff_text, limits=limits)

    has_truncation = any((before_info, after_info, diff_info))
    if not has_truncation:
        return canonical_before, canonical_after, diff_text, None

    return clamped_before, clamped_after, diff_clamped, limits


@dataclass(frozen=True)
class BinarySegmentEvidence:
    label: str
    before_size: int
    after_size: int
    before_digest: str
    after_digest: str
    changed: bool
    reason: str | None = None


@dataclass(frozen=True)
class DiffResult:
    """Structured diff artefact for downstream adapters."""

    canonical_before: str
    canonical_after: str
    diff: str
    stats: Mapping[str, int]
    content_type: str
    from_label: str
    to_label: str
    label: str | None = None
    mask_tokens: Sequence[str] | None = None
    placeholder: str = "[REDACTED]"
    context_lines: int = 3
    redaction_counts: Mapping[str, int] | None = None
    binary_evidence: Sequence[BinarySegmentEvidence] | None = None
    safety_limits: Mapping[str, object] | None = None


@dataclass(frozen=True)
class DiffChangeSummary:
    before_digest: str
    after_digest: str
    diff_digest: str
    before_lines: int
    after_lines: int
    added_lines: int
    removed_lines: int
    changed_lines: int


@dataclass(frozen=True)
class DiffPlanSummary:
    content_type: str
    from_label: str | None
    to_label: str | None
    label: str | None
    mask_tokens: tuple[str, ...]
    placeholder: str
    context_lines: int
    redaction_counts: tuple[tuple[str, int], ...] = field(default_factory=tuple)
    binary_evidence: tuple[BinarySegmentEvidence, ...] = field(default_factory=tuple)
    safety_limits: Mapping[str, object] | None = None


@dataclass(frozen=True)
class DiffMetadataSummary:
    content_type: str
    context_lines: int
    baseline_name: str | None
    comparison_name: str | None


@dataclass(frozen=True)
class DiffComparisonSummary:
    from_label: str
    to_label: str
    plan: DiffPlanSummary
    metadata: DiffMetadataSummary
    summary: DiffChangeSummary


@dataclass(frozen=True)
class DiffResultSummary:
    generated_at: datetime
    versions: tuple[str, ...]
    comparisons: tuple[DiffComparisonSummary, ...]
    comparison_count: int = field(init=False)

    def __post_init__(self) -> None:
        object.__setattr__(self, "comparison_count", len(self.comparisons))


def _calculate_stats(before: list[str], after: list[str]) -> Mapping[str, int]:
    matcher = SequenceMatcher(None, before, after)
    added = removed = changed = 0
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == "equal":
            continue
        if tag == "replace":
            changed += max(i2 - i1, j2 - j1)
        elif tag == "delete":
            removed += i2 - i1
        elif tag == "insert":
            added += j2 - j1
    return {"added_lines": added, "removed_lines": removed, "changed_lines": changed}


def build_unified_diff(
    before: str,
    after: str,
    *,
    content_type: str = "text",
    from_label: str = "before",
    to_label: str = "after",
    label: str | None = None,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    context_lines: int = 3,
) -> DiffResult:
    """Return :class:`DiffResult` with canonicalised payloads and unified diff."""

    try:
        normaliser = _NORMALISERS[content_type]
    except KeyError as exc:
        raise ValueError(f"Unsupported content_type: {content_type}") from exc

    canonical_before = normaliser(before)
    canonical_after = normaliser(after)

    active_redactor = resolve_redactor(redactor=redactor, mask_tokens=mask_tokens, placeholder=placeholder)
    result_placeholder = placeholder
    if active_redactor:
        result_placeholder = active_redactor.placeholder
    before_lines = _apply_redaction(canonical_before.splitlines(), active_redactor)
    after_lines = _apply_redaction(canonical_after.splitlines(), active_redactor)
    redaction_counts: Mapping[str, int] | None = None
    if active_redactor:
        redaction_counts = active_redactor.stats()
    diff_iter = unified_diff(
        before_lines,
        after_lines,
        fromfile=from_label,
        tofile=to_label,
        lineterm="",
        n=context_lines,
    )
    diff_text = "\n".join(diff_iter)
    stats = _calculate_stats(before_lines, after_lines)
    mask_tuple: Sequence[str] | None = None
    if active_redactor:
        ordered = getattr(active_redactor, "_ordered_tokens", ())
        if ordered:
            mask_tuple = tuple(ordered)
    elif mask_tokens is not None:
        mask_tuple = tuple(mask_tokens)

    safe_before, safe_after, safe_diff, safety_limits = _enforce_diff_safety_limits(
        canonical_before, canonical_after, diff_text
    )

    return DiffResult(
        canonical_before=safe_before,
        canonical_after=safe_after,
        diff=safe_diff,
        stats=stats,
        content_type=content_type,
        from_label=from_label,
        to_label=to_label,
        label=label,
        mask_tokens=mask_tuple,
        placeholder=result_placeholder,
        context_lines=context_lines,
        redaction_counts=redaction_counts,
        binary_evidence=None,
        safety_limits=safety_limits,
    )


def build_binary_diff(
    before: bytes,
    after: bytes,
    *,
    from_label: str = "before",
    to_label: str = "after",
    label: str | None = None,
    reason: str | None = None,
) -> DiffResult:
    """Return :class:`DiffResult` summarising binary payload changes."""

    before_digest = _digest_bytes(before)
    after_digest = _digest_bytes(after)
    evidence = BinarySegmentEvidence(
        label=label or "binary",
        before_size=len(before),
        after_size=len(after),
        before_digest=before_digest,
        after_digest=after_digest,
        changed=before != after,
        reason=reason,
    )
    delta = len(after) - len(before)
    summary_lines = [
        f"binary:{evidence.label}",
        f"- before size={evidence.before_size} digest={before_digest}",
        f"+ after size={evidence.after_size} digest={after_digest}",
    ]
    if delta:
        summary_lines.append(f"Δ bytes: {delta:+d}")

    stats = {
        "added_lines": 0,
        "removed_lines": 0,
        "changed_lines": 1 if evidence.changed else 0,
    }

    return DiffResult(
        canonical_before=before_digest,
        canonical_after=after_digest,
        diff="\n".join(summary_lines),
        stats=stats,
        content_type="binary",
        from_label=from_label,
        to_label=to_label,
        label=label,
        mask_tokens=None,
        placeholder="[REDACTED]",
        context_lines=0,
        redaction_counts=None,
        binary_evidence=(evidence,),
    )


def _build_comparison_summary(
    result: DiffResult,
    baseline_name: str | None,
    comparison_name: str | None,
) -> DiffComparisonSummary:
    """Return a :class:`DiffComparisonSummary` for ``result``."""

    before_lines = result.canonical_before.splitlines()
    after_lines = result.canonical_after.splitlines()
    stats = result.stats or {}

    change_summary = DiffChangeSummary(
        before_digest=_digest(result.canonical_before),
        after_digest=_digest(result.canonical_after),
        diff_digest=_digest(f"{result.canonical_before}\n---\n{result.canonical_after}"),
        before_lines=len(before_lines),
        after_lines=len(after_lines),
        added_lines=int(stats.get("added_lines", 0)),
        removed_lines=int(stats.get("removed_lines", 0)),
        changed_lines=int(stats.get("changed_lines", 0)),
    )

    plan_summary = DiffPlanSummary(
        content_type=result.content_type,
        from_label=result.from_label,
        to_label=result.to_label,
        label=result.label,
        mask_tokens=tuple(result.mask_tokens or ()),
        placeholder=result.placeholder,
        context_lines=result.context_lines,
        redaction_counts=tuple(sorted((result.redaction_counts or {}).items())),
        binary_evidence=tuple(result.binary_evidence or ()),
        safety_limits=result.safety_limits,
    )

    metadata_summary = DiffMetadataSummary(
        content_type=result.content_type,
        context_lines=result.context_lines,
        baseline_name=baseline_name or result.from_label,
        comparison_name=comparison_name or result.to_label,
    )

    return DiffComparisonSummary(
        from_label=result.from_label,
        to_label=result.to_label,
        plan=plan_summary,
        metadata=metadata_summary,
        summary=change_summary,
    )


def summarise_diff_result(
    result: DiffResult,
    *,
    versions: Sequence[str] | None = None,
    baseline_name: str | None = None,
    comparison_name: str | None = None,
) -> DiffResultSummary:
    """Return :class:`DiffResultSummary` describing ``result``."""

    comparison_summary = _build_comparison_summary(result, baseline_name, comparison_name)

    versions_tuple = tuple(versions or ())
    return DiffResultSummary(
        generated_at=datetime.now(timezone.utc),
        versions=versions_tuple,
        comparisons=(comparison_summary,),
    )


def summarise_diff_results(
    results: Sequence[DiffResult],
    *,
    versions: Sequence[str] | None = None,
    baseline_names: Sequence[str | None] | None = None,
    comparison_names: Sequence[str | None] | None = None,
) -> DiffResultSummary:
    """Return a combined :class:`DiffResultSummary` for ``results``.

    Each comparison mirrors :func:`summarise_diff_result` while ensuring callers
    can provide explicit ``baseline_names`` or ``comparison_names`` for the
    generated metadata payload. When a name sequence is supplied its length
    must match ``results``; otherwise the originating ``from_label``/
    ``to_label`` values are reused.
    """

    if not results:
        raise ValueError("results must not be empty")

    if baseline_names is not None and len(baseline_names) != len(results):
        raise ValueError("baseline_names length must match results")
    if comparison_names is not None and len(comparison_names) != len(results):
        raise ValueError("comparison_names length must match results")

    comparisons: list[DiffComparisonSummary] = []
    for index, result in enumerate(results):
        baseline_name = baseline_names[index] if baseline_names is not None else None
        comparison_name = comparison_names[index] if comparison_names is not None else None
        comparisons.append(_build_comparison_summary(result, baseline_name, comparison_name))

    return DiffResultSummary(
        generated_at=datetime.now(timezone.utc),
        versions=tuple(versions or ()),
        comparisons=tuple(comparisons),
    )


def diff_summary_to_payload(summary: DiffResultSummary) -> Mapping[str, object]:
    """Return JSON-ready mapping for ``summary``."""

    comparisons: list[MutableMapping[str, object]] = []
    for comparison in summary.comparisons:
        comparisons.append(
            {
                "from": comparison.from_label,
                "to": comparison.to_label,
                "plan": {
                    "content_type": comparison.plan.content_type,
                    "from_label": comparison.plan.from_label,
                    "to_label": comparison.plan.to_label,
                    "label": comparison.plan.label,
                    "mask_tokens": list(comparison.plan.mask_tokens),
                    "placeholder": comparison.plan.placeholder,
                    "context_lines": comparison.plan.context_lines,
                    "redaction_counts": {
                        token: count for token, count in comparison.plan.redaction_counts
                    },
                    "binary_evidence": [
                        {
                            "label": evidence.label,
                            "before_size": evidence.before_size,
                            "after_size": evidence.after_size,
                            "before_digest": evidence.before_digest,
                            "after_digest": evidence.after_digest,
                            "changed": evidence.changed,
                            "reason": evidence.reason,
                        }
                        for evidence in comparison.plan.binary_evidence
                    ],
                    "safety_limits": (
                        dict(comparison.plan.safety_limits)
                        if comparison.plan.safety_limits
                        else None
                    ),
                },
                "metadata": {
                    "content_type": comparison.metadata.content_type,
                    "context_lines": comparison.metadata.context_lines,
                    "baseline_name": comparison.metadata.baseline_name,
                    "comparison_name": comparison.metadata.comparison_name,
                },
                "summary": {
                    "before_digest": comparison.summary.before_digest,
                    "after_digest": comparison.summary.after_digest,
                    "diff_digest": comparison.summary.diff_digest,
                    "before_lines": comparison.summary.before_lines,
                    "after_lines": comparison.summary.after_lines,
                    "added_lines": comparison.summary.added_lines,
                    "removed_lines": comparison.summary.removed_lines,
                    "changed_lines": comparison.summary.changed_lines,
                },
            }
        )

    return {
        "generated_at": summary.generated_at.isoformat(),
        "versions": list(summary.versions),
        "comparison_count": summary.comparison_count,
        "comparisons": comparisons,
    }


def render_unified_diff(
    before: str,
    after: str,
    *,
    content_type: str = "text",
    from_label: str = "before",
    to_label: str = "after",
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    context_lines: int = 3,
) -> str:
    """Return a unified diff string with token masking applied."""

    result = build_unified_diff(
        before,
        after,
        content_type=content_type,
        from_label=from_label,
        to_label=to_label,
        redactor=redactor,
        mask_tokens=mask_tokens,
        placeholder=placeholder,
        context_lines=context_lines,
    )
    return result.diff
