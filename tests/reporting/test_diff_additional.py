from __future__ import annotations

from textwrap import dedent

import pytest

from driftbuster.reporting.diff import (
    DiffResult,
    build_unified_diff,
    canonicalise_text,
    canonicalise_xml,
)
from driftbuster.reporting.redaction import RedactionFilter


class DummyRedactor(RedactionFilter):
    def apply(self, text: str) -> str:
        return text.replace("secret", "[MASK]")


def test_canonicalise_xml_handles_doctype_fallback() -> None:
    payload = "<!DOCTYPE broken ["  # malformed
    assert canonicalise_xml(payload) == canonicalise_text(payload)


def test_canonicalise_helpers_handle_empty_payloads() -> None:
    assert canonicalise_text("") == ""
    assert canonicalise_xml("") == ""


def test_build_unified_diff_applies_redaction_and_stats() -> None:
    before = "secret=1\nvalue=2\n"
    after = "secret=3\nvalue=2\n"
    result = build_unified_diff(
        before,
        after,
        content_type="text",
        redactor=DummyRedactor(),
        context_lines=1,
    )
    assert isinstance(result, DiffResult)
    assert "[MASK]" in result.diff
    assert result.stats["added_lines"] >= 0


def test_build_unified_diff_rejects_unknown_content_type() -> None:
    with pytest.raises(ValueError):
        build_unified_diff("a", "b", content_type="unknown")


def test_calculate_stats_counts_insertions() -> None:
    before = "line\n"
    after = "line\nextra\n"
    result = build_unified_diff(before, after)
    assert result.stats["added_lines"] >= 1


def test_calculate_stats_counts_deletions() -> None:
    before = "one\n two\n three\n"
    after = "one\n three\n"
    result = build_unified_diff(before, after)
    # Ensure the deletion branch is exercised
    assert result.stats["removed_lines"] >= 1
