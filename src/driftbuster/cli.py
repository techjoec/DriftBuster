"""Lightweight command-line entry point for manual DriftBuster scans.

The long-term roadmap includes a richer CLI (see ``docs/python-cli-plan.md``),
but manual validation tasks already reference ``python -m driftbuster.cli``.
This module provides the minimal plumbing required for those checklists:

* Parse a target path plus a subset of the planned arguments.
* Invoke the detector so ``structured-config-xml`` variants can be reviewed.
* Render either a compact table or JSON lines for note-taking.

Automation, HTML output, and diff helpers intentionally remain out of scope so
the CLI stays aligned with the current HOLD constraints.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Iterable, Optional, Sequence, Tuple

from .core.detector import Detector
from .core.types import DetectionMatch


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="driftbuster", description="Scan files for DriftBuster formats"
    )
    parser.add_argument(
        "path",
        type=Path,
        help="File or directory to scan.",
    )
    parser.add_argument(
        "--glob",
        default="**/*",
        help="Glob used when scanning directories (default: **/*).",
    )
    parser.add_argument(
        "--sample-size",
        type=int,
        default=None,
        help="Bytes to sample from each file (defaults to detector setting).",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Emit JSON lines instead of a table.",
    )
    return parser


def _relative_path(root: Path, path: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.as_posix()


def _iter_scan_results(
    root: Path, glob: str, sample_size: Optional[int]
) -> Iterable[Tuple[Path, Optional[DetectionMatch]]]:
    detector = Detector(sample_size=sample_size)
    if root.is_file():
        yield root, detector.scan_file(root)
        return
    if not root.exists():
        raise FileNotFoundError(f"Path does not exist: {root}")
    for path, match in detector.scan_path(root, glob=glob):
        yield path, match


def _ellipsize(value: str, limit: int) -> str:
    if len(value) <= limit:
        return value
    if limit <= 1:
        return value[:limit]
    return f"{value[: limit - 1]}…"


def _emit_table(
    root: Path, results: Sequence[Tuple[Path, Optional[DetectionMatch]]]
) -> None:
    columns = ("Path", "Format", "Variant", "Confidence", "Metadata keys")
    widths = [max(len(col), 4) for col in columns]

    formatted_rows = []
    for path, match in results:
        relative = _relative_path(root, path)
        format_name = match.format_name if match else "—"
        variant = match.variant if match and match.variant else "—"
        confidence = f"{match.confidence:.2f}" if match else "—"
        metadata_keys = "—"
        if match and match.metadata:
            metadata_keys = ", ".join(sorted(match.metadata.keys()))
        formatted_rows.append(
            (relative, format_name, variant, confidence, metadata_keys)
        )
        for index, value in enumerate(formatted_rows[-1]):
            widths[index] = max(widths[index], len(value))

    max_metadata_width = 48
    if widths[-1] > max_metadata_width:
        widths[-1] = max_metadata_width

    header = "  ".join(
        column.ljust(widths[index]) for index, column in enumerate(columns)
    )
    print(header)
    print("  ".join("-" * width for width in widths))
    for row in formatted_rows:
        formatted = []
        for index, value in enumerate(row):
            column_width = widths[index]
            adjusted = _ellipsize(value, column_width)
            formatted.append(adjusted.ljust(column_width))
        print("  ".join(formatted))


def _emit_json(
    root: Path, results: Sequence[Tuple[Path, Optional[DetectionMatch]]]
) -> None:
    for path, match in results:
        payload = {
            "path": _relative_path(root, path),
            "detected": bool(match),
        }
        if match:
            payload.update(
                {
                    "format": match.format_name,
                    "variant": match.variant,
                    "confidence": match.confidence,
                    "metadata": match.metadata,
                }
            )
        print(json.dumps(payload, sort_keys=True))


def main(argv: Sequence[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)

    root: Path = args.path
    try:
        results = list(_iter_scan_results(root, args.glob, args.sample_size))
    except FileNotFoundError as exc:
        parser.error(str(exc))
        return 2

    if args.json:
        _emit_json(root, results)
    else:
        _emit_table(root, results)
    return 0


if __name__ == "__main__":  # pragma: no cover - CLI entry
    sys.exit(main())
