from __future__ import annotations

import datetime as dt
import os
from pathlib import Path

from scripts import purge_reporting_retention as purge_mod


def _set_mtime(path: Path, *, days_ago: int) -> None:
    now = dt.datetime.now().timestamp()
    mtime = now - days_ago * 86400
    os.utime(path, times=(mtime, mtime))


def test_discover_candidates_filters_by_retention(tmp_path: Path) -> None:
    root = tmp_path / "captures"
    root.mkdir()
    old_dir = root / "2024-10-10"
    old_dir.mkdir()
    new_dir = root / "2025-11-01"
    new_dir.mkdir()
    _set_mtime(old_dir, days_ago=60)
    _set_mtime(new_dir, days_ago=5)

    now = dt.datetime(2025, 11, 13, tzinfo=purge_mod.UTC)
    candidates = purge_mod.discover_candidates(
        [root], retention_days=30, now=now
    )

    assert [c.path for c in candidates] == [old_dir]
    assert candidates[0].age_days >= 59


def test_discover_candidates_ignores_missing_paths(tmp_path: Path) -> None:
    missing = tmp_path / "missing"
    existing = tmp_path / "artifacts"
    existing.mkdir()
    stale_file = existing / "report.txt"
    stale_file.write_text("placeholder")
    _set_mtime(stale_file, days_ago=45)

    now = dt.datetime(2025, 11, 13, tzinfo=purge_mod.UTC)
    candidates = purge_mod.discover_candidates(
        [missing, existing], retention_days=30, now=now
    )

    assert [c.path for c in candidates] == [stale_file]


def test_purge_deletes_when_confirmed(tmp_path: Path) -> None:
    target_dir = tmp_path / "to_purge"
    nested_file = target_dir / "evidence.json"
    target_dir.mkdir()
    nested_file.write_text("{}")
    _set_mtime(target_dir, days_ago=40)

    candidate = purge_mod.PurgeCandidate(path=target_dir, age_days=40)
    deleted = purge_mod.purge([candidate], confirm=True)

    assert deleted == [target_dir]
    assert not target_dir.exists()


def test_purge_does_not_delete_when_not_confirmed(tmp_path: Path) -> None:
    keep_dir = tmp_path / "keep"
    keep_dir.mkdir()
    _set_mtime(keep_dir, days_ago=50)

    candidate = purge_mod.PurgeCandidate(path=keep_dir, age_days=50)
    purge_mod.purge([candidate], confirm=False)

    assert keep_dir.exists()
