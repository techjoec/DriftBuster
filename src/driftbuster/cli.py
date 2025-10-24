"""Lightweight command-line entry point for manual DriftBuster scans.

The long-term roadmap includes a richer CLI (see ``CLOUDTASKS.md`` area A18),
but manual validation tasks already reference ``python -m driftbuster.cli``.
This module provides the minimal plumbing required for those checklists:

* Parse a target path plus a subset of the planned arguments.
* Invoke the detector so ``structured-config-xml`` variants can be reviewed.
* Render either a compact table or JSON lines for note-taking.
* Generate unified diff/patch output for rapid comparison workflows.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Mapping, Optional, Sequence, Tuple

from .core.detector import Detector
from .core.types import DetectionMatch
from .reporting.diff import build_unified_diff
from .sql import build_sqlite_snapshot


_XML_SUFFIXES = {
    ".config",
    ".csproj",
    ".resx",
    ".targets",
    ".vbproj",
    ".xml",
    ".xaml",
    ".xslt",
}


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


def _build_diff_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="driftbuster diff",
        description="Generate unified diffs for configuration snapshots.",
    )
    parser.add_argument(
        "baseline",
        type=Path,
        help="Baseline file to diff against.",
    )
    parser.add_argument(
        "comparisons",
        nargs="+",
        type=Path,
        help="One or more files to compare with the baseline.",
    )
    parser.add_argument(
        "--content-type",
        choices=("auto", "text", "xml"),
        default="auto",
        help="Canonicalisation strategy applied before diffing (default: auto).",
    )
    parser.add_argument(
        "--context-lines",
        type=int,
        default=3,
        help="Context lines to include around each diff hunk (default: 3).",
    )
    parser.add_argument(
        "--mask-token",
        action="append",
        dest="mask_tokens",
        default=[],
        help="Token to redact before diffing (repeatable).",
    )
    parser.add_argument(
        "--placeholder",
        default="[REDACTED]",
        help="Placeholder shown for masked tokens (default: [REDACTED]).",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        help="Directory where unified diff patches will be written.",
    )
    return parser


def _build_export_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="driftbuster export-sql",
        description="Export anonymised SQL snapshots for portable review.",
    )
    parser.add_argument("database", nargs="+", help="Path(s) to SQLite databases.")
    parser.add_argument(
        "--output-dir",
        default="sql-exports",
        help="Directory to store exported SQL snapshots.",
    )
    parser.add_argument(
        "--table",
        action="append",
        default=[],
        help="Restrict export to a specific table (repeatable).",
    )
    parser.add_argument(
        "--exclude-table",
        action="append",
        default=[],
        help="Exclude a specific table from export (repeatable).",
    )
    parser.add_argument(
        "--mask-column",
        dest="mask_column",
        action="append",
        default=[],
        help="Mask sensitive column data using placeholder (table.column).",
    )
    parser.add_argument(
        "--hash-column",
        dest="hash_column",
        action="append",
        default=[],
        help="Deterministically hash column data (table.column).",
    )
    parser.add_argument(
        "--placeholder",
        default="[REDACTED]",
        help="Placeholder used when masking columns.",
    )
    parser.add_argument(
        "--hash-salt",
        default="",
        help="Salt applied when hashing column data.",
    )
    parser.add_argument(
        "--limit",
        type=int,
        help="Optional maximum rows to export per table.",
    )
    parser.add_argument(
        "--prefix",
        default="",
        help="Optional prefix to apply to exported snapshot filenames.",
    )
    parser.add_argument(
        "--manifest-name",
        default="sql-manifest.json",
        help="Filename for the manifest written to the output directory.",
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


def _guess_content_type(
    baseline: Path, candidate: Path, explicit: str = "auto"
) -> str:
    if explicit != "auto":
        return explicit

    if baseline.suffix.lower() in _XML_SUFFIXES or candidate.suffix.lower() in _XML_SUFFIXES:
        return "xml"
    return "text"


def _read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except OSError as exc:
        raise FileNotFoundError(f"Unable to read {path}: {exc}") from exc


def _ensure_file(path: Path, *, parser: argparse.ArgumentParser, role: str) -> Path:
    resolved = path.expanduser().resolve()
    if not resolved.exists():
        parser.error(f"{role} does not exist: {resolved}")
    if not resolved.is_file():
        parser.error(f"{role} must be a file: {resolved}")
    return resolved


def _build_patch_name(baseline: Path, candidate: Path) -> str:
    left = baseline.stem.replace(" ", "-") or "baseline"
    right = candidate.stem.replace(" ", "-") or "comparison"
    return f"{left}--{right}.patch"


def _emit_table(
    root: Path, results: Sequence[Tuple[Path, Optional[DetectionMatch]]]
) -> None:
    columns = (
        "Path",
        "Format",
        "Variant",
        "Confidence",
        "Severity",
        "Severity hint",
        "Metadata keys",
    )
    widths = [max(len(col), 4) for col in columns]

    formatted_rows = []
    for path, match in results:
        relative = _relative_path(root, path)
        format_name = match.format_name if match else "—"
        variant = match.variant if match and match.variant else "—"
        confidence = f"{match.confidence:.2f}" if match else "—"
        severity = "—"
        severity_hint = "—"
        metadata_keys = "—"
        if match and match.metadata:
            metadata = match.metadata
            metadata_keys = ", ".join(sorted(metadata.keys()))
            severity = str(metadata.get("catalog_severity") or "—")
            severity_hint = str(metadata.get("catalog_severity_hint") or "—")
        formatted_rows.append(
            (
                relative,
                format_name,
                variant,
                confidence,
                severity,
                severity_hint,
                metadata_keys,
            )
        )
        for index, value in enumerate(formatted_rows[-1]):
            widths[index] = max(widths[index], len(value))

    max_hint_width = 72
    hint_index = columns.index("Severity hint")
    if widths[hint_index] > max_hint_width:
        widths[hint_index] = max_hint_width

    max_metadata_width = 48
    metadata_index = columns.index("Metadata keys")
    if widths[metadata_index] > max_metadata_width:
        widths[metadata_index] = max_metadata_width

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
            metadata = match.metadata or {}
            payload.update(
                {
                    "format": match.format_name,
                    "variant": match.variant,
                    "confidence": match.confidence,
                    "severity": metadata.get("catalog_severity"),
                    "severity_hint": metadata.get("catalog_severity_hint"),
                    "metadata": metadata,
                }
            )
        print(json.dumps(payload, sort_keys=True))


def _parse_column_arguments(values: Sequence[str] | None) -> Mapping[str, tuple[str, ...]]:
    mapping: dict[str, list[str]] = {}
    for entry in values or ():
        if not entry or "." not in entry:
            continue
        table, column = entry.split(".", 1)
        table = table.strip()
        column = column.strip()
        if not table or not column:
            continue
        mapping.setdefault(table, []).append(column)
    return {key: tuple(value) for key, value in mapping.items()}


def _determine_snapshot_path(output_dir: Path, stem: str) -> Path:
    base = f"{stem}-sql-snapshot.json"
    candidate = output_dir / base
    counter = 1
    while candidate.exists():
        candidate = output_dir / f"{stem}-sql-snapshot-{counter}.json"
        counter += 1
    return candidate


def _run_export_sql(argv: Sequence[str]) -> int:
    parser = _build_export_parser()
    args = parser.parse_args(argv)

    output_dir = Path(args.output_dir).expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    mask_map = _parse_column_arguments(args.mask_column)
    hash_map = _parse_column_arguments(args.hash_column)
    tables = tuple(args.table or ()) or None
    exclude_tables = tuple(args.exclude_table or ()) or None
    limit = args.limit
    placeholder = args.placeholder
    hash_salt = args.hash_salt or ""

    exports: list[Mapping[str, object]] = []
    exit_code = 0

    for database in args.database:
        db_path = Path(database).expanduser().resolve()
        if not db_path.exists():
            sys.stderr.write(f"error: database not found: {db_path}\n")
            exit_code = 1
            continue

        stem = args.prefix or db_path.stem
        if len(args.database) > 1:
            stem = f"{stem}-{db_path.stem}" if args.prefix else db_path.stem

        if limit is not None and limit <= 0:
            sys.stderr.write("error: --limit must be positive when provided\n")
            exit_code = 1
            continue

        try:
            snapshot = build_sqlite_snapshot(
                db_path,
                tables=tables,
                exclude_tables=exclude_tables,
                mask_columns=mask_map,
                hash_columns=hash_map,
                limit=limit,
                placeholder=placeholder,
                hash_salt=hash_salt,
            )
        except Exception as exc:  # pragma: no cover - defensive guard
            sys.stderr.write(f"error: failed to export {db_path}: {exc}\n")
            exit_code = 1
            continue

        destination = _determine_snapshot_path(output_dir, stem)
        payload = snapshot.to_dict()
        destination.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")

        exports.append(
            {
                "source": str(db_path),
                "output": destination.name,
                "dialect": "sqlite",
                "tables": [table["name"] for table in payload.get("tables", [])],
                "row_counts": {
                    table["name"]: table["row_count"] for table in payload.get("tables", [])
                },
                "masked_columns": {
                    table: list(columns) for table, columns in mask_map.items()
                },
                "hashed_columns": {
                    table: list(columns) for table, columns in hash_map.items()
                },
            }
        )

        sys.stdout.write(f"Exported SQL snapshot to {destination}\n")

    manifest_payload: Mapping[str, object] = {
        "captured_at": datetime.now(timezone.utc).isoformat(),
        "exports": exports,
        "options": {
            "tables": list(tables or ()),
            "exclude_tables": list(exclude_tables or ()),
            "masked_columns": {key: list(value) for key, value in mask_map.items()},
            "hashed_columns": {key: list(value) for key, value in hash_map.items()},
            "limit": limit,
            "placeholder": placeholder,
            "hash_salt": hash_salt,
        },
    }
    manifest_path = output_dir / args.manifest_name
    manifest_path.write_text(json.dumps(manifest_payload, indent=2, sort_keys=True), encoding="utf-8")
    sys.stdout.write(f"Manifest written to {manifest_path}\n")

    return exit_code


def _run_diff(argv: Sequence[str]) -> int:
    parser = _build_diff_parser()
    args = parser.parse_args(argv)

    if args.context_lines < 0:
        parser.error("--context-lines must be zero or greater")

    baseline_path = _ensure_file(args.baseline, parser=parser, role="Baseline path")
    comparison_paths = [
        _ensure_file(path, parser=parser, role="Comparison path") for path in args.comparisons
    ]

    try:
        baseline_content = _read_text(baseline_path)
    except FileNotFoundError as exc:
        parser.error(str(exc))
        return 2

    output_dir: Path | None = None
    if args.output_dir is not None:
        output_dir = args.output_dir.expanduser().resolve()
        output_dir.mkdir(parents=True, exist_ok=True)

    exit_code = 0
    mask_tokens: tuple[str, ...] | None = None
    if args.mask_tokens:
        mask_tokens = tuple(token for token in args.mask_tokens if token)
        if not mask_tokens:
            mask_tokens = None

    for candidate_path in comparison_paths:
        try:
            candidate_content = _read_text(candidate_path)
        except FileNotFoundError as exc:
            sys.stderr.write(f"error: {exc}\n")
            exit_code = 1
            continue

        content_type = _guess_content_type(baseline_path, candidate_path, args.content_type)
        result = build_unified_diff(
            baseline_content,
            candidate_content,
            content_type=content_type,
            from_label=baseline_path.name,
            to_label=candidate_path.name,
            mask_tokens=mask_tokens,
            placeholder=args.placeholder,
            context_lines=args.context_lines,
        )

        diff_text = result.diff.strip("\n")
        stats = result.stats or {}
        added = int(stats.get("added_lines", 0))
        removed = int(stats.get("removed_lines", 0))
        changed = int(stats.get("changed_lines", 0))

        if output_dir:
            destination = output_dir / _build_patch_name(baseline_path, candidate_path)
            destination.write_text(result.diff + ("\n" if not result.diff.endswith("\n") else ""), encoding="utf-8")
            sys.stdout.write(
                f"Wrote diff for {candidate_path.name} to {destination}\n"
            )
        else:
            sys.stdout.write(
                f"=== {baseline_path.name} → {candidate_path.name} ({content_type}) ===\n"
            )
            if diff_text:
                sys.stdout.write(f"{result.diff}\n")
            else:
                sys.stdout.write("(no differences)\n")

        sys.stdout.write(
            f"Summary: added={added} removed={removed} changed={changed}\n"
        )

    return exit_code


def main(argv: Sequence[str] | None = None) -> int:
    if argv is None:
        argv = list(sys.argv[1:])
    else:
        argv = list(argv)
    if argv and argv[0] == "export-sql":
        return _run_export_sql(argv[1:])
    if argv and argv[0] == "diff":
        return _run_diff(argv[1:])

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


def console_main() -> None:
    """Entry point for ``driftbuster`` console script."""

    sys.exit(main())


def export_sql_console_main() -> None:
    """Entry point for ``driftbuster-export-sql`` console script."""

    argv = ["export-sql", *sys.argv[1:]]
    sys.exit(main(argv))
