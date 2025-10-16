from __future__ import annotations

import argparse
import re
from typing import Sequence

from .registry import (
    enumerate_installed_apps,
    find_app_registry_roots,
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

    return parser


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

    parser.print_help()
    return 2


if __name__ == "__main__":  # pragma: no cover - manual use
    raise SystemExit(main())

