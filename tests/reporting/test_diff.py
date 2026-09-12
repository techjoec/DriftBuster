import pytest

from driftbuster.reporting.diff import (
    DiffResult,
    build_unified_diff,
    canonicalise_text,
    canonicalise_xml,
    diff_summary_to_payload,
    summarise_diff_results,
)
from driftbuster.reporting.redaction import RedactionFilter
from typed_payloads import as_dict


class _MaskingRedactor(RedactionFilter):
    def apply(self, text: str) -> str:
        return text.replace("secret", "[MASK]")


def test_canonicalise_helpers_handle_empty_payloads() -> None:
    assert canonicalise_text("") == ""
    assert canonicalise_xml("") == ""


def test_build_unified_diff_applies_custom_redactor() -> None:
    result = build_unified_diff(
        "secret=1\nvalue=2\n",
        "secret=3\nvalue=2\n",
        content_type="text",
        redactor=_MaskingRedactor(),
        context_lines=1,
    )
    assert isinstance(result, DiffResult)
    assert "[MASK]" in result.diff
    assert "secret" not in result.diff


def test_build_unified_diff_rejects_unknown_content_type() -> None:
    with pytest.raises(ValueError):
        build_unified_diff("a", "b", content_type="unknown")


def test_build_unified_diff_stats_count_insertions_and_deletions() -> None:
    inserted = build_unified_diff("line\n", "line\nextra\n")
    assert inserted.stats["added_lines"] == 1
    assert inserted.stats["removed_lines"] == 0

    removed = build_unified_diff("one\n two\n three\n", "one\n three\n")
    assert removed.stats["removed_lines"] == 1
    assert removed.stats["added_lines"] == 0


def test_summarise_diff_results_combines_multiple() -> None:
    first = build_unified_diff("alpha", "beta", from_label="baseline", to_label="candidate")
    second = build_unified_diff(
        "line1\n", "line1\nline2\n", from_label="left", to_label="right", content_type="text"
    )

    summary = summarise_diff_results(
        (first, second),
        versions=("baseline", "candidate"),
        baseline_names=("baseline.cfg", "left.cfg"),
        comparison_names=("candidate.cfg", "right.cfg"),
    )

    assert summary.comparison_count == 2
    assert summary.comparisons[0].metadata.baseline_name == "baseline.cfg"
    assert summary.comparisons[1].summary.added_lines == 1

    payload = as_dict(diff_summary_to_payload(summary))
    assert payload["comparison_count"] == 2
    assert payload["comparisons"][1]["metadata"]["comparison_name"] == "right.cfg"


def test_summarise_diff_results_validates_lengths() -> None:
    result = build_unified_diff("one", "two")

    with pytest.raises(ValueError):
        summarise_diff_results((result,), baseline_names=("only", "extra"))

    with pytest.raises(ValueError):
        summarise_diff_results((result,), comparison_names=("only", "extra"))

    with pytest.raises(ValueError):
        summarise_diff_results(())
