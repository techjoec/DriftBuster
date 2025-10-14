from __future__ import annotations

import json
from pathlib import Path

import pytest

from driftbuster import cli


def test_cli_table_output(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    target_dir = tmp_path / "configs"
    target_dir.mkdir()
    sample = target_dir / "appsettings.json"
    sample.write_text('{"Logging": {"LogLevel": {"Default": "Information"}}}', encoding="utf-8")

    exit_code = cli.main([str(target_dir)])
    captured = capsys.readouterr()

    assert exit_code == 0
    assert "Path" in captured.out
    assert "appsettings.json" in captured.out
    assert "json" in captured.out


def test_cli_json_output(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    sample = tmp_path / "config.json"
    sample.write_text('{"key": "value"}', encoding="utf-8")

    exit_code = cli.main([str(sample), "--json"])
    captured = capsys.readouterr()

    assert exit_code == 0
    payload = [json.loads(line) for line in captured.out.strip().splitlines() if line]
    assert payload
    assert payload[0]["format"] == "json"


def test_cli_reports_missing_path(capsys: pytest.CaptureFixture[str]) -> None:
    with pytest.raises(SystemExit) as exc:
        cli.main(["/path/does/not/exist"])

    assert exc.value.code == 2
    assert "Path does not exist" in capsys.readouterr().err
