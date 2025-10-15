from __future__ import annotations

import pytest

from driftbuster.reporting.diff import (
    DiffResult,
    build_unified_diff,
    canonicalise_text,
    canonicalise_xml,
    render_unified_diff,
)


def test_canonicalise_text_normalises_newlines() -> None:
    payload = "line1\r\nline2 \r\n"
    normalised = canonicalise_text(payload)
    assert normalised.splitlines() == ["line1", "line2"]


def test_canonicalise_xml_preserves_prolog_and_handles_doctype() -> None:
    payload = (
        "<?xml version=\"1.0\"?>\n"
        "<!DOCTYPE note [<!ELEMENT note (to,from,heading,body)>]>\n"
        "<note attr=' value '>\n  <to>SECRET</to>\n</note>"
    )
    canonical = canonicalise_xml(payload)
    assert canonical.startswith("<?xml")
    assert "<!DOCTYPE" in canonical
    assert "SECRET" in canonical


def test_canonicalise_xml_falls_back_on_parse_error() -> None:
    malformed = "<root><unclosed></root>"
    assert canonicalise_xml(malformed) == canonicalise_text(malformed)


def test_build_unified_diff_applies_redaction() -> None:
    result = build_unified_diff(
        "token = SECRET\n",
        "token = SECRET\nextra = 1\n",
        mask_tokens=("SECRET",),
    )
    assert isinstance(result, DiffResult)
    assert "[REDACTED]" in result.diff
    assert result.stats["added_lines"] == 1


def test_render_unified_diff_and_errors() -> None:
    diff_text = render_unified_diff("a", "b", content_type="text")
    assert "-a" in diff_text and "+b" in diff_text

    with pytest.raises(ValueError):
        build_unified_diff("a", "b", content_type="unknown")
