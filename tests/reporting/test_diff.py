import pytest

from driftbuster.reporting.diff import (
    build_unified_diff,
    diff_summary_to_payload,
    summarise_diff_results,
)


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

    payload = diff_summary_to_payload(summary)
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
