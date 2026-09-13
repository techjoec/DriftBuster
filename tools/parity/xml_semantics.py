"""Semantic check behind the fix d normaliser of run_parity.sh (diff and canon surfaces).

Temporary; deleted together with the Python package.

Usage:
    xml_semantics.py <diff|canon> <python.jsonl> <port.jsonl> [--root <canon root>]

The text normaliser in run_parity.sh drops every xmlns declaration and renames Python's ns0/ns1 prefixes to the port's, so
by itself it cannot see a declaration written on the wrong element, a missing xmlns="" undeclaration or an unbound prefix.
This check closes that gap: for every record pair whose content type is xml, each canonical text (``canonical`` on canon,
``canonical_before`` / ``canonical_after`` on diff) is parsed on both sides with namespace processing, after removing the XML
declaration and DOCTYPE prolog the canonicaliser writes verbatim, and the two element trees are compared by expanded name
(``{uri}local``): tags, attributes, text, tails, comments and child order. A text that parses on one side only, or trees that
differ, is reported and the exit status is 1. A text that parses on neither side (a text fallback or a clamped payload) is
left to the textual compare.

The fix itself (the port keeps the source prefixes) is asserted separately: for every namespaced record (an xml canonical text
on either side declares a namespace), each port canonical text and the source document it came from (``<root>/<path>`` on
canon, the pair's before or after file on diff, read as UTF-8 with replacement) are parsed without namespace processing, and
the qualified names as written, each element's tag in document order with its sorted non-xmlns attribute names, must be
identical. A port text that does not parse where Python's does fails; a source or text that parses on neither side is skipped.
"""

from __future__ import annotations

import json
import re
import sys
import xml.etree.ElementTree as ET
import xml.parsers.expat
from pathlib import Path

# The same pattern and flags as reporting.diff._XML_DECLARATION_PATTERN, so an upper-case <?XML ...?> is stripped too.
_DECLARATION = re.compile(r"<\?xml[^>]*\?>", re.IGNORECASE)


def _strip_prolog(text: str) -> str:
    working = text.lstrip()
    declaration = _DECLARATION.match(working)
    if declaration:
        working = working[declaration.end():].lstrip()
    if working.upper().startswith("<!DOCTYPE"):
        depth = 0
        for index, character in enumerate(working):
            if character == "[":
                depth += 1
            elif character == "]" and depth:
                depth -= 1
            elif character == ">" and depth == 0 and index:
                return working[index + 1:].lstrip()
    return working


def _tree(text: str) -> object:
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    try:
        root = ET.fromstring(_strip_prolog(text), parser=parser)
    except ET.ParseError as exc:
        return ("unparsable", str(exc))

    def shape(element: ET.Element) -> tuple[object, ...]:
        tag = "<!--" if element.tag is ET.Comment else element.tag
        return (tag, sorted(element.attrib.items()), element.text, element.tail, [shape(child) for child in element])

    return ("tree", shape(root))


def _texts(surface: str, record: dict[str, object]) -> list[tuple[str, object]]:
    if surface == "canon":
        if record.get("content_type") != "xml":
            return []
        return [("canonical", record.get("canonical"))]
    result = record.get("result")
    if not isinstance(result, dict) or result.get("content_type") != "xml":
        return []
    return [("canonical_before", result.get("canonical_before")), ("canonical_after", result.get("canonical_after"))]


_DECLARES_NAMESPACE = re.compile(r' xmlns(:|=)')


def _written_names(text: str) -> list[tuple[str, list[str]]] | None:
    """Element tags in document order with their sorted non-xmlns attribute names, as written; None when expat rejects it."""

    names: list[tuple[str, list[str]]] = []
    parser = xml.parsers.expat.ParserCreate()

    def start(name: str, attributes: dict[str, str]) -> None:
        names.append((name, sorted(key for key in attributes if key != "xmlns" and not key.startswith("xmlns:"))))

    parser.StartElementHandler = start
    try:
        parser.Parse(text, True)
    except xml.parsers.expat.ExpatError:
        return None
    return names


def _source_text(path: Path) -> str:
    """The source as the dumps read it (UTF-8 with replacement), less the leading BOMs canonicalise_xml strips."""

    return path.read_bytes().decode("utf-8", "replace").lstrip("\ufeff")


def _sources(surface: str, record: dict[str, object], root: str | None) -> dict[str, Path]:
    if surface == "canon":
        return {"canonical": Path(root or ".") / str(record.get("path"))}
    before, _, after = str(record.get("pair")).partition("\t")
    return {"canonical_before": Path(before), "canonical_after": Path(after)}


def check_source_prefixes(surface: str, python_records: list[dict[str, object]], port_records: list[dict[str, object]], root: str | None) -> int:
    failures = 0
    checked = 0
    for index, (python_record, port_record) in enumerate(zip(python_records, port_records)):
        python_texts = dict(_texts(surface, python_record))
        port_texts = dict(_texts(surface, port_record))
        if not any(isinstance(text, str) and _DECLARES_NAMESPACE.search(text) for text in (*python_texts.values(), *port_texts.values())):
            continue
        label = python_record.get("path") or python_record.get("pair") or str(index)
        sources = _sources(surface, port_record, root)
        for field, port_text in port_texts.items():
            python_text = python_texts.get(field)
            if not isinstance(port_text, str) or not isinstance(python_text, str):
                continue
            written = _written_names(_strip_prolog_keep_doctype(port_text))
            if written is None:
                if _written_names(_strip_prolog_keep_doctype(python_text)) is not None:
                    failures += 1
                    print(f"source prefixes: {label} {field}: the port text does not parse where python's does")
                continue
            source = _written_names(_strip_prolog_keep_doctype(_source_text(sources[field])))
            if source is None:
                continue
            checked += 1
            if written != source:
                failures += 1
                print(f"source prefixes differ: {label} {field}: source {str(source)[:300]} port {str(written)[:300]}")
    print(f"source prefixes: {checked} namespaced port canonical texts use the names written in the source, {failures} different")
    return failures


def _strip_prolog_keep_doctype(text: str) -> str:
    """The text less leading whitespace and an XML declaration, as canonicalise_xml removes them before parsing (expat rejects a
    declaration after whitespace and an upper-case one); a DOCTYPE stays, so its attribute defaults apply to both texts."""

    working = text.lstrip()
    declaration = _DECLARATION.match(working)
    return working[declaration.end():].lstrip() if declaration else working


def main(surface: str, python_path: str, port_path: str, root: str | None = None) -> int:
    with open(python_path, encoding="utf-8") as handle:
        python_records = [json.loads(line) for line in handle if line.strip()]
    with open(port_path, encoding="utf-8") as handle:
        port_records = [json.loads(line) for line in handle if line.strip()]
    failures = 0
    checked = 0
    for index, (python_record, port_record) in enumerate(zip(python_records, port_records)):
        label = python_record.get("path") or python_record.get("pair") or str(index)
        python_texts = dict(_texts(surface, python_record))
        for field, port_text in _texts(surface, port_record):
            python_text = python_texts.get(field)
            if not isinstance(python_text, str) or not isinstance(port_text, str):
                continue
            python_tree = _tree(python_text)
            port_tree = _tree(port_text)
            if python_tree[0] == "unparsable" and port_tree[0] == "unparsable":
                continue
            checked += 1
            if python_tree != port_tree:
                failures += 1
                print(f"xml semantics differ: {label} {field}: python {str(python_tree)[:300]} port {str(port_tree)[:300]}")
    print(f"xml semantics: {checked} canonical texts compared by expanded name, {failures} different")
    failures += check_source_prefixes(surface, python_records, port_records, root)
    return 1 if failures else 0


if __name__ == "__main__":
    arguments = sys.argv[1:]
    root_argument = None
    if len(arguments) == 5 and arguments[3] == "--root":
        root_argument = arguments[4]
        arguments = arguments[:3]
    if len(arguments) != 3 or arguments[0] not in ("diff", "canon"):
        raise SystemExit(__doc__)
    raise SystemExit(main(*arguments, root=root_argument))
