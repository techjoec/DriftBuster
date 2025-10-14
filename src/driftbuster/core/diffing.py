"""Planning helpers for future diff/patch generation.

The core module does not execute diffs yet, but downstream tooling and
checklists already reference a dedicated helper that can translate detector
output into the kwargs consumed by ``driftbuster.reporting.diff``. This module
captures the expected interface so future implementation work has a concrete
target while HOLD remains in place.

Blueprint summary
=================

Two helpers define the intended contract:

``build_diff_plan(before, after, *, content_type="text", label=None, mask_tokens=None, context_lines=3, placeholder="[REDACTED]")``
    Validates the inputs and returns a :class:`DiffPlan` dataclass capturing the
    canonical kwargs for ``driftbuster.reporting.diff.build_unified_diff`` once
    the execution path is allowed to land.

``plan_to_kwargs(plan)``
    Converts a :class:`DiffPlan` instance into a shallow dictionary that mirrors
    the keyword arguments used by the reporting layer. Manual scripts can feed
    that mapping into ``build_unified_diff`` during validation runs without
    wiring the code path today.

The helpers intentionally avoid importing ``driftbuster.reporting`` so the core
module stays free of reporting dependencies until HOLD lifts.

Expected usage
==============

Manual verification scripts (see ``notes/snippets/xml-config-diffs.md``) should
instantiate a :class:`DiffPlan`, log the resulting mapping, and only execute the
diff via the reporting module during manual tests. Future work will replace the
placeholder ``NotImplementedError`` with concrete orchestration once the diff
design is approved.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Mapping, MutableMapping, Optional, Sequence


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

    The return value mirrors the signature of
    ``driftbuster.reporting.diff.build_unified_diff`` so manual scripts can pass
    it along without modification once the diff runner is available.
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


def execute_diff_plan(plan: DiffPlan) -> None:
    """Placeholder hook for the eventual diff execution pipeline."""

    raise NotImplementedError("Diff execution will land after HOLD lifts.")


__all__ = ["DiffPlan", "build_diff_plan", "plan_to_kwargs", "execute_diff_plan"]

