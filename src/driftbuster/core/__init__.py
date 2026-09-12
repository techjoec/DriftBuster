"""DriftBuster core module exports."""

from .detector import Detector, scan_file, scan_path
from .profiles import (
    AppliedProfileConfig,
    ConfigurationProfile,
    ProfileConfig,
    ProfiledDetection,
    ProfileStore,
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
    "DetectionMatch",
    "Detector",
    "MetadataValidationError",
    "ProfileConfig",
    "ProfileStore",
    "ProfiledDetection",
    "diff_summary_snapshots",
    "normalize_tags",
    "scan_file",
    "scan_path",
    "summarise_metadata",
    "validate_detection_metadata",
]
