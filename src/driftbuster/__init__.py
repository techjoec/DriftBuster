"""DriftBuster core package.

This package currently exposes the modular detection core along with
the XML-focused format plugin implementation. The API is intentionally
small while we build out the foundation described in the DEV PLAN.
"""

from __future__ import annotations

import importlib
from typing import TYPE_CHECKING

from .core import (
    AppliedProfileConfig,
    ConfigurationProfile,
    DetectionMatch,
    Detector,
    ProfileConfig,
    ProfileStore,
    ProfiledDetection,
    diff_summary_snapshots,
    normalize_tags,
    scan_file,
    scan_path,
)
from .formats import FormatPlugin, get_plugins, register, registry_summary
from .hunt import HuntHit, HuntRule, default_rules, hunt_path
from .token_approvals import (
    TokenApproval,
    TokenApprovalStore,
    TokenCandidate,
    TokenCandidateSet,
    collect_token_candidates,
)

if TYPE_CHECKING:  # pragma: no cover - import only for type hints
    from . import offline_runner as offline_runner_module

__all__ = [
    "AppliedProfileConfig",
    "ConfigurationProfile",
    "Detector",
    "ProfileConfig",
    "ProfileStore",
    "ProfiledDetection",
    "DetectionMatch",
    "HuntHit",
    "HuntRule",
    "diff_summary_snapshots",
    "FormatPlugin",
    "get_plugins",
    "register",
    "registry_summary",
    "normalize_tags",
    "default_rules",
    "hunt_path",
    "scan_file",
    "scan_path",
    "offline_runner",
    "TokenApproval",
    "TokenApprovalStore",
    "TokenCandidate",
    "TokenCandidateSet",
    "collect_token_candidates",
]


def __getattr__(name: str):
    """Lazily import optional modules to avoid heavy dependencies at import."""

    if name == "offline_runner":
        module = importlib.import_module(".offline_runner", __name__)
        globals()[name] = module
        return module
    raise AttributeError(f"module '{__name__}' has no attribute '{name}'")
