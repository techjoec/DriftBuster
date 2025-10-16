"""Windows Registry live scan utilities.

This package provides a backend-abstracted interface for enumerating installed
applications and scanning registry trees to locate settings by keyword or
pattern. It favours read-only access and graceful fallbacks on non‑Windows
platforms.
"""

from .scan import (
    is_windows,
    RegistryApp,
    RegistryHit,
    SearchSpec,
    enumerate_installed_apps,
    find_app_registry_roots,
    search_registry,
)

__all__ = [
    "is_windows",
    "RegistryApp",
    "RegistryHit",
    "SearchSpec",
    "enumerate_installed_apps",
    "find_app_registry_roots",
    "search_registry",
]

