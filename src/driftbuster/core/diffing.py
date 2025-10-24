"""Diff plan helpers bridging the reporting diff builder.

Helpers
=======

- ``build_diff_plan``: validate inputs and capture kwargs for diff execution
  via ``driftbuster.reporting.diff.build_unified_diff``.
- ``plan_to_kwargs``: convert a :class:`DiffPlan` into a dictionary so manual
  scripts can forward kwargs unchanged during rehearsal runs.
- ``execute_diff_plan``: execute the plan, optionally producing an execution
  summary compatible with ``driftbuster.reporting.diff.summarise_diff_result``.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Mapping, MutableMapping, Optional, Sequence
from typing import Literal, overload

if TYPE_CHECKING:  # pragma: no cover
    from driftbuster.reporting.diff import DiffResult, DiffResultSummary


@dataclass(frozen=True)
class DiffPlan:
    """Blueprint describing a future diff operation."""

    before: str
    after: str
    content_type: str = "text"
    from_label: str = "before"
    to_label: str = "after"
    label: Optional[str] = None
    mask_tokens: Optional[Sequence[str]] = None
    placeholder: str = "[REDACTED]"
    context_lines: int = 3


def build_diff_plan(
    before: str,
    after: str,
    *,
    content_type: str = "text",
    from_label: str = "before",
    to_label: str = "after",
    label: Optional[str] = None,
    mask_tokens: Optional[Sequence[str]] = None,
    placeholder: str = "[REDACTED]",
    context_lines: int = 3,
) -> DiffPlan:
    """Return a :class:`DiffPlan` placeholder for downstream diff execution."""

    if context_lines < 0:
        raise ValueError("context_lines must be non-negative")
    if not content_type:
        raise ValueError("content_type must be provided")
    return DiffPlan(
        before=before,
        after=after,
        content_type=content_type,
        from_label=from_label,
        to_label=to_label,
        label=label,
        mask_tokens=mask_tokens,
        placeholder=placeholder,
        context_lines=context_lines,
    )


def plan_to_kwargs(plan: DiffPlan) -> Mapping[str, object]:
    """Translate ``plan`` into kwargs for future diff execution.

    The return value mirrors
    ``driftbuster.reporting.diff.build_unified_diff`` so manual scripts can
    forward the mapping unchanged once the diff runner is available.
    """

    payload: MutableMapping[str, object] = {
        "before": plan.before,
        "after": plan.after,
        "content_type": plan.content_type,
        "from_label": plan.from_label,
        "to_label": plan.to_label,
        "label": plan.label,
        "mask_tokens": plan.mask_tokens,
        "placeholder": plan.placeholder,
        "context_lines": plan.context_lines,
    }
    return payload


@dataclass(frozen=True)
class DiffPlanExecution:
    """Executed diff with accompanying summary metadata."""

    plan: DiffPlan
    result: "DiffResult"
    summary: "DiffResultSummary"


@overload
def execute_diff_plan(
    plan: DiffPlan,
    *,
    summarise: Literal[False] = False,
    versions: Optional[Sequence[str]] = ...,
    baseline_name: Optional[str] = ...,
    comparison_name: Optional[str] = ...,
) -> "DiffResult":
    ...


@overload
def execute_diff_plan(
    plan: DiffPlan,
    *,
    summarise: Literal[True],
    versions: Optional[Sequence[str]] = ...,
    baseline_name: Optional[str] = ...,
    comparison_name: Optional[str] = ...,
) -> DiffPlanExecution:
    ...


def execute_diff_plan(
    plan: DiffPlan,
    *,
    summarise: bool = False,
    versions: Optional[Sequence[str]] = None,
    baseline_name: Optional[str] = None,
    comparison_name: Optional[str] = None,
):
    """Execute ``plan`` via the reporting diff builder.

    When ``summarise`` is ``True`` a :class:`DiffPlanExecution` containing the
    original plan, diff result, and summary metadata is returned. The summary
    parameters mirror ``driftbuster.reporting.diff.summarise_diff_result`` so
    rehearsal scripts stay aligned with adapter outputs.
    """

    from driftbuster.reporting.diff import build_unified_diff, summarise_diff_result

    result = build_unified_diff(**plan_to_kwargs(plan))
    if not summarise:
        return result
    summary = summarise_diff_result(
        result,
        versions=tuple(versions) if versions is not None else None,
        baseline_name=baseline_name,
        comparison_name=comparison_name,
    )
    return DiffPlanExecution(plan=plan, result=result, summary=summary)


__all__ = [
    "DiffPlan",
    "build_diff_plan",
    "plan_to_kwargs",
    "execute_diff_plan",
    "DiffPlanExecution",
]
