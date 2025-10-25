from __future__ import annotations

import contextlib
import io
import json
from pathlib import Path

from driftbuster.core.run_profiles import RunProfile, save_profile
from driftbuster.run_profiles_cli import main as cli_main


def _invoke_cli(base_dir: Path, *args: str) -> tuple[int, str]:
    argv = ["--base-dir", str(base_dir), *args]
    buffer = io.StringIO()
    with contextlib.redirect_stdout(buffer):
        exit_code = cli_main(argv)
    return exit_code, buffer.getvalue()


def test_schedule_commands_manage_state(tmp_path: Path) -> None:
    base_dir = tmp_path
    source_dir = base_dir / "data"
    source_dir.mkdir()
    source_file = source_dir / "config.txt"
    source_file.write_text("baseline", encoding="utf-8")

    profile = RunProfile(name="nightly", sources=[str(source_file)])
    save_profile(profile, base_dir=base_dir)

    schedules_path = (base_dir / "Profiles" / "schedules.json")
    schedules_path.parent.mkdir(parents=True, exist_ok=True)
    schedules_path.write_text(
        json.dumps(
            {
                "schedules": [
                    {
                        "name": "nightly",
                        "profile": "nightly",
                        "every": "24h",
                        "start_at": "2025-01-01T00:00:00Z",
                        "metadata": {"notes": "Rotation scheduled"},
                    }
                ]
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )

    exit_code, output = _invoke_cli(
        base_dir,
        "schedule",
        "due",
        "--at",
        "2025-01-02T00:00:00Z",
    )
    assert exit_code == 0
    due_payload = json.loads(output)
    assert len(due_payload) == 1
    due_entry = due_payload[0]
    assert due_entry["name"] == "nightly"
    assert due_entry["scheduled_for"] == "2025-01-01T00:00:00+00:00"

    state_path = schedules_path.parent / "scheduler-state.json"
    state = json.loads(state_path.read_text(encoding="utf-8"))
    assert state["nightly"]["pending"] == "2025-01-01T00:00:00+00:00"

    exit_code, output = _invoke_cli(
        base_dir,
        "schedule",
        "mark-complete",
        "--name",
        "nightly",
        "--completed-at",
        "2025-01-01T00:00:00Z",
    )
    assert exit_code == 0
    completion_payload = json.loads(output)
    assert completion_payload["next_run"] == "2025-01-02T00:00:00+00:00"
    state = json.loads(state_path.read_text(encoding="utf-8"))
    assert state["nightly"]["next_run"] == "2025-01-02T00:00:00+00:00"
    assert state["nightly"]["pending"] is None

    exit_code, output = _invoke_cli(
        base_dir,
        "schedule",
        "skip-until",
        "--name",
        "nightly",
        "--resume-at",
        "2025-01-05T09:30:00Z",
    )
    assert exit_code == 0
    skip_payload = json.loads(output)
    assert skip_payload["next_run"] == "2025-01-05T09:30:00+00:00"

    exit_code, output = _invoke_cli(base_dir, "schedule", "list")
    assert exit_code == 0
    listing = json.loads(output)
    assert listing[0]["next_run"] == "2025-01-05T09:30:00+00:00"
