from __future__ import annotations

from typing import Sequence

from .core.run_profiles import (
    ProfileFile,
    ProfileRunResult,
    RunProfile,
    execute_profile,
    list_profiles,
    load_profile,
    profile_directory,
    profiles_root,
    save_profile,
)


def main(argv: Sequence[str] | None = None) -> int:
    from .run_profiles_cli import main as _main

    return _main(argv)


__all__ = [
    "RunProfile",
    "ProfileFile",
    "ProfileRunResult",
    "profiles_root",
    "profile_directory",
    "load_profile",
    "save_profile",
    "list_profiles",
    "execute_profile",
    "main",
]


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
