"""Avalonia GUI bridge exposing DriftBuster helpers over JSON/STDIO."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict

from .core.diffing import build_diff_plan, plan_to_kwargs
from .core import run_profiles
from .hunt import default_rules, hunt_path


def _resolve_path(value: str | None) -> Path:
    if not value:
        raise ValueError("path is required")
    candidate = Path(value).expanduser().resolve()
    if not candidate.exists():
        raise FileNotFoundError(f"Path does not exist: {candidate}")
    return candidate


def _load_text(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace")


def _diff_command(payload: Dict[str, Any]) -> Dict[str, Any]:
    versions = payload.get("versions")

    resolved_paths: list[Path]

    if versions:
        if not isinstance(versions, list):
            raise ValueError("versions must be an array of file paths")
        resolved_paths = [_resolve_path(value) for value in versions]
    else:
        left = payload.get("left")
        right = payload.get("right")
        if left is None or right is None:
            raise ValueError("Provide at least two file paths via 'versions' or 'left'/'right'.")
        resolved_paths = [_resolve_path(left), _resolve_path(right)]

    if len(resolved_paths) < 2:
        raise ValueError("At least two file paths are required for diffing.")

    baseline = resolved_paths[0]
    if not baseline.is_file():
        raise ValueError(f"Baseline path is not a file: {baseline}")

    baseline_text = _load_text(baseline)

    comparisons = []
    for candidate in resolved_paths[1:]:
        if not candidate.is_file():
            raise ValueError(f"Comparison path is not a file: {candidate}")

        candidate_text = _load_text(candidate)
        plan = build_diff_plan(
            baseline_text,
            candidate_text,
            from_label=baseline.name,
            to_label=candidate.name,
        )

        comparisons.append(
            {
                "from": baseline.name,
                "to": candidate.name,
                "plan": plan_to_kwargs(plan),
                "metadata": {
                    "left_path": str(baseline),
                    "right_path": str(candidate),
                    "content_type": plan.content_type,
                    "context_lines": plan.context_lines,
                },
            }
        )

    return {
        "versions": [str(path) for path in resolved_paths],
        "comparisons": comparisons,
    }


def _hunt_command(payload: Dict[str, Any]) -> Dict[str, Any]:
    directory_value = payload.get("directory")
    pattern = (payload.get("pattern") or "").strip().lower()

    root_path = _resolve_path(directory_value)

    hits = hunt_path(
        root_path,
        rules=default_rules(),
        return_json=True,
    )

    if pattern:
        filtered = [hit for hit in hits if pattern in hit["excerpt"].lower()]
    else:
        filtered = list(hits)

    return {
        "directory": str(root_path),
        "pattern": pattern or None,
        "count": len(filtered),
        "hits": filtered,
    }


def _profile_from_payload(payload: Dict[str, Any]) -> run_profiles.RunProfile:
    return run_profiles.RunProfile.from_dict(payload)


def _profile_list_command(payload: Dict[str, Any]) -> Dict[str, Any]:
    base_dir = payload.get("base_dir")
    profiles = run_profiles.list_profiles(base_dir=Path(base_dir) if base_dir else None)
    return {"profiles": [profile.to_dict() for profile in profiles]}


def _profile_save_command(payload: Dict[str, Any]) -> Dict[str, Any]:
    profile_payload = payload.get("profile")
    if profile_payload is None:
        raise ValueError("profile payload is required")
    profile = _profile_from_payload(profile_payload)
    base_dir = payload.get("base_dir")
    directory = run_profiles.save_profile(profile, base_dir=Path(base_dir) if base_dir else None)
    return {"profile": profile.to_dict(), "directory": str(directory)}


def _profile_run_command(payload: Dict[str, Any]) -> Dict[str, Any]:
    base_dir_text = payload.get("base_dir")
    base_dir = Path(base_dir_text) if base_dir_text else None

    if "profile" in payload:
        profile = _profile_from_payload(payload["profile"])
    elif "name" in payload:
        profile = run_profiles.load_profile(payload["name"], base_dir=base_dir)
    else:
        raise ValueError("Provide either a profile payload or profile name.")

    if payload.get("save", True):
        run_profiles.save_profile(profile, base_dir=base_dir)

    timestamp = payload.get("timestamp")
    result = run_profiles.execute_profile(profile, base_dir=base_dir, timestamp=timestamp)
    return result.to_dict()


def _ping_command() -> Dict[str, Any]:
    return {"status": "pong"}


def handle(payload: Dict[str, Any]) -> Dict[str, Any]:
    command = payload.get("cmd")

    if command == "ping":
        return _ping_command()
    if command == "diff":
        return _diff_command(payload)
    if command == "hunt":
        return _hunt_command(payload)
    if command == "profile-list":
        return _profile_list_command(payload)
    if command == "profile-save":
        return _profile_save_command(payload)
    if command == "profile-run":
        return _profile_run_command(payload)

    raise ValueError(f"Unknown command: {command}")


def _write_response(message: Dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


def main() -> None:
    while True:
        raw = sys.stdin.readline()
        if raw == "":
            break
        raw = raw.strip()
        if not raw:
            continue

        try:
            payload = json.loads(raw)
        except json.JSONDecodeError as exc:  # noqa: BLE001 - input validation
            _write_response({"ok": False, "error": f"invalid json: {exc}"})
            continue

        if payload.get("cmd") == "shutdown":
            _write_response({"ok": True, "result": {"status": "bye"}})
            break

        try:
            result = handle(payload)
        except Exception as exc:  # noqa: BLE001 - convert to structured error
            _write_response({"ok": False, "error": str(exc)})
            continue

        _write_response({"ok": True, "result": result})


if __name__ == "__main__":
    main()
