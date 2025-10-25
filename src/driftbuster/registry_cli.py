from __future__ import annotations

import argparse
import json
import re
from typing import Any, Sequence

from .registry import (
    enumerate_installed_apps,
    find_app_registry_roots,
    parse_registry_root_descriptor,
    search_registry,
    SearchSpec,
    is_windows,
)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="driftbuster-registry", description="Windows Registry live scan helpers")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("list-apps", help="List installed applications")

    suggest = sub.add_parser("suggest-roots", help="Suggest registry roots for an app token")
    suggest.add_argument("token", help="App token, e.g. part of DisplayName or Publisher")

    search = sub.add_parser("search", help="Search registry under suggested roots")
    search.add_argument("token", help="App token, e.g. part of DisplayName or Publisher")
    search.add_argument("--keyword", action="append", default=[], help="Keyword to require (repeatable)")
    search.add_argument("--pattern", action="append", default=[], help="Regex to match (repeatable)")
    search.add_argument("--max-depth", type=int, default=12)
    search.add_argument("--max-hits", type=int, default=200)
    search.add_argument("--time-budget", type=float, default=10.0)
    search.add_argument(
        "--root",
        action="append",
        default=[],
        help="Explicit hive path, e.g. HKLM\\Software\\Vendor[,view=64] (repeatable)",
    )

    emit = sub.add_parser(
        "emit-config",
        help="Render a registry_scan source snippet for remote or local runs",
    )
    emit.add_argument("token", help="Token to feed into registry_scan entries")
    emit.add_argument("--alias", help="Optional alias for manifest output")
    emit.add_argument("--keyword", action="append", default=[], help="Keyword to require (repeatable)")
    emit.add_argument("--pattern", action="append", default=[], help="Regex to match (repeatable)")
    emit.add_argument("--max-depth", type=int, default=12)
    emit.add_argument("--max-hits", type=int, default=200)
    emit.add_argument("--time-budget", type=float, default=10.0)
    emit.add_argument(
        "--remote-target",
        action="append",
        default=[],
        metavar="HOST[,key=value]...",
        help=(
            "Remote host descriptor. Repeat to add a batch. Supported keys: "
            "username, password-env, credential-profile, transport, port, use-ssl, alias"
        ),
    )
    emit.add_argument(
        "--root",
        action="append",
        default=[],
        help="Explicit hive path, e.g. HKLM\\Software\\Vendor[,view=64] (repeatable)",
    )

    return parser


def _parse_remote_target_arg(value: str) -> dict[str, Any]:
    parts = [segment.strip() for segment in value.split(",") if segment.strip()]
    if not parts:
        raise ValueError("remote target requires a host segment")
    host_segment = parts[0]
    if "=" in host_segment:
        raise ValueError("remote target must start with the host name")

    payload: dict[str, Any] = {"host": host_segment}
    for entry in parts[1:]:
        if "=" not in entry:
            raise ValueError(f"Remote target entry '{entry}' must include '='")
        key, raw_value = entry.split("=", 1)
        key_normalised = key.strip().lower().replace("-", "_")
        raw_value = raw_value.strip()
        if not raw_value:
            raise ValueError(f"Remote target value for '{key}' must be non-empty")
        if key_normalised in {"port"}:
            payload[key_normalised] = int(raw_value)
        elif key_normalised in {"use_ssl"}:
            lowered = raw_value.lower()
            if lowered in {"1", "true", "yes", "on"}:
                payload[key_normalised] = True
            elif lowered in {"0", "false", "no", "off"}:
                payload[key_normalised] = False
            else:
                raise ValueError(f"Unsupported boolean value '{raw_value}' for use-ssl")
        elif key_normalised in {"username", "user"}:
            payload["username"] = raw_value
        elif key_normalised in {"password_env"}:
            payload["password_env"] = raw_value
        elif key_normalised in {"credential_profile"}:
            payload["credential_profile"] = raw_value
        elif key_normalised in {"transport"}:
            payload["transport"] = raw_value
        elif key_normalised in {"alias"}:
            payload["alias"] = raw_value
        else:
            raise ValueError(f"Unsupported remote target key '{key}'")
    return payload


def _parse_root_argument(value: str) -> tuple[str, str, str | None]:
    root = parse_registry_root_descriptor(value)
    return root.hive, root.path, root.view


def main(argv: Sequence[str] | None = None) -> int:
    if not is_windows():
        raise SystemExit("Registry scanning requires Windows.")

    parser = build_parser()
    args = parser.parse_args(argv)

    if args.command == "list-apps":
        apps = enumerate_installed_apps()
        for app in apps:
            version = f" {app.version}" if app.version else ""
            print(f"{app.display_name}{version}  [{app.hive} {app.view}]  {app.key_path}")
        return 0

    apps = enumerate_installed_apps()

    if args.command == "suggest-roots":
        roots = find_app_registry_roots(args.token, installed=apps)
        for hive, path, view in roots:
            suffix = f" ({view}-bit)" if view in {"32", "64"} else ""
            print(f"{hive} \\ {path}{suffix}")
        return 0

    if args.command == "search":
        try:
            explicit_roots = tuple(_parse_root_argument(value) for value in args.root)
        except ValueError as exc:
            raise SystemExit(f"invalid --root value: {exc}") from exc

        if explicit_roots:
            roots = explicit_roots
        else:
            roots = find_app_registry_roots(args.token, installed=apps)
        patterns = tuple(re.compile(p) for p in (args.pattern or ()))
        spec = SearchSpec(
            keywords=tuple(args.keyword or ()),
            patterns=patterns,
            max_depth=args.max_depth,
            max_hits=args.max_hits,
            time_budget_s=args.time_budget,
        )
        hits = search_registry(roots, spec)
        for hit in hits:
            print(f"{hit.hive} \\ {hit.path} :: {hit.value_name} = {hit.data_preview}")
        return 0

    if args.command == "emit-config":
        snippet: dict[str, Any] = {
            "registry_scan": {
                "token": args.token,
                "keywords": [kw for kw in args.keyword if kw],
                "patterns": [pat for pat in args.pattern if pat],
                "max_depth": args.max_depth,
                "max_hits": args.max_hits,
                "time_budget_s": args.time_budget,
            }
        }
        if args.alias:
            snippet["alias"] = args.alias

        try:
            explicit_roots = [
                _parse_root_argument(value)
                for value in args.root
            ]
        except ValueError as exc:
            raise SystemExit(f"invalid --root value: {exc}") from exc
        if explicit_roots:
            snippet["registry_scan"]["roots"] = [
                {"hive": hive, "path": path, **({"view": view} if view else {})}
                for hive, path, view in explicit_roots
            ]

        remote_targets = [
            _parse_remote_target_arg(value)
            for value in args.remote_target
        ]
        if remote_targets:
            primary = remote_targets[0]
            snippet["registry_scan"]["remote"] = primary
            if len(remote_targets) > 1:
                snippet["registry_scan"]["remote_batch"] = remote_targets[1:]

        # Drop empty sequences to keep output tight.
        for key in ("keywords", "patterns"):
            if not snippet["registry_scan"][key]:
                snippet["registry_scan"].pop(key)

        print(json.dumps(snippet, indent=2, sort_keys=True))
        return 0

    parser.print_help()
    return 2


if __name__ == "__main__":  # pragma: no cover - manual use
    raise SystemExit(main())

