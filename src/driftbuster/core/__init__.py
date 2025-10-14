"""DriftBuster core module exports."""

from .detector import Detector, scan_file, scan_path
from .profiles import (
    AppliedProfileConfig,
    ConfigurationProfile,
    ProfileConfig,
    ProfileStore,
    ProfiledDetection,
    diff_summary_snapshots,
    normalize_tags,
)
from .types import (
    DetectionMatch,
    MetadataValidationError,
    summarise_metadata,
    validate_detection_metadata,
)

__all__ = [
    "AppliedProfileConfig",
    "ConfigurationProfile",
    "Detector",
    "ProfileConfig",
    "ProfileStore",
    "ProfiledDetection",
    "DetectionMatch",
    "MetadataValidationError",
    "diff_summary_snapshots",
    "normalize_tags",
    "summarise_metadata",
    "validate_detection_metadata",
    "scan_file",
    "scan_path",
]
