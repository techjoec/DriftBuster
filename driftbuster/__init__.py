"""Compatibility shim so ``python -m driftbuster`` works from a src layout."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

_PACKAGE_ROOT = Path(__file__).resolve().parent
_SRC_PACKAGE = _PACKAGE_ROOT.parent / "src" / "driftbuster"

if not _SRC_PACKAGE.exists():  # pragma: no cover - defensive guard
    raise ImportError(
        "Unable to locate src/driftbuster; editable layout is required for CLI runs."
    )

_spec = importlib.util.spec_from_file_location(
    "_driftbuster_src",
    _SRC_PACKAGE / "__init__.py",
    submodule_search_locations=[str(_SRC_PACKAGE)],
)
if _spec is None or _spec.loader is None:  # pragma: no cover - defensive guard
    raise ImportError("Failed to load driftbuster package from src layout")

_module = importlib.util.module_from_spec(_spec)
_sys_modules = sys.modules
_sys_modules.setdefault("_driftbuster_src", _module)
_spec.loader.exec_module(_module)

# Re-export public attributes from the real package.
for name, value in list(_module.__dict__.items()):
    if name.startswith("__") and name not in {"__all__", "__path__"}:
        continue
    globals()[name] = value

# Ensure imports like ``import driftbuster.core`` find the src directory.
__path__ = [str(_SRC_PACKAGE)]

