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


def _clean_secret_values(values: Sequence[str] | None) -> list[str]:
    cleaned: list[str] = []
    for value in values or ():
        if value is None:
            continue
        text = value.strip()
        if not text or text in cleaned:
            continue
        cleaned.append(text)
    return cleaned


def _build_secret_scanner_payload(
    ignore_rules: Sequence[str] | None,
    ignore_patterns: Sequence[str] | None,
) -> dict[str, list[str]]:
    payload: dict[str, list[str]] = {}
    rules = _clean_secret_values(ignore_rules)
    patterns = _clean_secret_values(ignore_patterns)
    if rules:
        payload["ignore_rules"] = rules
    if patterns:
        payload["ignore_patterns"] = patterns
    return payload


def _apply_secret_overrides(
    profile: run_profiles.RunProfile,
    ignore_rules: Sequence[str] | None,
    ignore_patterns: Sequence[str] | None,
) -> run_profiles.RunProfile:
    overrides = _build_secret_scanner_payload(ignore_rules, ignore_patterns)
    if not overrides:
        return profile

    payload = profile.to_dict()
    existing = dict(payload.get("secret_scanner") or {})
    for key, values in overrides.items():
        merged = _clean_secret_values(existing.get(key))
        for value in values:
            if value not in merged:
                merged.append(value)
        existing[key] = merged
    payload["secret_scanner"] = existing
    return run_profiles.RunProfile.from_dict(payload)


def _create(args: argparse.Namespace) -> int:
    profile = run_profiles.RunProfile(
        name=args.name,
        description=args.description,
        sources=tuple(args.source or ()),
        baseline=args.baseline,
        options=_parse_options(args.option or ()),
        secret_scanner=_build_secret_scanner_payload(
            args.secret_ignore_rules,
            args.secret_ignore_patterns,
        ),
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

    profile = _apply_secret_overrides(
        profile,
        args.secret_ignore_rules,
        args.secret_ignore_patterns,
    )

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
    create.add_argument(
        "--secret-ignore-rule",
        action="append",
        dest="secret_ignore_rules",
        default=[],
        help="Secret scanner rule names to ignore (repeatable).",
    )
    create.add_argument(
        "--secret-ignore-pattern",
        action="append",
        dest="secret_ignore_patterns",
        default=[],
        help="Regular expressions to suppress secret findings (repeatable).",
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
    run_command.add_argument(
        "--secret-ignore-rule",
        action="append",
        dest="secret_ignore_rules",
        default=[],
        help="Secret scanner rule names to ignore for this run (repeatable).",
    )
    run_command.add_argument(
        "--secret-ignore-pattern",
        action="append",
        dest="secret_ignore_patterns",
        default=[],
        help="Regular expressions to suppress secret findings for this run (repeatable).",
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
