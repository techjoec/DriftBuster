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
    ProfiledDetection,
    ProfileStore,
    diff_summary_snapshots,
    normalize_tags,
    scan_file,
    scan_path,
)
from .formats import FormatPlugin, get_plugins, register, registry_summary
from .hunt import HuntHit, HuntRule, default_rules, hunt_path

if TYPE_CHECKING:  # pragma: no cover - import only for type hints
    from . import offline_runner as offline_runner_module  # noqa: F401  # re-exported lazily via __getattr__

__all__ = [
    "AppliedProfileConfig",
    "ConfigurationProfile",
    "DetectionMatch",
    "Detector",
    "FormatPlugin",
    "HuntHit",
    "HuntRule",
    "ProfileConfig",
    "ProfileStore",
    "ProfiledDetection",
    "default_rules",
    "diff_summary_snapshots",
    "get_plugins",
    "hunt_path",
    "normalize_tags",
    "offline_runner",  # pyright: ignore[reportUnsupportedDunderAll]  # resolved lazily by __getattr__
    "register",
    "registry_summary",
    "scan_file",
    "scan_path",
]


def __getattr__(name: str):
    """Lazily import optional modules to avoid heavy dependencies at import."""

    if name == "offline_runner":
        module = importlib.import_module(".offline_runner", __name__)
        globals()[name] = module
        return module
    raise AttributeError(f"module '{__name__}' has no attribute '{name}'")
