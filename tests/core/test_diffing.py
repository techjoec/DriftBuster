from __future__ import annotations

import pytest

from driftbuster.core.diffing import build_diff_plan, execute_diff_plan, plan_to_kwargs


def test_build_diff_plan_returns_expected_values() -> None:
    plan = build_diff_plan(
        "before",
        "after",
        content_type="xml",
        from_label="left",
        to_label="right",
        label="config",
        mask_tokens=("secret",),
        placeholder="***",
        context_lines=5,
    )

    assert plan.content_type == "xml"
    assert plan.from_label == "left"
    assert plan.to_label == "right"
    assert plan.label == "config"
    assert plan.mask_tokens == ("secret",)
    assert plan.context_lines == 5


def test_build_diff_plan_rejects_negative_context() -> None:
    with pytest.raises(ValueError):
        build_diff_plan("before", "after", context_lines=-1)


def test_plan_to_kwargs_reflects_plan() -> None:
    plan = build_diff_plan("a", "b")
    payload = plan_to_kwargs(plan)

    assert payload["before"] == "a"
    assert payload["after"] == "b"
    assert payload["context_lines"] == 3


def test_execute_diff_plan_not_implemented() -> None:
    plan = build_diff_plan("a", "b")
    with pytest.raises(NotImplementedError):
        execute_diff_plan(plan)
