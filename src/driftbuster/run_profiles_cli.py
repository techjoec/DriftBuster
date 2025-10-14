from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Sequence

from .core import run_profiles


def _parse_options(option_pairs: Sequence[str]) -> dict[str, str]:
    options: dict[str, str] = {}
    for item in option_pairs:
        if "=" not in item:
            raise SystemExit(f"Invalid option format: {item!r}. Use key=value.")
        key, value = item.split("=", 1)
        options[key.strip()] = value.strip()
    return options


def _create(args: argparse.Namespace) -> int:
    profile = run_profiles.RunProfile(
        name=args.name,
        description=args.description,
        sources=tuple(args.source or ()),
        baseline=args.baseline,
        options=_parse_options(args.option or ()),
    )
    run_profiles.save_profile(profile, base_dir=args.base_dir)
    print(f"Saved profile '{profile.name}'")
    return 0


def _list_profiles(args: argparse.Namespace) -> int:
    profiles = run_profiles.list_profiles(base_dir=args.base_dir)
    if not profiles:
        print("No profiles found.")
        return 0
    for profile in profiles:
        description = profile.description or ""
        print(f"- {profile.name} {description}".rstrip())
    return 0


def _show(args: argparse.Namespace) -> int:
    profile = run_profiles.load_profile(args.name, base_dir=args.base_dir)
    print(json.dumps(profile.to_dict(), indent=2, sort_keys=True))
    return 0


def _run(args: argparse.Namespace) -> int:
    if args.profile:
        profile = run_profiles.RunProfile.from_dict(json.loads(Path(args.profile).read_text(encoding="utf-8")))
    else:
        profile = run_profiles.load_profile(args.name, base_dir=args.base_dir)

    if args.save:
        run_profiles.save_profile(profile, base_dir=args.base_dir)

    result = run_profiles.execute_profile(
        profile,
        base_dir=args.base_dir,
        timestamp=args.timestamp,
    )

    print(f"Run saved to {result.output_dir}")
    print(f"Files collected: {len(result.files)}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="driftbuster-run",
        description="Manage DriftBuster run profiles.",
    )
    parser.add_argument(
        "--base-dir",
        type=Path,
        help="Override the profiles root directory (defaults to ./Profiles).",
    )

    subparsers = parser.add_subparsers(dest="command", required=True)

    create = subparsers.add_parser("create", help="Create or update a run profile.")
    create.add_argument("--name", required=True)
    create.add_argument("--description")
    create.add_argument(
        "--source",
        action="append",
        help="File, directory, or glob to include (repeatable).",
    )
    create.add_argument(
        "--baseline",
        help="Source path that should act as the baseline (defaults to first source).",
    )
    create.add_argument(
        "--option",
        action="append",
        help="Custom option in key=value format (repeatable).",
    )
    create.set_defaults(func=_create)

    list_parser = subparsers.add_parser("list", help="List available profiles.")
    list_parser.set_defaults(func=_list_profiles)

    show = subparsers.add_parser("show", help="Show profile configuration.")
    show.add_argument("name")
    show.set_defaults(func=_show)

    run_command = subparsers.add_parser("run", help="Execute a profile run.")
    run_group = run_command.add_mutually_exclusive_group(required=True)
    run_group.add_argument("--name", help="Name of a saved profile.")
    run_group.add_argument("--profile", help="Path to a profile JSON file.")
    run_command.add_argument("--timestamp", help="Override run timestamp (UTC).")
    run_command.add_argument(
        "--save",
        action="store_true",
        help="Persist the supplied profile before running.",
    )
    run_command.set_defaults(func=_run)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    func = getattr(args, "func", None)
    if func is None:
        parser.print_help()
        return 1
    return func(args)


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
