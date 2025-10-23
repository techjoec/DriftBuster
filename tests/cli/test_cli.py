from __future__ import annotations

import json
import sqlite3
import sys
from pathlib import Path
from typing import Sequence

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


def _create_sqlite_db(path: Path) -> Path:
    connection = sqlite3.connect(path)
    try:
        cursor = connection.cursor()
        cursor.execute(
            "CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT)"
        )
        cursor.execute(
            "INSERT INTO accounts (email, secret) VALUES (?, ?)",
            ("alice@example.com", "token"),
        )
        connection.commit()
    finally:
        connection.close()
    return path


def test_cli_export_sql_generates_manifest(tmp_path: Path) -> None:
    database = _create_sqlite_db(tmp_path / "demo.sqlite")
    output_dir = tmp_path / "exports"

    exit_code = cli.main(
        [
            "export-sql",
            str(database),
            "--output-dir",
            str(output_dir),
            "--mask-column",
            "accounts.secret",
            "--hash-column",
            "accounts.email",
            "--placeholder",
            "[MASK]",
            "--hash-salt",
            "pepper",
        ]
    )

    assert exit_code == 0

    manifest_path = output_dir / "sql-manifest.json"
    assert manifest_path.exists()
    manifest = json.loads(manifest_path.read_text())
    assert manifest["exports"], "expected manifest export entries"
    export_entry = manifest["exports"][0]
    assert export_entry["dialect"] == "sqlite"
    assert export_entry["masked_columns"] == {"accounts": ["secret"]}
    assert export_entry["hashed_columns"] == {"accounts": ["email"]}

    snapshot_path = output_dir / "demo-sql-snapshot.json"
    assert snapshot_path.exists()


def test_main_consumes_sys_argv(monkeypatch: pytest.MonkeyPatch, tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    sample = tmp_path / "config.json"
    sample.write_text('{"enabled": true}', encoding="utf-8")

    monkeypatch.setattr(sys, "argv", ["driftbuster", str(sample), "--json"])

    exit_code = cli.main()
    captured = capsys.readouterr()

    assert exit_code == 0
    payload = [json.loads(line) for line in captured.out.strip().splitlines() if line]
    assert payload and payload[0]["detected"]
    assert payload[0]["format"] == "json"


def test_export_sql_console_main_forwards_arguments(monkeypatch: pytest.MonkeyPatch) -> None:
    recorded: dict[str, Sequence[str] | None] = {}

    def fake_main(argv: Sequence[str] | None = None) -> int:  # type: ignore[override]
        recorded["argv"] = argv
        return 0

    monkeypatch.setattr(cli, "main", fake_main)  # type: ignore[arg-type]
    monkeypatch.setattr(sys, "argv", ["driftbuster-export-sql", "demo.sqlite", "--limit", "10"])

    with pytest.raises(SystemExit) as exc:
        cli.export_sql_console_main()

    assert exc.value.code == 0
    assert recorded["argv"][0] == "export-sql"
    assert recorded["argv"][1:] == ["demo.sqlite", "--limit", "10"]
