from __future__ import annotations

import json
from pathlib import Path

import pytest

from driftbuster import profile_cli


def test_profile_cli_summary(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    store_payload = {
        "profiles": [
            {
                "name": "prod",
                "configs": [
                    {
                        "id": "cfg1",
                        "path": "configs/app.config",
                    }
                ],
            }
        ]
    }
    store_path = tmp_path / "store.json"
    store_path.write_text(json.dumps(store_payload), encoding="utf-8")

    exit_code = profile_cli.main(["summary", str(store_path)])
    captured = capsys.readouterr()

    assert exit_code == 0
    data = json.loads(captured.out)
    assert data["total_profiles"] == 1
    assert data["total_configs"] == 1


def test_profile_cli_diff(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    baseline = tmp_path / "baseline.json"
    current = tmp_path / "current.json"
    baseline.write_text(json.dumps({"total_profiles": 1, "total_configs": 1, "profiles": []}), encoding="utf-8")
    current.write_text(json.dumps({"total_profiles": 2, "total_configs": 3, "profiles": []}), encoding="utf-8")

    exit_code = profile_cli.main(["diff", str(baseline), str(current)])
    captured = capsys.readouterr()

    assert exit_code == 0
    diff = json.loads(captured.out)
    assert diff["totals"]["current"]["profiles"] == 2


def test_profile_cli_hunt_bridge(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    store_payload = {
        "profiles": [
            {
                "name": "prod",
                "tags": ["prod"],
                "configs": [
                    {
                        "id": "cfg1",
                        "path": "configs/appsettings.json",
                        "expected_format": "json",
                        "expected_variant": "structured-settings-json",
                    }
                ],
            }
        ]
    }
    hunts_payload = [
        {
            "rule": {"name": "server-name", "description": "", "token_name": "server"},
            "relative_path": "configs/appsettings.json",
            "path": "dummy",
            "line_number": 1,
            "excerpt": "server: dev",
        }
    ]

    store_path = tmp_path / "store.json"
    hunt_path = tmp_path / "hunts.json"
    store_path.write_text(json.dumps(store_payload), encoding="utf-8")
    hunt_path.write_text(json.dumps(hunts_payload), encoding="utf-8")

    exit_code = profile_cli.main([
        "hunt-bridge",
        str(store_path),
        str(hunt_path),
        "--tag",
        "prod",
    ])
    captured = capsys.readouterr()

    assert exit_code == 0
    payload = json.loads(captured.out)
    assert payload["items"][0]["profiles"][0]["profile"] == "prod"
