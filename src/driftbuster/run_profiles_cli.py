from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping, Sequence

from .core import run_profiles
from .scheduler import ProfileScheduler, ScheduleError, ScheduleSpec


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


def _serialise_timezone(tz: object) -> str:
    key = getattr(tz, "key", None)
    if key:
        return str(key)
    tzname = getattr(tz, "tzname", None)
    if callable(tzname):  # pragma: no cover - exercised indirectly
        name = tzname(None)
        if name:
            return name
    return str(tz)


def _default_schedule_config_path(base_dir: Path | None, override: Path | None) -> Path:
    if override is not None:
        return Path(override)
    return run_profiles.profiles_root(base_dir) / "schedules.json"


def _default_schedule_state_path(base_dir: Path | None, override: Path | None) -> Path:
    if override is not None:
        return Path(override)
    return run_profiles.profiles_root(base_dir) / "scheduler-state.json"


def _load_schedule_payload(path: Path) -> Sequence[Mapping[str, Any]]:
    if not path.exists():
        raise SystemExit(f"Schedule manifest not found: {path}")
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise SystemExit(f"Failed to parse schedules from {path}: {exc}") from exc
    if isinstance(payload, Mapping):
        entries = payload.get("schedules", [])
    else:
        entries = payload
    if not entries:
        return []
    if not isinstance(entries, Sequence):
        raise SystemExit("Schedules payload must be an array of schedule entries.")
    result: list[Mapping[str, Any]] = []
    for entry in entries:
        if isinstance(entry, Mapping):
            result.append(entry)
    return result


def _build_schedule_specs(
    entries: Sequence[Mapping[str, Any]],
    *,
    base_dir: Path | None,
) -> list[ScheduleSpec]:
    loader = lambda name: run_profiles.load_profile(name, base_dir=base_dir)
    specs: list[ScheduleSpec] = []
    for entry in entries:
        try:
            specs.append(ScheduleSpec.from_dict(entry, profile_loader=loader))
        except ScheduleError as exc:
            raise SystemExit(str(exc)) from exc
    return specs


def _load_schedule_state(path: Path) -> Mapping[str, Mapping[str, Any]]:
    if not path.exists():
        return {}
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise SystemExit(f"Failed to parse scheduler state from {path}: {exc}") from exc
    if not isinstance(payload, Mapping):
        raise SystemExit("Scheduler state payload must be a JSON object.")
    normalised: dict[str, Mapping[str, Any]] = {}
    for name, entry in payload.items():
        if not isinstance(entry, Mapping):
            continue
        normalised[name] = {
            "next_run": entry.get("next_run"),
            "pending": entry.get("pending"),
        }
    return normalised


def _write_schedule_state(scheduler: ProfileScheduler, path: Path) -> None:
    snapshot = scheduler.snapshot_state()
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(snapshot, indent=2, sort_keys=True)
    if not text.endswith("\n"):
        text += "\n"
    path.write_text(text, encoding="utf-8")


def _build_scheduler(args: argparse.Namespace) -> tuple[ProfileScheduler, Path]:
    base_dir = args.base_dir
    config_path = _default_schedule_config_path(base_dir, getattr(args, "config", None))
    entries = _load_schedule_payload(config_path)
    specs = _build_schedule_specs(entries, base_dir=base_dir)
    scheduler = ProfileScheduler(specs)
    state_path = _default_schedule_state_path(base_dir, getattr(args, "state", None))
    state_payload = _load_schedule_state(state_path)
    scheduler.apply_state(state_payload)
    return scheduler, state_path


def _print_json(payload: Any) -> None:
    text = json.dumps(payload, indent=2, sort_keys=True)
    if not text.endswith("\n"):
        text += "\n"
    sys.stdout.write(text)


def _parse_reference_timestamp(value: str) -> datetime:
    try:
        candidate = datetime.fromisoformat(value)
    except ValueError as exc:
        raise SystemExit(f"Unable to parse timestamp: {value!r}") from exc
    if candidate.tzinfo is None:
        return candidate.replace(tzinfo=timezone.utc)
    return candidate.astimezone(timezone.utc)


def _schedule_list(args: argparse.Namespace) -> int:
    scheduler, _ = _build_scheduler(args)
    snapshot = scheduler.snapshot_state()
    payload = []
    for spec in scheduler.schedules():
        entry: dict[str, Any] = {
            "name": spec.name,
            "profile": spec.profile,
            "interval_seconds": spec.interval.total_seconds(),
            "tags": list(spec.tags),
            "metadata": dict(spec.metadata),
            "start_at": spec.start_at.isoformat() if spec.start_at else None,
            "next_run": snapshot.get(spec.name, {}).get("next_run"),
            "pending": snapshot.get(spec.name, {}).get("pending"),
        }
        if spec.window:
            entry["window"] = {
                "start": spec.window.start.isoformat(),
                "end": spec.window.end.isoformat(),
                "timezone": _serialise_timezone(spec.window.timezone),
            }
        payload.append(entry)
    _print_json(payload)
    return 0


def _schedule_due(args: argparse.Namespace) -> int:
    scheduler, state_path = _build_scheduler(args)
    reference = None
    if getattr(args, "at", None):
        reference = _parse_reference_timestamp(args.at)
    runs = scheduler.due(reference=reference)
    payload = [
        {
            "name": run.name,
            "profile": run.profile,
            "scheduled_for": run.scheduled_for.isoformat(),
            "tags": list(run.tags),
            "metadata": dict(run.metadata),
        }
        for run in runs
    ]
    _write_schedule_state(scheduler, state_path)
    _print_json(payload)
    return 0


def _schedule_mark_complete(args: argparse.Namespace) -> int:
    scheduler, state_path = _build_scheduler(args)
    completed_at = None
    if getattr(args, "completed_at", None):
        completed_at = _parse_reference_timestamp(args.completed_at)
    try:
        scheduler.mark_complete(args.name, completed_at=completed_at)
    except ScheduleError as exc:
        raise SystemExit(str(exc)) from exc
    snapshot = scheduler.snapshot_state().get(args.name, {})
    result = {
        "name": args.name,
        "next_run": snapshot.get("next_run"),
        "pending": snapshot.get("pending"),
    }
    _write_schedule_state(scheduler, state_path)
    _print_json(result)
    return 0


def _schedule_skip(args: argparse.Namespace) -> int:
    scheduler, state_path = _build_scheduler(args)
    resume_at = _parse_reference_timestamp(args.resume_at)
    try:
        scheduler.skip_until(args.name, resume_at)
    except ScheduleError as exc:
        raise SystemExit(str(exc)) from exc
    snapshot = scheduler.snapshot_state().get(args.name, {})
    result = {
        "name": args.name,
        "next_run": snapshot.get("next_run"),
        "pending": snapshot.get("pending"),
    }
    _write_schedule_state(scheduler, state_path)
    _print_json(result)
    return 0


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

    schedule = subparsers.add_parser(
        "schedule", help="Inspect and manage run profile schedules."
    )
    schedule_sub = schedule.add_subparsers(dest="schedule_command", required=True)

    def _add_schedule_common(parser: argparse.ArgumentParser) -> None:
        parser.add_argument(
            "--config",
            type=Path,
            help="Path to the schedules manifest (defaults to Profiles/schedules.json).",
        )
        parser.add_argument(
            "--state",
            type=Path,
            help=(
                "Path to persist scheduler state (defaults to Profiles/scheduler-state.json)."
            ),
        )

    schedule_list = schedule_sub.add_parser(
        "list", help="List registered schedules and their next run windows."
    )
    _add_schedule_common(schedule_list)
    schedule_list.set_defaults(func=_schedule_list)

    schedule_due = schedule_sub.add_parser(
        "due", help="Return runs that are due as of the supplied timestamp."
    )
    _add_schedule_common(schedule_due)
    schedule_due.add_argument(
        "--at",
        help="Reference timestamp in ISO 8601 format (defaults to current UTC time).",
    )
    schedule_due.set_defaults(func=_schedule_due)

    schedule_complete = schedule_sub.add_parser(
        "mark-complete",
        help="Mark a pending run complete and advance its schedule.",
    )
    _add_schedule_common(schedule_complete)
    schedule_complete.add_argument("--name", required=True, help="Schedule name to mark complete.")
    schedule_complete.add_argument(
        "--completed-at",
        help="Completion timestamp in ISO 8601 format (defaults to the pending time).",
    )
    schedule_complete.set_defaults(func=_schedule_mark_complete)

    schedule_skip = schedule_sub.add_parser(
        "skip-until", help="Skip the schedule until the supplied resume timestamp."
    )
    _add_schedule_common(schedule_skip)
    schedule_skip.add_argument("--name", required=True, help="Schedule name to update.")
    schedule_skip.add_argument(
        "--resume-at",
        required=True,
        help="Resume timestamp in ISO 8601 format.",
    )
    schedule_skip.set_defaults(func=_schedule_skip)

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
