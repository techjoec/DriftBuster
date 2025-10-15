from __future__ import annotations

import json
from pathlib import Path
import zipfile

import pytest

from driftbuster import offline_runner


def _write_config(tmp_path: Path, payload: dict) -> Path:
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return config_path


def test_load_config_accepts_string_and_object_sources(tmp_path: Path) -> None:
    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "demo",
            "sources": [
                str(tmp_path / "file.txt"),
                {"path": str(tmp_path / "dir"), "alias": "dir", "optional": True},
            ],
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    config = offline_runner.load_config(config_path)
    assert config.profile.name == "demo"
    assert len(config.profile.sources) == 2
    assert config.profile.sources[0].path.endswith("file.txt")
    assert config.profile.sources[1].alias == "dir"
    assert config.profile.sources[1].optional is True


def test_execute_offline_run_collects_files(tmp_path: Path) -> None:
    logs_dir = tmp_path / "logs"
    logs_dir.mkdir()
    sample_log = logs_dir / "firewall.log"
    sample_log.write_text("entry", encoding="utf-8")

    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "version": "1.0",
        "profile": {
            "name": "windows_baseline",
            "description": "Collect baseline logs",
            "sources": [
                {"path": str(sample_log)},
                {
                    "path": str(logs_dir),
                    "alias": "logs",
                    "exclude": ["*.tmp"],
                },
            ],
            "tags": ["windows", "baseline"],
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
            "compress": True,
            "include_config": True,
            "include_logs": True,
            "include_manifest": True,
        },
        "metadata": {"request_id": "abc-123"},
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)

    assert result.package_path is not None
    assert result.package_path.exists()
    assert result.manifest_path is not None
    assert result.manifest_path.exists()
    assert result.log_path is not None
    assert result.log_path.exists()
    assert len(result.files) >= 2

    archive_contents: set[str] = set()
    with zipfile.ZipFile(result.package_path, "r") as archive:
        archive_contents = set(archive.namelist())

    assert any(name.startswith("data/") for name in archive_contents)
    assert "manifest.json" in archive_contents
    assert "config.json" in archive_contents or any(
        name.endswith("config.json") for name in archive_contents
    )

    manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
    assert manifest["schema"] == offline_runner.MANIFEST_SCHEMA
    assert manifest["profile"]["name"] == "windows_baseline"
    assert manifest["metadata"]["request_id"] == "abc-123"
    assert any(entry["relative_path"].endswith("firewall.log") for entry in manifest["files"])


def test_execute_offline_run_handles_optional_source(tmp_path: Path) -> None:
    existing = tmp_path / "present.log"
    existing.write_text("log", encoding="utf-8")

    config_payload = {
        "profile": {
            "name": "optional",
            "sources": [
                {"path": str(existing)},
                {
                    "path": str(tmp_path / "missing" / "*.log"),
                    "alias": "missing",
                    "optional": True,
                },
            ],
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)

    assert any(file.alias == "missing" for file in result.files) is False
    assert result.manifest_path is not None
    manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
    summary = next(entry for entry in manifest["sources"] if entry["alias"] == "missing")
    assert summary["skipped"] is True
    assert summary["reason"] == "no-matches"


def test_execute_offline_run_missing_required_source(tmp_path: Path) -> None:
    config_payload = {
        "profile": {
            "name": "missing-required",
            "sources": [str(tmp_path / "missing.txt")],
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    with pytest.raises(FileNotFoundError):
        offline_runner.execute_config_path(config_path)


def test_execute_offline_run_respects_exclude_patterns(tmp_path: Path) -> None:
    data_dir = tmp_path / "data"
    data_dir.mkdir()
    (data_dir / "keep.log").write_text("keep", encoding="utf-8")
    (data_dir / "ignore.tmp").write_text("ignore", encoding="utf-8")

    config_payload = {
        "profile": {
            "name": "excludes",
            "sources": [
                {
                    "path": str(data_dir),
                    "exclude": ["*.tmp"],
                }
            ],
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)
    paths = [file.relative_path.as_posix() for file in result.files]
    assert all("ignore.tmp" not in path for path in paths)
    assert any(path.endswith("keep.log") for path in paths)


def test_execute_offline_run_enforces_max_total_bytes(tmp_path: Path) -> None:
    source = tmp_path / "large.bin"
    source.write_bytes(b"0" * 1024)

    config_payload = {
        "profile": {
            "name": "limits",
            "sources": [str(source)],
        },
        "runner": {
            "max_total_bytes": 10,
            "output_directory": str(tmp_path / "out"),
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    with pytest.raises(ValueError, match="max_total_bytes"):
        offline_runner.execute_config_path(config_path)
