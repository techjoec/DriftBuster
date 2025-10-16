from __future__ import annotations

import importlib.util
import sys
from pathlib import Path


def _force_local_scripts_package() -> None:
    root = Path(__file__).parent
    pkg_dir = root / "scripts"
    init_py = pkg_dir / "__init__.py"
    if not init_py.is_file():
        return
    # If already bound to our package file, nothing to do.
    existing = sys.modules.get("scripts")
    if existing is not None and getattr(existing, "__file__", None) == str(init_py):
        return
    spec = importlib.util.spec_from_file_location(
        "scripts",
        str(init_py),
        submodule_search_locations=[str(pkg_dir)],
    )
    if spec and spec.loader:
        module = importlib.util.module_from_spec(spec)
        sys.modules["scripts"] = module
        spec.loader.exec_module(module)


_force_local_scripts_package()

