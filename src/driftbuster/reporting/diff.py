"""Diff helpers that canonicalise input before rendering output."""

from __future__ import annotations

from dataclasses import dataclass
from difflib import SequenceMatcher, unified_diff
import xml.etree.ElementTree as ET
from typing import Callable, Iterable, Mapping, Sequence

from .redaction import RedactionFilter, resolve_redactor


def _apply_redaction(lines: Iterable[str], redactor: RedactionFilter | None) -> list[str]:
    if not redactor:
        return list(lines)
    return [redactor.apply(line) for line in lines]


def canonicalise_text(payload: str) -> str:
    """Return ``payload`` with normalised newlines and trimmed trailing spaces."""

    if not payload:
        return ""
    normalised = payload.replace("\r\n", "\n").replace("\r", "\n")
    lines = [line.rstrip() for line in normalised.split("\n")]
    return "\n".join(lines)


def canonicalise_xml(payload: str) -> str:
    """Return canonical XML with insignificant whitespace stripped."""

    if not payload:
        return ""
    try:
        parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
        root = ET.fromstring(payload, parser=parser)
    except ET.ParseError:
        return canonicalise_text(payload)

    def _normalise(element: ET.Element) -> None:
        attributes = {key: value.strip() for key, value in element.attrib.items()}
        element.attrib.clear()
        for key in sorted(attributes):
            element.attrib[key] = attributes[key]
        if element.text:
            element.text = element.text.strip()
        for child in list(element):
            _normalise(child)
            if child.tail:
                child.tail = child.tail.strip()

    _normalise(root)
    return ET.tostring(root, encoding="unicode")


_NORMALISERS: Mapping[str, Callable[[str], str]] = {
    "text": canonicalise_text,
    "xml": canonicalise_xml,
}


@dataclass(frozen=True)
class DiffResult:
    """Structured diff artefact for downstream adapters."""

    canonical_before: str
    canonical_after: str
    diff: str
    stats: Mapping[str, int]
    label: str | None = None


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
    return DiffResult(
        canonical_before=canonical_before,
        canonical_after=canonical_after,
        diff=diff_text,
        stats=stats,
        label=label,
    )


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
