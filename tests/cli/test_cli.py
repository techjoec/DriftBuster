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


def test_relative_path_falls_back_when_outside_root(tmp_path: Path) -> None:
    root = tmp_path / "root"
    root.mkdir()
    outside = tmp_path / "other" / "config.json"
    outside.parent.mkdir()
    outside.write_text("{}", encoding="utf-8")

    result = cli._relative_path(root, outside)

    assert result == outside.as_posix()


def test_ellipsize_handles_small_limits() -> None:
    assert cli._ellipsize("abcdef", 1) == "a"
    assert cli._ellipsize("abcdef", 0) == ""


def test_main_returns_code_when_parser_error_is_suppressed(monkeypatch: pytest.MonkeyPatch) -> None:
    recorded: dict[str, str] = {}

    class DummyArgs:
        path = Path("/missing/path")
        glob = "**/*"
        sample_size = None
        json = False

    class DummyParser:
        def parse_args(self, _argv: list[str] | None = None) -> DummyArgs:
            return DummyArgs()

        def error(self, message: str) -> None:  # noqa: D401 - stub for testing
            recorded["message"] = message

    monkeypatch.setattr(cli, "_build_parser", lambda: DummyParser())

    def raise_missing(*_args: object, **_kwargs: object) -> list[tuple[Path, None]]:
        raise FileNotFoundError("Path does not exist: /missing/path")

    monkeypatch.setattr(cli, "_iter_scan_results", raise_missing)

    exit_code = cli.main(["/missing/path"])

    assert exit_code == 2
    assert recorded["message"].startswith("Path does not exist")
