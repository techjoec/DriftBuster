"""Retention purge helper for reporting artefacts.

This script scans one or more directories and deletes entries older than the
configured retention window. By default it only reports what would be removed;
pass ``--confirm`` to actually delete the files/directories.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Iterator, List, Sequence

UTC = _dt.timezone.utc


@dataclass(frozen=True)
class PurgeCandidate:
    """Represents a path eligible for purging."""

    path: Path
    age_days: float


def _iter_root_entries(root: Path) -> Iterator[Path]:
    if root.is_dir():
        yield from root.iterdir()
    else:
        yield root


def discover_candidates(
    roots: Sequence[Path],
    *,
    retention_days: int,
    now: _dt.datetime | None = None,
) -> List[PurgeCandidate]:
    """Return purge candidates older than ``retention_days``."""

    if retention_days < 0:
        raise ValueError("retention_days must be non-negative")

    clock = now or _dt.datetime.now(tz=UTC)
    threshold = clock - _dt.timedelta(days=retention_days)
    candidates: List[PurgeCandidate] = []

    for root in roots:
        root = root.expanduser().resolve()
        if not root.exists():
            continue
        for entry in _iter_root_entries(root):
            try:
                stat = entry.stat()
            except FileNotFoundError:
                continue
            modified = _dt.datetime.fromtimestamp(stat.st_mtime, tz=UTC)
            if modified <= threshold:
                age = (clock - modified).total_seconds() / 86400
                candidates.append(PurgeCandidate(path=entry, age_days=age))
    return sorted(candidates, key=lambda c: c.path)


def purge(
    candidates: Iterable[PurgeCandidate],
    *,
    confirm: bool,
) -> List[Path]:
    """Remove ``candidates`` if ``confirm`` is True; return deleted paths."""

    deleted: List[Path] = []
    for candidate in candidates:
        path = candidate.path
        if not confirm:
            continue
        if path.is_dir():
            for root, dirs, files in os.walk(path, topdown=False):
                for name in files:
                    Path(root, name).unlink(missing_ok=True)
                for name in dirs:
                    Path(root, name).rmdir()
            path.rmdir()
        else:
            path.unlink(missing_ok=True)
        deleted.append(path)
    return deleted


def _format_candidate(candidate: PurgeCandidate) -> str:
    return f"{candidate.path} (age={candidate.age_days:.1f}d)"


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "paths",
        metavar="PATH",
        nargs="+",
        type=Path,
        help="Directories or files to evaluate for retention purge",
    )
    parser.add_argument(
        "--retention-days",
        type=int,
        default=30,
        help="Retention window in days (default: 30)",
    )
    parser.add_argument(
        "--confirm",
        action="store_true",
        help="Actually delete candidates; otherwise prints a dry-run report",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    candidates = discover_candidates(args.paths, retention_days=args.retention_days)
    if not candidates:
        print("No purge candidates found within retention policy.")
        return 0

    print("Candidates:")
    for candidate in candidates:
        print(" -", _format_candidate(candidate))

    if args.confirm:
        deleted = purge(candidates, confirm=True)
        print(f"Deleted {len(deleted)} item(s).")
    else:
        print("Dry run complete. Re-run with --confirm to delete candidates.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
