#!/usr/bin/env python3
"""Score detection coverage on configsamples for selected categories.

Usage:
  python -m scripts.score_configsamples [root]

Scans configsamples/library/by-format/{conf,yaml,text} and reports:
  - Per-plugin match counts and percentages
  - Summary of files with no detection (0% coverage items)
"""

from __future__ import annotations

import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Iterable, Optional

from driftbuster.core.detector import Detector


TARGET_FOLDERS = ("conf", "yaml", "text", "toml", "hcl", "dockerfile")


def iter_files(root: Path) -> Iterable[Path]:
    for folder in TARGET_FOLDERS:
        base = root / "configsamples" / "library" / "by-format" / folder
        if not base.is_dir():
            continue
        for p in base.rglob("*"):
            if not p.is_file():
                continue
            if p.name.lower() == "metadata.json":
                continue
            yield p


def main(argv: Optional[list[str]] = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    project_root = Path(argv[0]).resolve() if argv else Path(__file__).resolve().parents[1]

    files = list(iter_files(project_root))
    if not files:
        print("No files found to scan.")
        return 1

    detector = Detector()
    plugin_counts: Counter[str] = Counter()
    total = 0
    unmatched: list[Path] = []
    by_plugin_files: defaultdict[str, list[Path]] = defaultdict(list)

    for path in files:
        total += 1
        match = detector.scan_file(path)
        if match is None:
            unmatched.append(path)
            continue
        plugin_counts[match.plugin_name] += 1
        by_plugin_files[match.plugin_name].append(path)

    print("== Plugin Coverage ==")
    for name, count in plugin_counts.most_common():
        pct = 100.0 * count / max(total, 1)
        print(f"{name:20s} {count:4d} / {total:4d}  ({pct:5.1f}%)")

    if unmatched:
        print("\n== 0% Coverage Items ==")
        for path in sorted(unmatched):
            rel = path.relative_to(project_root)
            print(str(rel))
    else:
        print("\n== 0% Coverage Items ==\nNone — all sampled files matched.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
