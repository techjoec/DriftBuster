#!/usr/bin/env python3
"""Manual capture helper coordinating detection, profiles, and hunt scans.

This script is intentionally lightweight so auditors can snapshot driftbuster
runs without wiring a full CLI yet. It emits two JSON artefacts:

``<capture-id>-snapshot.json``
    Full capture payload containing the capture metadata, redacted detection
    matches, optional profile summary, and hunt hits. Consumers expect the
    ``capture`` block to include ``id``, ``root``, ``captured_at`` (UTC ISO
    string), ``operator``, ``environment``, ``reason``, ``host``,
    ``placeholder``, and ``mask_token_count``.

``<capture-id>-manifest.json``
    Compact manifest for quick logging that records key metadata, duration
    metrics, aggregate counts, a profile summary, and redaction statistics.
    The manifest mirrors the capture metadata and advertises a
    ``schema_version`` field so downstream tooling can evolve alongside this
    module.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Mapping, MutableMapping, Sequence

from driftbuster.core import Detector, ProfileStore, ProfiledDetection
from driftbuster.core.profiles import AppliedProfileConfig, diff_summary_snapshots
from driftbuster.core.types import DetectionMatch, summarise_metadata
from driftbuster.hunt import HuntHit, default_rules, hunt_path
from driftbuster.profile_cli import _load_json as load_json_payload, _store_from_payload
from driftbuster.reporting.redaction import redact_data, resolve_redactor
from driftbuster.sql import build_sqlite_snapshot

CAPTURE_MANIFEST_SCHEMA_VERSION = "1.0"


def _ensure_mapping(data: Mapping[str, Any] | None) -> MutableMapping[str, Any]:
    return dict(data or {})


def _serialise_profile_config(binding: AppliedProfileConfig) -> Mapping[str, Any]:
    config = binding.config
    profile = binding.profile
    return {
        "profile": {
            "name": profile.name,
            "description": profile.description,
            "tags": sorted(profile.tags),
            "metadata": _ensure_mapping(profile.metadata),
        },
        "config": {
            "id": config.identifier,
            "path": config.path,
            "path_glob": config.path_glob,
            "application": config.application,
            "version": config.version,
            "branch": config.branch,
            "tags": sorted(config.tags),
            "expected_format": config.expected_format,
            "expected_variant": config.expected_variant,
            "metadata": _ensure_mapping(config.metadata),
        },
    }


def _relative_path(path: Path, root: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.name


def _serialise_detection(entry: ProfiledDetection, root: Path) -> Mapping[str, Any]:
    detection = entry.detection
    if detection is None:
        raise ValueError("Cannot serialise detection for paths without a match.")
    payload = dict(summarise_metadata(detection))
    payload.update(
        {
            "path": str(entry.path),
            "relative_path": _relative_path(entry.path, root),
            "profiles": tuple(_serialise_profile_config(binding) for binding in entry.profiles),
        }
    )
    return payload


def _serialise_plain_detection(path: Path, match: DetectionMatch, root: Path) -> Mapping[str, Any]:
    payload = dict(summarise_metadata(match))
    payload.update({
        "path": str(path),
        "relative_path": _relative_path(path, root),
        "profiles": tuple(),
    })
    return payload


def _serialise_hunt_hit(hit: HuntHit, root: Path) -> Mapping[str, Any]:
    rule = hit.rule
    return {
        "rule": {
            "name": rule.name,
            "description": rule.description,
            "token_name": rule.token_name,
            "keywords": rule.keywords,
            "patterns": tuple(getattr(pattern, "pattern", pattern) for pattern in rule.patterns),
        },
        "path": str(hit.path),
        "relative_path": _relative_path(hit.path, root),
        "line_number": hit.line_number,
        "excerpt": hit.excerpt,
    }


def _normalise_summary(summary: Mapping[str, Any]) -> Mapping[str, Any]:
    def _normalise(value: Any) -> Any:
        if isinstance(value, Mapping):
            return {str(key): _normalise(val) for key, val in value.items()}
        if isinstance(value, (list, tuple, set)):
            return [_normalise(item) for item in value]
        return value

    return _normalise(dict(summary))


def _load_profile_store(path: Path) -> ProfileStore:
    payload = load_json_payload(path)
    return _store_from_payload(payload)


def _parse_column_arguments(values: Sequence[str] | None) -> dict[str, tuple[str, ...]]:
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


def _prepare_output_paths(directory: Path, capture_id: str) -> tuple[Path, Path]:
    directory.mkdir(parents=True, exist_ok=True)
    snapshot_path = directory / f"{capture_id}-snapshot.json"
    manifest_path = directory / f"{capture_id}-manifest.json"
    return snapshot_path, manifest_path


def _resolve_operator(value: str | None) -> str | None:
    candidates = (
        value,
        os.getenv("DRIFTBUSTER_CAPTURE_OPERATOR"),
        os.getenv("USER"),
        os.getenv("USERNAME"),
    )
    for candidate in candidates:
        candidate = (candidate or "").strip()
        if candidate:
            return candidate
    return None


def _build_snapshot_payload(
    *,
    capture_id: str,
    root: Path,
    operator: str,
    environment: str,
    reason: str,
    placeholder: str,
    mask_tokens: Sequence[str] | None,
    detections: Sequence[Mapping[str, Any]],
    profile_summary: Mapping[str, Any] | None,
    hunt_hits: Sequence[Mapping[str, Any]],
) -> dict[str, Any]:
    capture_block = {
        "id": capture_id,
        "root": str(root),
        "captured_at": datetime.now(timezone.utc).isoformat(),
        "operator": operator,
        "environment": environment,
        "reason": reason,
        "host": socket.gethostname(),
        "placeholder": placeholder,
        "mask_token_count": len(mask_tokens or ()),
    }
    return {
        "capture": capture_block,
        "detections": list(detections),
        "profile_summary": profile_summary,
        "hunt_hits": list(hunt_hits),
    }


def _build_manifest_payload(
    *,
    capture: Mapping[str, Any],
    snapshot_path: Path,
    manifest_path: Path,
    detection_duration: float,
    hunt_duration: float,
    total_duration: float,
    detection_count: int,
    profile_match_count: int,
    hunt_count: int,
    profile_summary: Mapping[str, Any] | None,
    placeholder: str,
    mask_token_count: int,
    total_redactions: int,
) -> dict[str, Any]:
    profile_summary = profile_summary or {}
    return {
        "schema_version": CAPTURE_MANIFEST_SCHEMA_VERSION,
        "capture": {
            "id": capture["id"],
            "snapshot_path": snapshot_path.name,
            "manifest_path": manifest_path.name,
            "captured_at": capture["captured_at"],
            "root": capture["root"],
            "operator": capture["operator"],
            "environment": capture["environment"],
            "reason": capture["reason"],
            "host": capture["host"],
        },
        "durations": {
            "detection_seconds": round(detection_duration, 3),
            "hunt_seconds": round(hunt_duration, 3),
            "total_seconds": round(total_duration, 3),
        },
        "counts": {
            "detections": detection_count,
            "profile_matches": profile_match_count,
            "hunt_hits": hunt_count,
        },
        "profile_summary": {
            "total_profiles": profile_summary.get("total_profiles", 0),
            "total_configs": profile_summary.get("total_configs", 0),
        },
        "redaction": {
            "placeholder": placeholder,
            "mask_token_count": mask_token_count,
            "total_redactions": total_redactions,
        },
    }


def _build_detector(args: argparse.Namespace) -> Detector:
    return Detector(sample_size=args.sample_size)


def run_capture(args: argparse.Namespace) -> int:
    root = Path(args.root).resolve()
    if not root.exists():
        sys.stderr.write(f"error: capture root does not exist: {root}\n")
        return 1

    if not args.mask_tokens and not args.allow_unmasked:
        sys.stderr.write(
            "error: provide at least one --mask-token or explicitly opt-in with --allow-unmasked\n"
        )
        return 1

    operator = _resolve_operator(args.operator)
    if operator is None:
        sys.stderr.write(
            "error: provide --operator or set DRIFTBUSTER_CAPTURE_OPERATOR/USER before running captures\n"
        )
        return 1

    environment = (args.environment or "").strip()
    if not environment:
        sys.stderr.write("error: --environment is required for capture manifests\n")
        return 1

    reason = (args.reason or "").strip()
    if not reason:
        sys.stderr.write("error: --reason is required for capture manifests\n")
        return 1

    profile_store: ProfileStore | None = None
    profile_summary: Mapping[str, Any] | None = None
    if args.profiles:
        try:
            profile_store = _load_profile_store(Path(args.profiles))
            profile_summary = _normalise_summary(profile_store.summary())
        except Exception as exc:  # pragma: no cover - manual script
            sys.stderr.write(f"error: failed to load profiles: {exc}\n")
            return 1

    detector = _build_detector(args)

    capture_id = args.capture_id or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    snapshot_path, manifest_path = _prepare_output_paths(Path(args.output_dir), capture_id)

    start_time = time.monotonic()
    detection_start = time.monotonic()

    if profile_store is not None:
        profiled_results = detector.scan_with_profiles(
            root,
            profile_store=profile_store,
            tags=args.profile_tags,
            glob=args.glob,
        )
        detections = [
            _serialise_detection(entry, root)
            for entry in profiled_results
            if entry.detection is not None
        ]
    else:
        raw_results = detector.scan_path(root, glob=args.glob)
        detections = [
            _serialise_plain_detection(path, match, root)
            for path, match in raw_results
            if match is not None
        ]

    detection_duration = time.monotonic() - detection_start

    hunt_hits: Sequence[Mapping[str, Any]] = []
    hunt_duration = 0.0
    if not args.skip_hunt:
        hunt_start = time.monotonic()
        hits = hunt_path(
            root,
            rules=default_rules(),
            glob=args.hunt_glob,
            sample_size=args.sample_size,
            exclude_patterns=args.hunt_exclude,
        )
        hunt_duration = time.monotonic() - hunt_start
        hunt_hits = [_serialise_hunt_hit(hit, root) for hit in hits]

    total_duration = time.monotonic() - start_time

    redactor = resolve_redactor(mask_tokens=args.mask_tokens, placeholder=args.placeholder)

    snapshot_payload = _build_snapshot_payload(
        capture_id=capture_id,
        root=root,
        operator=operator,
        environment=environment,
        reason=reason,
        placeholder=args.placeholder,
        mask_tokens=args.mask_tokens,
        detections=detections,
        profile_summary=profile_summary,
        hunt_hits=hunt_hits,
    )

    redacted_snapshot = redact_data(snapshot_payload, redactor) if redactor else snapshot_payload

    snapshot_path.write_text(json.dumps(redacted_snapshot, indent=2, sort_keys=True))

    detection_count = len(detections)
    profile_match_count = sum(len(entry.get("profiles", ())) for entry in detections)
    hunt_count = len(hunt_hits)

    total_redactions = 0
    if redactor:
        total_redactions = sum(redactor.stats().values())

    manifest_payload = _build_manifest_payload(
        capture=redacted_snapshot["capture"],
        snapshot_path=snapshot_path,
        manifest_path=manifest_path,
        detection_duration=detection_duration,
        hunt_duration=hunt_duration,
        total_duration=total_duration,
        detection_count=detection_count,
        profile_match_count=profile_match_count,
        hunt_count=hunt_count,
        profile_summary=profile_summary,
        placeholder=args.placeholder,
        mask_token_count=len(args.mask_tokens or ()),
        total_redactions=total_redactions,
    )

    manifest_path.write_text(json.dumps(manifest_payload, indent=2, sort_keys=True))

    sys.stdout.write(
        f"Snapshot written to {snapshot_path}\nManifest written to {manifest_path}\n"
    )

    if redactor and total_redactions == 0:
        sys.stderr.write("warning: redaction filter configured but no tokens were replaced\n")

    return 0


def _determine_snapshot_path(output_dir: Path, stem: str) -> Path:
    base = f"{stem}-sql-snapshot.json"
    candidate = output_dir / base
    counter = 1
    while candidate.exists():
        candidate = output_dir / f"{stem}-sql-snapshot-{counter}.json"
        counter += 1
    return candidate


def run_sql_export(args: argparse.Namespace) -> int:
    output_dir = Path(args.output_dir).expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    mask_map = _parse_column_arguments(args.mask_column)
    hash_map = _parse_column_arguments(args.hash_column)
    tables = tuple(args.table or ()) or None
    exclude_tables = tuple(args.exclude_table or ()) or None
    limit = args.limit
    placeholder = args.placeholder
    hash_salt = args.hash_salt or ""

    exports: list[Mapping[str, Any]] = []
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
                "masked_columns": {key: list(value) for key, value in mask_map.items()},
                "hashed_columns": {key: list(value) for key, value in hash_map.items()},
            }
        )

        sys.stdout.write(f"Exported SQL snapshot to {destination}\n")

    manifest_path = output_dir / "sql-manifest.json"
    manifest_payload = {
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
    manifest_path.write_text(json.dumps(manifest_payload, indent=2, sort_keys=True), encoding="utf-8")

    return exit_code


def _load_snapshot(path: Path) -> Mapping[str, Any]:
    try:
        return json.loads(path.read_text())
    except FileNotFoundError:
        raise
    except json.JSONDecodeError as exc:
        raise ValueError(f"Failed to parse snapshot {path}: {exc}") from exc


def _detection_key(entry: Mapping[str, Any]) -> tuple[str, str | None, str | None]:
    detection = entry.get("detection", {})
    return (
        entry.get("relative_path") or entry.get("path"),
        detection.get("format"),
        detection.get("variant"),
    )


def _detection_signature(entry: Mapping[str, Any]) -> str:
    detection = entry.get("detection", {})
    return json.dumps(detection, sort_keys=True)


def _hunt_token_summary(hits: Iterable[Mapping[str, Any]]) -> tuple[dict[str, int], int]:
    expected: dict[str, int] = {}
    unexpected = 0
    for hit in hits:
        rule = hit.get("rule", {})
        token = rule.get("token_name")
        if token:
            expected[token] = expected.get(token, 0) + 1
        else:
            unexpected += 1
    return expected, unexpected


def compare_snapshots(args: argparse.Namespace) -> int:
    baseline_path = Path(args.baseline)
    current_path = Path(args.current)

    if not current_path.exists():
        sys.stderr.write(f"error: current snapshot not found: {current_path}\n")
        return 1

    if not baseline_path.exists():
        sys.stdout.write(
            "No baseline snapshot found; record this run as the first capture.\n"
        )
        return 0

    try:
        baseline = _load_snapshot(baseline_path)
        current = _load_snapshot(current_path)
    except Exception as exc:  # pragma: no cover - manual script
        sys.stderr.write(f"error: {exc}\n")
        return 1

    baseline_detections = baseline.get("detections", [])
    current_detections = current.get("detections", [])

    baseline_map = { _detection_key(entry): entry for entry in baseline_detections }
    current_map = { _detection_key(entry): entry for entry in current_detections }

    added_keys = sorted(set(current_map) - set(baseline_map))
    removed_keys = sorted(set(baseline_map) - set(current_map))

    changed_keys = []
    for key in set(baseline_map) & set(current_map):
        if _detection_signature(baseline_map[key]) != _detection_signature(current_map[key]):
            changed_keys.append(key)
    changed_keys.sort()

    baseline_summary = baseline.get("profile_summary") or {}
    current_summary = current.get("profile_summary") or {}

    profile_diff = {}
    if baseline_summary and current_summary:
        profile_diff = dict(
            diff_summary_snapshots(baseline_summary, current_summary)  # type: ignore[arg-type]
        )

    baseline_expected, baseline_unexpected = _hunt_token_summary(baseline.get("hunt_hits", []))
    current_expected, current_unexpected = _hunt_token_summary(current.get("hunt_hits", []))

    sys.stdout.write("Snapshot comparison summary\n")
    sys.stdout.write("===========================\n")
    sys.stdout.write(
        f"Added detections: {len(added_keys)}\nRemoved detections: {len(removed_keys)}\n"
    )
    sys.stdout.write(f"Changed detections: {len(changed_keys)}\n")

    if profile_diff:
        added_profiles = profile_diff.get("added_profiles", [])
        removed_profiles = profile_diff.get("removed_profiles", [])
        changed_profiles = profile_diff.get("changed_profiles", [])
        sys.stdout.write("\nProfile summary diff:\n")
        sys.stdout.write(f"  Added profiles: {', '.join(added_profiles) or 'none'}\n")
        sys.stdout.write(f"  Removed profiles: {', '.join(removed_profiles) or 'none'}\n")
        sys.stdout.write(f"  Changed profiles: {len(changed_profiles)}\n")
    else:
        sys.stdout.write("\nProfile summary diff unavailable (missing summaries).\n")

    sys.stdout.write("\nDynamic token overview:\n")
    if current_expected:
        sys.stdout.write("  Expected tokens:\n")
        for token, count in sorted(current_expected.items()):
            baseline_count = baseline_expected.get(token, 0)
            delta = count - baseline_count
            sys.stdout.write(
                f"    {token}: {baseline_count} -> {count} (delta {delta:+d})\n"
            )
    else:
        sys.stdout.write("  Expected tokens: none\n")

    sys.stdout.write(
        f"  Unexpected hits: {baseline_unexpected} -> {current_unexpected}"
        f" (delta {current_unexpected - baseline_unexpected:+d})\n"
    )

    if added_keys:
        sys.stdout.write("\nAdded detection keys:\n")
        for key in added_keys:
            sys.stdout.write(f"  {key}\n")
    if removed_keys:
        sys.stdout.write("\nRemoved detection keys:\n")
        for key in removed_keys:
            sys.stdout.write(f"  {key}\n")
    if changed_keys:
        sys.stdout.write("\nChanged detection keys:\n")
        for key in changed_keys:
            sys.stdout.write(f"  {key}\n")

    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Manual capture helper for driftbuster runs.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    capture = subparsers.add_parser("run", help="Capture a snapshot and manifest.")
    capture.add_argument("root", nargs="?", default=".", help="Directory to scan.")
    capture.add_argument("--profiles", type=str, help="Path to ProfileStore JSON payload.")
    capture.add_argument("--profile-tag", dest="profile_tags", action="append", default=[], help="Optional profile tags to activate.")
    capture.add_argument("--glob", default="**/*", help="Glob used for scanning (defaults to **/*).")
    capture.add_argument("--hunt-glob", default="**/*", help="Glob pattern for hunt traversal.")
    capture.add_argument("--hunt-exclude", action="append", default=[], help="Glob patterns to skip during hunt traversal.")
    capture.add_argument("--skip-hunt", action="store_true", help="Skip hunt scan step.")
    capture.add_argument("--sample-size", type=int, default=128 * 1024, help="Sample size in bytes for detection and hunt scans.")
    capture.add_argument("--output-dir", default="captures", help="Directory to store snapshot + manifest.")
    capture.add_argument("--capture-id", help="Optional capture identifier (defaults to UTC timestamp).")
    capture.add_argument("--operator", help="Operator name recorded in manifest.")
    capture.add_argument("--environment", help="Environment label (prod/test/etc).")
    capture.add_argument("--reason", help="Reason for this capture run.")
    capture.add_argument("--mask-token", dest="mask_tokens", action="append", default=[], help="Sensitive token to redact (repeatable).")
    capture.add_argument("--placeholder", default="[REDACTED]", help="Placeholder string used for redaction.")
    capture.add_argument(
        "--allow-unmasked",
        action="store_true",
        help="Skip the redaction guard when no mask tokens are required.",
    )
    capture.set_defaults(func=run_capture)

    compare = subparsers.add_parser("compare", help="Compare two capture snapshots.")
    compare.add_argument("baseline", help="Baseline snapshot JSON path.")
    compare.add_argument("current", help="Current snapshot JSON path.")
    compare.set_defaults(func=compare_snapshots)

    sql_export = subparsers.add_parser(
        "export-sql",
        help="Export anonymised SQL snapshots for portable review.",
    )
    sql_export.add_argument("database", nargs="+", help="Path(s) to SQLite databases.")
    sql_export.add_argument(
        "--output-dir",
        default="sql-exports",
        help="Directory to store exported SQL snapshots.",
    )
    sql_export.add_argument(
        "--table",
        action="append",
        default=[],
        help="Restrict export to a specific table (repeatable).",
    )
    sql_export.add_argument(
        "--exclude-table",
        action="append",
        default=[],
        help="Exclude a specific table from export (repeatable).",
    )
    sql_export.add_argument(
        "--mask-column",
        dest="mask_column",
        action="append",
        default=[],
        help="Mask sensitive column data using placeholder (table.column).",
    )
    sql_export.add_argument(
        "--hash-column",
        dest="hash_column",
        action="append",
        default=[],
        help="Deterministically hash column data (table.column).",
    )
    sql_export.add_argument(
        "--placeholder",
        default="[REDACTED]",
        help="Placeholder used when masking columns.",
    )
    sql_export.add_argument(
        "--hash-salt",
        default="",
        help="Salt applied when hashing column data.",
    )
    sql_export.add_argument(
        "--limit",
        type=int,
        help="Optional maximum rows to export per table.",
    )
    sql_export.add_argument(
        "--prefix",
        default="",
        help="Optional prefix to apply to exported snapshot filenames.",
    )
    sql_export.set_defaults(func=run_sql_export)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        handler = args.func
    except AttributeError:
        parser.print_help()
        return 1
    return handler(args)


if __name__ == "__main__":  # pragma: no cover - manual helper
    raise SystemExit(main())
