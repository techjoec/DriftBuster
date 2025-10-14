"""Planning helpers for future diff/patch generation.

The core module does not execute diffs yet, but downstream tooling already
expects helpers that mirror the future reporting contract. This module
documents that surface so the eventual implementation can slot in without
changing call sites while HOLD remains active.

Helpers
=======

- ``build_diff_plan``: validate inputs and capture kwargs for future diff
  execution via ``driftbuster.reporting.diff.build_unified_diff``.
- ``plan_to_kwargs``: convert a :class:`DiffPlan` into a dictionary.
  Manual scripts can hand the mapping to the reporting layer during
  validation runs.

Importing ``driftbuster.reporting`` is intentionally avoided so the core stays
decoupled from reporting dependencies until the diff runner lands.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Mapping, MutableMapping, Optional, Sequence

if TYPE_CHECKING:  # pragma: no cover
    from driftbuster.reporting.diff import DiffResult


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


def execute_diff_plan(plan: DiffPlan) -> "DiffResult":
    """Execute ``plan`` via the reporting diff builder."""

    from driftbuster.reporting.diff import build_unified_diff

    return build_unified_diff(**plan_to_kwargs(plan))


__all__ = [
    "DiffPlan",
    "build_diff_plan",
    "plan_to_kwargs",
    "execute_diff_plan",
]
