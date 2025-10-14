from __future__ import annotations

import io
import json
from pathlib import Path

import pytest

from driftbuster import api_server


def test_diff_command_builds_plan(tmp_path: Path) -> None:
    baseline = tmp_path / "baseline.txt"
    version_b = tmp_path / "b.txt"
    version_c = tmp_path / "c.txt"

    baseline.write_text("a=1\n", encoding="utf-8")
    version_b.write_text("a=2\n", encoding="utf-8")
    version_c.write_text("a=3\n", encoding="utf-8")

    payload = api_server._diff_command(
        {"versions": [str(baseline), str(version_b), str(version_c)]}
    )

    assert payload["versions"] == [str(baseline.resolve()), str(version_b.resolve()), str(version_c.resolve())]
    assert len(payload["comparisons"]) == 2

    first = payload["comparisons"][0]
    assert first["from"] == "baseline.txt"
    assert first["to"] == "b.txt"
    assert "plan" in first
    assert first["metadata"]["left_path"].endswith("baseline.txt")


def test_diff_command_accepts_left_right(tmp_path: Path) -> None:
    left = tmp_path / "one.txt"
    right = tmp_path / "two.txt"
    left.write_text("v1", encoding="utf-8")
    right.write_text("v2", encoding="utf-8")

    payload = api_server._diff_command({"left": str(left), "right": str(right)})

    assert payload["versions"] == [str(left.resolve()), str(right.resolve())]
    comparison = payload["comparisons"][0]
    assert comparison["from"] == "one.txt"
    assert comparison["to"] == "two.txt"


def test_hunt_command_filters_hits(tmp_path: Path) -> None:
    directory = tmp_path / "configs"
    directory.mkdir()
    sample = directory / "settings.txt"
    sample.write_text("Server host: corp.internal\n", encoding="utf-8")

    payload = api_server._hunt_command({"directory": str(directory)})
    assert payload["count"] >= 1

    filtered = api_server._hunt_command({"directory": str(directory), "pattern": "corp"})
    assert filtered["count"] == payload["count"]

    stripped = api_server._hunt_command({"directory": str(directory), "pattern": "missing"})
    assert stripped["count"] == 0


def test_handle_routes_commands() -> None:
    assert api_server.handle({"cmd": "ping"}) == {"status": "pong"}
    with pytest.raises(ValueError):
        api_server.handle({"cmd": "unknown"})


def test_main_process_loop_handles_shutdown(monkeypatch: pytest.MonkeyPatch) -> None:
    commands = io.StringIO(
        json.dumps({"cmd": "ping"}) + "\n" + json.dumps({"cmd": "shutdown"}) + "\n"
    )
    output = io.StringIO()

    monkeypatch.setattr(api_server.sys, "stdin", commands)
    monkeypatch.setattr(api_server.sys, "stdout", output)

    api_server.main()

    lines = [json.loads(line) for line in output.getvalue().splitlines()]
    assert lines[0]["ok"] is True and lines[0]["result"]["status"] == "pong"
    assert lines[1]["result"]["status"] == "bye"


def test_profile_commands(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.chdir(tmp_path)
    profile_payload = {
        "name": "demo",
        "sources": [str(tmp_path / "config.json")],
    }
    (tmp_path / "config.json").write_text("{}", encoding="utf-8")

    save_result = api_server.handle({"cmd": "profile-save", "profile": profile_payload})
    assert save_result["profile"]["name"] == "demo"

    list_result = api_server.handle({"cmd": "profile-list"})
    assert list_result["profiles"]

    run_result = api_server.handle({"cmd": "profile-run", "profile": profile_payload, "timestamp": "20250101T120000Z"})
    assert run_result["timestamp"] == "20250101T120000Z"
    assert run_result["profile"]["name"] == "demo"
