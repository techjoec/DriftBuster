from __future__ import annotations

from collections.abc import Sequence

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
from .scheduler import (
    ProfileScheduler,
    ScheduledRun,
    ScheduleError,
    ScheduleSpec,
    ScheduleWindow,
    parse_interval,
)


def main(argv: Sequence[str] | None = None) -> int:
    from .run_profiles_cli import main as _main

    return _main(argv)


__all__ = [
    "ProfileFile",
    "ProfileRunResult",
    "ProfileScheduler",
    "RunProfile",
    "ScheduleError",
    "ScheduleSpec",
    "ScheduleWindow",
    "ScheduledRun",
    "execute_profile",
    "list_profiles",
    "load_profile",
    "main",
    "parse_interval",
    "profile_directory",
    "profiles_root",
    "save_profile",
]


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
