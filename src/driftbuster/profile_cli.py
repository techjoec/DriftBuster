"""Manual CLI helper for profile summaries and diffs."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, Iterable, List, Mapping, MutableMapping, Optional, Sequence, Tuple

from .core.profiles import (
    ConfigurationProfile,
    ProfileConfig,
    ProfileStore,
    diff_summary_snapshots,
)


def _load_json(path: Path) -> Mapping[str, Any]:
    """Return JSON payload stored at ``path`` with friendly error handling."""

    try:
        text = path.read_text()
    except OSError as exc:  # pragma: no cover - manual CLI guard
        raise ValueError(f"Unable to read JSON payload from {path}: {exc}") from exc
    try:
        return json.loads(text)
    except json.JSONDecodeError as exc:  # pragma: no cover - defensive guard
        raise ValueError(f"Failed to parse JSON from {path}: {exc}") from exc


def _store_from_payload(payload: Mapping[str, Any]) -> ProfileStore:
    """Return a ProfileStore built from a dictionary payload."""

    builder = getattr(ProfileStore, "from_dict", None)
    if callable(builder):
        try:
            return builder(payload)  # type: ignore[arg-type]
        except Exception:  # pragma: no cover - fall back to manual build
            pass

    profiles: list[ConfigurationProfile] = []
    for entry in payload.get("profiles", []):
        if not isinstance(entry, Mapping):
            continue
        configs = []
        for cfg in entry.get("configs", []):
            if not isinstance(cfg, Mapping):
                continue
            configs.append(
                ProfileConfig(
                    identifier=str(cfg["id"]),
                    path=cfg.get("path"),
                    path_glob=cfg.get("path_glob"),
                    application=cfg.get("application"),
                    version=cfg.get("version"),
                    branch=cfg.get("branch"),
                    tags=cfg.get("tags"),
                    expected_format=cfg.get("expected_format"),
                    expected_variant=cfg.get("expected_variant"),
                    metadata=cfg.get("metadata", {}),
                )
            )
        profiles.append(
            ConfigurationProfile(
                name=str(entry["name"]),
                description=entry.get("description"),
                tags=entry.get("tags"),
                configs=tuple(configs),
                metadata=entry.get("metadata", {}),
            )
        )
    return ProfileStore(profiles)


def _normalise_payload(payload: Any) -> Any:
    """Convert mapping proxy/frozen containers to JSON-friendly structures."""

    if isinstance(payload, Mapping):
        return {key: _normalise_payload(value) for key, value in payload.items()}
    if isinstance(payload, (list, tuple, set, frozenset)):
        return [_normalise_payload(value) for value in payload]
    return payload


def _extract_expected_tokens(metadata: Mapping[str, Any]) -> Tuple[str, ...]:
    """Normalise expected token declarations from configuration metadata."""

    candidates: list[str] = []
    for key in ("expected_dynamic", "expected_tokens", "tokens"):
        value = metadata.get(key)
        if isinstance(value, Mapping):
            for token, flag in value.items():
                if isinstance(flag, bool) and not flag:
                    continue
                if isinstance(token, str) and token.strip():
                    candidates.append(token.strip())
        elif isinstance(value, (list, tuple, set, frozenset)):
            for item in value:
                if isinstance(item, str) and item.strip():
                    candidates.append(item.strip())
        elif isinstance(value, str) and value.strip():
            candidates.append(value.strip())

    deduped: list[str] = []
    seen: set[str] = set()
    for token in candidates:
        lowered = token.lower()
        if lowered in seen:
            continue
        seen.add(lowered)
        deduped.append(token)
    return tuple(deduped)


def _write_json(payload: Mapping[str, Any], *, output: Path | None, indent: int, sort_keys: bool) -> None:
    """Serialise ``payload`` to JSON writing to ``output`` (or STDOUT)."""

    serialisable = _normalise_payload(payload)
    json_kwargs: MutableMapping[str, Any] = {"sort_keys": sort_keys}
    json_kwargs["indent"] = None if indent <= 0 else indent
    text = json.dumps(serialisable, **json_kwargs)
    suffix = "\n" if not text.endswith("\n") else ""
    if output is None:
        sys.stdout.write(text + suffix)
    else:
        output.write_text(text + suffix)


def _add_output_options(parser: argparse.ArgumentParser) -> None:
    parser.add_argument(
        "--indent",
        type=int,
        default=2,
        help="JSON indentation level (0 for compact output).",
    )
    parser.add_argument(
        "--sort-keys",
        action="store_true",
        help="Sort keys before writing JSON output.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Optional file to write results to (defaults to stdout).",
    )


def _resolve_relative_path(
    entry: Mapping[str, Any],
    *,
    root: Optional[Path],
) -> Optional[str]:
    relative = entry.get("relative_path")
    if isinstance(relative, str) and relative:
        return relative

    path_text = entry.get("path")
    if not isinstance(path_text, str) or not path_text:
        return None

    path = Path(path_text)
    if root is None:
        return path.name

    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.name


def _build_bridge_payload(
    store: ProfileStore,
    hunts: Iterable[Mapping[str, Any]],
    *,
    tags: Optional[Sequence[str]],
    root: Optional[Path],
) -> Mapping[str, Any]:
    items = []
    for entry in hunts:
        relative = _resolve_relative_path(entry, root=root)
        matches = store.matching_configs(tags, relative_path=relative)
        token_name: Optional[str] = None
        rule = entry.get("rule") if isinstance(entry, Mapping) else None
        if isinstance(rule, Mapping):
            raw_token = rule.get("token_name")
            if isinstance(raw_token, str) and raw_token.strip():
                token_name = raw_token.strip()
        profile_entries: list[dict[str, Any]] = []
        for match in matches:
            profile_entry: dict[str, Any] = {
                "profile": match.profile.name,
                "config": match.config.identifier,
                "profile_tags": sorted(match.profile.tags),
                "expected_format": match.config.expected_format,
                "expected_variant": match.config.expected_variant,
            }
            tokens = _extract_expected_tokens(match.config.metadata)
            if tokens:
                profile_entry["expected_tokens"] = tokens
                if token_name:
                    token_lower = token_name.lower()
                    expected_lower = {token.lower() for token in tokens}
                    profile_entry["token_match"] = token_lower in expected_lower
                    remaining = tuple(
                        token for token in tokens if token.lower() != token_lower
                    )
                    if remaining:
                        profile_entry["remaining_expected_tokens"] = remaining
            profile_entries.append(profile_entry)

        items.append(
            {
                "hunt": _normalise_payload(entry),
                "relative_path": relative,
                "profiles": profile_entries,
            }
        )
    return {"items": items}


def _handle_summary(args: argparse.Namespace) -> int:
    store_payload = _load_json(args.store)
    store = _store_from_payload(store_payload)
    summary = store.summary()
    _write_json(summary, output=args.output, indent=args.indent, sort_keys=args.sort_keys)
    return 0


def _handle_diff(args: argparse.Namespace) -> int:
    baseline = _load_json(args.baseline)
    current = _load_json(args.current)
    diff = diff_summary_snapshots(baseline, current)
    _write_json(diff, output=args.output, indent=args.indent, sort_keys=args.sort_keys)
    return 0


def _handle_hunt_bridge(args: argparse.Namespace) -> int:
    store_payload = _load_json(args.store)
    hunts_payload = _load_json(args.hunt)

    if not isinstance(hunts_payload, Sequence):
        raise ValueError("Hunt payload must be a JSON array of hunt hits.")

    store = _store_from_payload(store_payload)
    bridge = _build_bridge_payload(
        store,
        (entry for entry in hunts_payload if isinstance(entry, Mapping)),
        tags=args.tags,
        root=args.root,
    )
    _write_json(bridge, output=args.output, indent=args.indent, sort_keys=args.sort_keys)
    return 0


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="driftbuster-profile",
        description="Profile summary and diff helper for manual audits.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    summary_parser = subparsers.add_parser(
        "summary",
        help="Generate a profile summary from a ProfileStore payload.",
    )
    summary_parser.add_argument(
        "store",
        type=Path,
        help="Path to JSON payload compatible with ProfileStore.from_dict().",
    )
    _add_output_options(summary_parser)
    summary_parser.set_defaults(func=_handle_summary)

    diff_parser = subparsers.add_parser(
        "diff",
        help="Diff two stored profile summary JSON payloads.",
    )
    diff_parser.add_argument("baseline", type=Path, help="Baseline summary JSON file.")
    diff_parser.add_argument("current", type=Path, help="Current summary JSON file.")
    _add_output_options(diff_parser)
    diff_parser.set_defaults(func=_handle_diff)

    bridge_parser = subparsers.add_parser(
        "hunt-bridge",
        help="Attach profile metadata to hunt hits for manual review.",
    )
    bridge_parser.add_argument(
        "store",
        type=Path,
        help="ProfileStore JSON payload (same format as the summary command).",
    )
    bridge_parser.add_argument(
        "hunt",
        type=Path,
        help="JSON array produced by hunt_path(..., return_json=True).",
    )
    bridge_parser.add_argument(
        "--tag",
        dest="tags",
        action="append",
        default=[],
        help="Activation tag applied when matching profile configs (repeatable).",
    )
    bridge_parser.add_argument(
        "--root",
        type=Path,
        help="Base path used to resolve hunt absolute paths into profile-relative paths.",
    )
    _add_output_options(bridge_parser)
    bridge_parser.set_defaults(func=_handle_hunt_bridge)

    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        handler = args.func
    except AttributeError:  # pragma: no cover - argparse fallback
        return 1
    try:
        return handler(args)
    except Exception as exc:  # pragma: no cover - manual utility
        sys.stderr.write(f"error: {exc}\n")
        return 1


if __name__ == "__main__":  # pragma: no cover - manual helper
    raise SystemExit(main())
