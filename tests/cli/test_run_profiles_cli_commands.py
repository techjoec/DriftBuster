from __future__ import annotations

import argparse
import json
from pathlib import Path

import pytest

from driftbuster import run_profiles
from driftbuster.run_profiles_cli import _create, _list_profiles, _parse_options, _run, _show, build_parser, main


def test_parse_options_validates_format() -> None:
    options = _parse_options(["foo=bar", "baz = qux "])
    assert options == {"foo": "bar", "baz": "qux"}

    with pytest.raises(SystemExit):
        _parse_options(["invalid"])


def test_list_profiles_reports_empty(tmp_path, capsys) -> None:
    args = argparse.Namespace(base_dir=tmp_path)
    exit_code = _list_profiles(args)
    assert exit_code == 0
    assert "No profiles" in capsys.readouterr().out


def test_create_list_show_and_run(tmp_path, capsys) -> None:
    source = tmp_path / "config.ini"
    source.write_text("content", encoding="utf-8")

    base_dir = tmp_path

    args = argparse.Namespace(
        name="demo",
        description="Example",
        source=[str(source)],
        baseline=str(source),
        option=["key=value"],
        base_dir=base_dir,
        secret_ignore_rules=["Skip"],
        secret_ignore_patterns=["ALLOW"],
    )

    exit_code = _create(args)
    assert exit_code == 0

    list_args = argparse.Namespace(base_dir=base_dir)
    _list_profiles(list_args)
    listed = capsys.readouterr().out
    assert "demo" in listed

    show_args = argparse.Namespace(name="demo", base_dir=base_dir)
    _show(show_args)
    shown = capsys.readouterr().out
    payload = json.loads(shown)
    assert payload["name"] == "demo"
    assert payload["options"] == {"key": "value"}
    assert payload["secret_scanner"] == {
        "ignore_rules": ["Skip"],
        "ignore_patterns": ["ALLOW"],
    }

    profile_file = base_dir / "Profiles" / "demo" / "profile.json"
    run_args = argparse.Namespace(
        profile=str(profile_file),
        name=None,
        base_dir=base_dir,
        save=True,
        timestamp="20240101T000000Z",
        secret_ignore_rules=[],
        secret_ignore_patterns=[],
    )
    exit_code = _run(run_args)
    assert exit_code == 0
    output = capsys.readouterr().out
    assert "Run saved" in output


def test_run_command_loads_by_name(tmp_path, capsys) -> None:
    source = tmp_path / "file.txt"
    source.write_text("payload", encoding="utf-8")

    profile = run_profiles.RunProfile(name="cli", sources=[str(source)])
    run_profiles.save_profile(profile, base_dir=tmp_path)

    args = argparse.Namespace(
        profile=None,
        name="cli",
        base_dir=tmp_path,
        save=False,
        timestamp=None,
        secret_ignore_rules=[],
        secret_ignore_patterns=[],
    )
    _run(args)
    output = capsys.readouterr().out
    assert "Files collected" in output


def test_main_entrypoint_dispatches(tmp_path, monkeypatch, capsys) -> None:
    parser = build_parser()
    args = parser.parse_args(
        [
            "--base-dir",
            str(tmp_path),
            "create",
            "--name",
            "entry",
            "--source",
            str(tmp_path / "dummy"),
        ]
    )

    dummy = tmp_path / "dummy"
    dummy.write_text("data", encoding="utf-8")

    monkeypatch.setattr(argparse.ArgumentParser, "parse_args", lambda self, _: args)

    result = main(["ignored"])
    assert result == 0
    capsys.readouterr()  # drain stdout


def test_main_returns_error_when_no_command(tmp_path, monkeypatch) -> None:
    parser = build_parser()
    empty_args = argparse.Namespace(func=None)
    monkeypatch.setattr(argparse.ArgumentParser, "parse_args", lambda self, _: empty_args)
    exit_code = main([])
    assert exit_code == 1


def test_run_profiles_module_main(tmp_path, monkeypatch) -> None:
    called: dict[str, bool] = {"ran": False}

    def fake_main(argv):
        called["ran"] = True
        assert argv == ["list"]
        return 2

    monkeypatch.setattr("driftbuster.run_profiles_cli.main", fake_main)
    exit_code = run_profiles.main(["list"])
    assert exit_code == 2
    assert called["ran"] is True
