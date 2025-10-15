"""Format plugins package."""

from .registry import (
    FormatPlugin,
    get_plugins,
    plugin_versions,
    register,
    registry_summary,
)

# Ensure built-in plugins register on import.
from . import xml  # noqa: F401
from . import json as _json_plugin  # noqa: F401
from . import ini as _ini_plugin  # noqa: F401

__all__ = [
    "FormatPlugin",
    "get_plugins",
    "plugin_versions",
    "register",
    "registry_summary",
]
