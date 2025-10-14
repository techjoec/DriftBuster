"""Utility snippet to compare two ProfileStore payloads using summary diffs."""

from __future__ import annotations

import json
import sys
from pathlib import Path

from driftbuster.core import ProfileStore, diff_summary_snapshots


def load_store(path: str) -> ProfileStore:
    payload = json.loads(Path(path).read_text())
    return ProfileStore.from_dict(payload)


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: python profile-summary-diff.py <baseline.json> <current.json>")
        return 1

    baseline_store = load_store(sys.argv[1])
    current_store = load_store(sys.argv[2])

    diff = diff_summary_snapshots(baseline_store.summary(), current_store.summary())

    print("Baseline totals:", diff["totals"]["baseline"])
    print("Current totals:", diff["totals"]["current"])
    if diff["added_profiles"]:
        print("Added profiles:", ", ".join(diff["added_profiles"]))
    if diff["removed_profiles"]:
        print("Removed profiles:", ", ".join(diff["removed_profiles"]))
    for entry in diff["changed_profiles"]:
        print(
            f"{entry['name']}: +{', '.join(entry['added_config_ids']) or '—'} -{', '.join(entry['removed_config_ids']) or '—'}",
        )
    return 0


if __name__ == "__main__":  # pragma: no cover - manual utility
    raise SystemExit(main())
