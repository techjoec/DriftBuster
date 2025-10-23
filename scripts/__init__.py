"""Utility scripts package for local tools and test imports.

This package exposes key helper modules as attributes so tests can import with:

    from scripts import release_build, sync_versions
"""

from . import offline_compliance_audit as offline_compliance_audit  # re-export for tests
from . import release_build as release_build  # re-export for tests
from . import sync_versions as sync_versions  # re-export for tests

__all__ = [
    "offline_compliance_audit",
    "release_build",
    "sync_versions",
]
