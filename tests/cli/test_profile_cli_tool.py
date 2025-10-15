from __future__ import annotations

import argparse
import json
from pathlib import Path

import pytest

from driftbuster.core.profiles import ConfigurationProfile, ProfileConfig, ProfileStore
from driftbuster.profile_cli import (
    _build_bridge_payload,
    _handle_diff,
    _handle_hunt_bridge,
    _handle_summary,
    _store_from_payload,
    _write_json,
    parse_args,
)


def test_store_from_payload_fallback(monkeypatch) -> None:
    payload = {
        "profiles": [
            {
                "name": "demo",
                "configs": [
                    {
                        "id": "cfg1",
                        "path": "config\\file.txt",
                        "tags": ["prod"],
                    }
                ],
            }
        ]
    }

    def broken(cls, *_args, **_kwargs):
        raise RuntimeError("broken path")

    monkeypatch.setattr(ProfileStore, "from_dict", classmethod(broken))
    store = _store_from_payload(payload)
    configs = store.find_config("cfg1")
    assert configs
    config = configs[0].config
    assert config.path.replace("\\", "/") == "config/file.txt"
    assert "prod" in config.tags


def test_write_json_writes_stdout(tmp_path, capsys) -> None:
    payload = {"value": 1}
    _write_json(payload, output=None, indent=0, sort_keys=True)
    assert capsys.readouterr().out.strip() == json.dumps(payload, sort_keys=True)

    output = tmp_path / "out.json"
    _write_json(payload, output=output, indent=2, sort_keys=False)
    assert json.loads(output.read_text()) == payload


def test_handle_summary_and_diff(tmp_path: Path, capsys) -> None:
    store_payload = {
        "profiles": [
            {
                "name": "demo",
                "description": None,
                "tags": [],
                "metadata": {},
                "configs": [{"id": "cfg1"}],
            }
        ]
    }
    store_path = tmp_path / "store.json"
    store_path.write_text(json.dumps(store_payload))

    args = argparse.Namespace(store=store_path, output=None, indent=2, sort_keys=True)
    assert _handle_summary(args) == 0
    summary_output = json.loads(capsys.readouterr().out)
    assert summary_output["total_profiles"] == 1

    baseline = tmp_path / "baseline.json"
    current = tmp_path / "current.json"
    baseline.write_text(json.dumps({"total_profiles": 1, "total_configs": 1, "profiles": []}))
    current.write_text(json.dumps({"total_profiles": 2, "total_configs": 3, "profiles": []}))
    diff_args = argparse.Namespace(
        baseline=baseline,
        current=current,
        output=None,
        indent=0,
        sort_keys=False,
    )
    assert _handle_diff(diff_args) == 0
    diff_output = json.loads(capsys.readouterr().out)
    assert diff_output["totals"]["current"]["profiles"] == 2


def test_handle_hunt_bridge(tmp_path: Path, capsys) -> None:
    store_payload = {
        "profiles": [
            {
                "name": "demo",
                "configs": [
                    {
                        "id": "cfg1",
                        "path": "logs/app.log",
                    }
                ],
            }
        ]
    }
    store_path = tmp_path / "store.json"
    store_path.write_text(json.dumps(store_payload))

    hunts = [
        {"path": str(tmp_path / "logs/app.log"), "relative_path": "logs/app.log", "rule": {"name": "rule"}},
        {"path": str(tmp_path / "missing.log"), "rule": {"name": "rule"}},
    ]
    hunt_path = tmp_path / "hunts.json"
    hunt_path.write_text(json.dumps(hunts))

    args = argparse.Namespace(
        store=store_path,
        hunt=hunt_path,
        tags=["demo"],
        root=tmp_path,
        output=None,
        indent=2,
        sort_keys=False,
    )
    assert _handle_hunt_bridge(args) == 0
    bridge = json.loads(capsys.readouterr().out)
    assert bridge["items"][0]["profiles"]
    assert bridge["items"][1]["relative_path"] == "missing.log"


def test_build_bridge_payload_relative_resolution(tmp_path: Path) -> None:
    store = ProfileStore([ConfigurationProfile(name="demo", configs=())])
    payload = _build_bridge_payload(
        store,
        [{"path": str(tmp_path / "extra.txt"), "rule": {"name": "rule"}}],
        tags=None,
        root=None,
    )
    assert payload["items"][0]["relative_path"] == "extra.txt"


def test_parse_args_requires_command() -> None:
    parser = parse_args(["summary", "store.json"])
    assert parser.command == "summary"

    with pytest.raises(SystemExit):
        parse_args([])
