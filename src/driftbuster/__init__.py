"""DriftBuster core package.

This package currently exposes the modular detection core along with
the XML-focused format plugin implementation. The API is intentionally
small while we build out the foundation described in the DEV PLAN.
"""

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
from . import offline_runner
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
]
