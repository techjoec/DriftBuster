"""Generate anonymised binary fixture samples for regression tests."""

from __future__ import annotations

import argparse
import hashlib
import json
import plistlib
import sqlite3
from pathlib import Path
from typing import Iterable


ROOT = Path(__file__).resolve().parents[2]
FIXTURE_DIR = ROOT / "fixtures" / "binary"


def _hash_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _create_sqlite(path: Path) -> None:
    if path.exists():
        path.unlink()
    connection = sqlite3.connect(path)
    try:
        connection.execute(
            "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)"
        )
        connection.executemany(
            "INSERT INTO settings(key, value) VALUES (?, ?)",
            [
                ("feature_enabled", "true"),
                ("retention_hours", "72"),
                ("last_review", "2025-01-07T12:00:00Z"),
            ],
        )
        connection.commit()
    finally:
        connection.close()


def _create_plist(path: Path) -> None:
    payload = {
        "Environment": "staging",
        "FeatureFlags": {"BinaryMode": True, "SqliteSync": "v2"},
        "ReviewedAt": "2025-01-05T09:30:00Z",
    }
    with path.open("wb") as handle:
        plistlib.dump(payload, handle, fmt=plistlib.FMT_BINARY)


def _create_front_matter(path: Path) -> None:
    path.write_text(
        "---\n"
        "title: Hybrid Pipeline\n"
        "environment: staging\n"
        "retention_hours: 72\n"
        "---\n"
        "The binary payload is stored alongside this record for audit purposes.\n",
        encoding="utf-8",
    )


def _write_manifest(fixtures: Iterable[Path]) -> None:
    entries = []
    for fixture in fixtures:
        entries.append(
            {
                "name": fixture.name,
                "sha256": _hash_file(fixture),
                "size": fixture.stat().st_size,
                "notes": "Synthetic sample generated for binary adapter coverage",
            }
        )
    manifest = {"fixtures": entries}
    (FIXTURE_DIR / "MANIFEST.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()

    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)

    sqlite_path = FIXTURE_DIR / "settings.sqlite"
    plist_path = FIXTURE_DIR / "preferences.plist"
    front_matter_path = FIXTURE_DIR / "config_frontmatter.md"

    _create_sqlite(sqlite_path)
    _create_plist(plist_path)
    _create_front_matter(front_matter_path)
    _write_manifest((sqlite_path, plist_path, front_matter_path))

    print("Generated binary fixtures:")
    for path in (sqlite_path, plist_path, front_matter_path):
        print(f" - {path.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
