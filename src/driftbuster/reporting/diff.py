"""Diff helpers that canonicalise input before rendering output."""

from __future__ import annotations

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


def canonicalise_text(payload: str) -> str:
    """Return ``payload`` with normalised newlines and trimmed trailing spaces."""

    if not payload:
        return ""
    normalised = payload.replace("\r\n", "\n").replace("\r", "\n")
    lines = [line.rstrip() for line in normalised.split("\n")]
    return "\n".join(lines)


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
    "xml": canonicalise_xml,
}


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
    binary_evidence: Sequence[BinarySegmentEvidence] | None = None


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
    binary_evidence: tuple[BinarySegmentEvidence, ...] = field(default_factory=tuple)


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
    before_lines = _apply_redaction(canonical_before.splitlines(), active_redactor)
    after_lines = _apply_redaction(canonical_after.splitlines(), active_redactor)
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
    if mask_tokens is not None:
        mask_tuple = tuple(mask_tokens)

    return DiffResult(
        canonical_before=canonical_before,
        canonical_after=canonical_after,
        diff=diff_text,
        stats=stats,
        content_type=content_type,
        from_label=from_label,
        to_label=to_label,
        label=label,
        mask_tokens=mask_tuple,
        placeholder=placeholder,
        context_lines=context_lines,
        binary_evidence=None,
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
        binary_evidence=tuple(result.binary_evidence or ()),
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
