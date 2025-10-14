"""Diff helpers that canonicalise input before rendering output."""

from __future__ import annotations

from dataclasses import dataclass
from difflib import SequenceMatcher, unified_diff
from io import StringIO
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


def _attribute_sort_key(name: str) -> tuple[str, str, str]:
    """Return a namespace-aware sort key for attribute ordering."""

    if name.startswith("{") and "}" in name:
        namespace, _brace, local = name[1:].partition("}")
        return (namespace.lower(), local.lower(), name)
    if ":" in name:
        prefix, local = name.split(":", 1)
        return (prefix.lower(), local.lower(), name)
    return ("", name.lower(), name)


def _normalise_element(element: ET.Element) -> None:
    attributes: list[tuple[str, str]] = []
    for key, value in element.attrib.items():
        if isinstance(value, str):
            attributes.append((key, value.strip()))
        else:
            attributes.append((key, value))
    element.attrib.clear()
    for key, value in sorted(attributes, key=lambda item: _attribute_sort_key(item[0])):
        element.attrib[key] = value
    if element.text:
        element.text = element.text.strip()
    for child in list(element):
        _normalise_element(child)
        if child.tail:
            child.tail = child.tail.strip()


def _extract_namespaces(payload: str) -> tuple[list[tuple[str, str]], ET.Element]:
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    stream = StringIO(payload)
    namespaces: list[tuple[str, str]] = []
    try:
        iterator = ET.iterparse(stream, events=("start", "start-ns"), parser=parser)
        for event, value in iterator:
            if event == "start-ns":
                prefix, uri = value
                normalised_prefix = prefix or ""
                if (normalised_prefix, uri) not in namespaces:
                    namespaces.append((normalised_prefix, uri))
        root = iterator.root
    finally:
        stream.close()
    return namespaces, root


def _preserve_namespace_order(namespaces: list[tuple[str, str]]) -> None:
    namespace_map = getattr(ET, "_namespace_map", None)
    if namespace_map is None:
        return
    original = dict(namespace_map)
    namespace_map.clear()
    seen: set[str] = set()
    registered: list[str] = []
    for prefix, uri in namespaces:
        key = prefix or ""
        if key in seen:
            continue
        try:
            ET.register_namespace(key, uri)
            registered.append(key)
        except ValueError:
            continue
        seen.add(key)
    for key, value in original.items():
        if key not in namespace_map:
            namespace_map[key] = value

    def _restore() -> None:
        namespace_map.clear()
        namespace_map.update(original)

    return _restore


def canonicalise_xml(payload: str) -> str:
    """Return canonical XML with insignificant whitespace stripped."""

    if not payload:
        return ""
    try:
        namespaces, root = _extract_namespaces(payload)
    except ET.ParseError:
        return canonicalise_text(payload)

    restore = _preserve_namespace_order(namespaces)
    try:
        _normalise_element(root)
        return ET.tostring(root, encoding="unicode")
    finally:
        if callable(restore):
            restore()


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
