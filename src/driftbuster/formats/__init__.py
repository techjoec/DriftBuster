"""Format plugins package."""

from . import format_registry as registry
from .format_registry import (
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
from . import registry_live as _registry_live  # noqa: F401
from . import yaml as _yaml_plugin  # noqa: F401
from . import conf as _conf_plugin  # noqa: F401
from . import text as _text_plugin  # noqa: F401
from . import toml as _toml_plugin  # noqa: F401
from . import hcl as _hcl_plugin  # noqa: F401
from . import dockerfile as _dockerfile_plugin  # noqa: F401
from . import binary as _binary_plugin  # noqa: F401

__all__ = [
    "FormatPlugin",
    "get_plugins",
    "plugin_versions",
    "register",
    "registry_summary",
    "registry",
]
