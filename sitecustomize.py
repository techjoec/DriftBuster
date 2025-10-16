"""Test import guard to ensure local 'scripts' package is preferred.

Pytest collection from tests/scripts/ can inadvertently create a namespace
package named 'scripts' that shadows the real package at repo_root/scripts.
This hook preloads the local 'scripts' package into sys.modules so
`from scripts import release_build` resolves correctly.
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path


def _ensure_local_scripts_package() -> None:
    root = Path(__file__).parent
    pkg_dir = root / "scripts"
    init_py = pkg_dir / "__init__.py"
    if not init_py.is_file():
        return

    # If already a concrete module with file, leave it.
    existing = sys.modules.get("scripts")
    if existing is not None and getattr(existing, "__file__", None):
        return

    spec = importlib.util.spec_from_file_location(
        "scripts",
        str(init_py),
        submodule_search_locations=[str(pkg_dir)],
    )
    if spec is None or spec.loader is None:
        return
    module = importlib.util.module_from_spec(spec)
    sys.modules["scripts"] = module
    spec.loader.exec_module(module)


try:
    _ensure_local_scripts_package()
except Exception:
    # Defensive: never fail import startup due to this convenience hook.
    pass

