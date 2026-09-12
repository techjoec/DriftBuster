"""Format plugins package."""

from . import binary as _binary_plugin  # noqa: F401  # imported for plugin registration side effect
from . import conf as _conf_plugin  # noqa: F401  # imported for plugin registration side effect
from . import dockerfile as _dockerfile_plugin  # noqa: F401  # imported for plugin registration side effect
from . import format_registry as registry
from . import hcl as _hcl_plugin  # noqa: F401  # imported for plugin registration side effect
from . import ini as _ini_plugin  # noqa: F401  # imported for plugin registration side effect
from . import json as _json_plugin  # noqa: F401  # imported for plugin registration side effect
from . import registry_live as _registry_live  # noqa: F401  # imported for plugin registration side effect
from . import text as _text_plugin  # noqa: F401  # imported for plugin registration side effect
from . import toml as _toml_plugin  # noqa: F401  # imported for plugin registration side effect

# Ensure built-in plugins register on import.
from . import xml  # noqa: F401  # imported for plugin registration side effect
from . import yaml as _yaml_plugin  # noqa: F401  # imported for plugin registration side effect
from .format_registry import (
    FormatPlugin,
    get_plugins,
    plugin_versions,
    register,
    registry_summary,
)

__all__ = [
    "FormatPlugin",
    "get_plugins",
    "plugin_versions",
    "register",
    "registry",
    "registry_summary",
]
