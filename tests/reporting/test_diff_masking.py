from __future__ import annotations

from driftbuster.reporting.diff import build_unified_diff
from driftbuster.reporting.redaction import RedactionFilter


def test_build_unified_diff_records_custom_redactor_tokens_and_counts() -> None:
    redactor = RedactionFilter(tokens=("secret", "api-key"), placeholder="<mask>")

    result = build_unified_diff(
        "secret token\n",
        "secret token\napi-key=VALUE\n",
        redactor=redactor,
    )

    assert result.placeholder == "<mask>"
    assert result.mask_tokens == ("api-key", "secret")
    assert result.redaction_counts == {"secret": 2, "api-key": 1}
    assert "<mask> token" in result.diff
    assert "api-key" not in result.diff
    assert "secret" not in result.diff


def test_build_unified_diff_without_tokens_returns_empty_redaction_counts() -> None:
    result = build_unified_diff("line", "line")

    assert result.redaction_counts is None
    assert result.mask_tokens is None
