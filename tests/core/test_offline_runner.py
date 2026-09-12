from __future__ import annotations

import json
import os
import re
import zipfile
from pathlib import Path
from typing import Any

import pytest

from driftbuster import offline_runner
from driftbuster.offline_runner import (
    OfflineCollectionSource,
    OfflineRunnerConfig,
    OfflineRunnerProfile,
    OfflineRunnerSettings,
    SecretDetectionContext,
    SecretDetectionRule,
    _build_secret_context,
    _compile_ruleset_from_mapping,
    _manifest_secret_scanner,
    _secret_option_values,
)


def _write_config(tmp_path: Path, payload: dict) -> Path:
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return config_path


def _read_manifest_from_package(package_path: Path | None) -> dict:
    assert package_path is not None
    with zipfile.ZipFile(package_path, "r") as archive, archive.open("manifest.json") as handle:
        return json.load(handle)


def _read_runner_log_from_package(package_path: Path | None) -> str:
    assert package_path is not None
    with zipfile.ZipFile(package_path, "r") as archive, archive.open("logs/runner.log") as handle:
        return handle.read().decode("utf-8")


def _build_config(
    tmp_path: Path,
    *,
    profile: dict[str, Any],
    runner: dict[str, Any] | None = None,
    metadata: dict[str, Any] | None = None,
) -> OfflineRunnerConfig:
    payload: dict[str, Any] = {
        "profile": profile,
        "runner": runner or {"output_directory": str(tmp_path / "out"), "cleanup_staging": False},
    }
    if metadata is not None:
        payload["metadata"] = metadata
    return OfflineRunnerConfig.from_dict(payload)


def _create_text_file(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def _read_collected_file_from_package(
    package_path: Path | None, relative_path: Path
) -> str:
    assert package_path is not None
    archive_path = f"data/{relative_path.as_posix()}"
    with zipfile.ZipFile(package_path, "r") as archive, archive.open(archive_path) as handle:
        return handle.read().decode("utf-8")


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
    first_source, second_source = config.profile.sources
    assert isinstance(first_source, offline_runner.OfflineCollectionSource)
    assert isinstance(second_source, offline_runner.OfflineCollectionSource)
    assert first_source.path.endswith("file.txt")
    assert second_source.alias == "dir"
    assert second_source.optional is True


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
    assert result.manifest_path is None
    assert result.log_path is None
    assert result.staging_dir is None
    assert len(result.files) >= 2

    archive_contents: set[str] = set()
    with zipfile.ZipFile(result.package_path, "r") as archive:
        archive_contents = set(archive.namelist())

    assert any(name.startswith("data/") for name in archive_contents)
    assert "manifest.json" in archive_contents
    assert "config.json" in archive_contents or any(
        name.endswith("config.json") for name in archive_contents
    )

    manifest = _read_manifest_from_package(result.package_path)
    log_contents = _read_runner_log_from_package(result.package_path)
    assert manifest["schema"] == offline_runner.MANIFEST_SCHEMA
    assert manifest["profile"]["name"] == "windows_baseline"
    assert manifest["metadata"]["request_id"] == "abc-123"
    assert any(entry["relative_path"].endswith("firewall.log") for entry in manifest["files"])
    assert manifest["package"]["cleanup_staging"] is True
    assert "offline collection finished" in log_contents


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
    assert result.manifest_path is None
    manifest = _read_manifest_from_package(result.package_path)
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


def test_execute_offline_run_deduplicates_recursive_glob_matches(tmp_path: Path) -> None:
    source_root = tmp_path / "source"
    source_root.mkdir()
    nested_dir = source_root / "nested"
    nested_dir.mkdir()
    (source_root / "root.log").write_text("root", encoding="utf-8")
    (nested_dir / "child.log").write_text("child", encoding="utf-8")

    config_payload = {
        "profile": {
            "name": "recursive-glob",
            "sources": [
                {"path": f"{source_root}/**/*"},
            ],
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)

    collected_paths = [file.relative_path.as_posix() for file in result.files]
    assert len(collected_paths) == len(set(collected_paths))
    assert any(path.endswith("root.log") for path in collected_paths)
    assert any(path.endswith("child.log") for path in collected_paths)


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


def test_execute_offline_run_scrubs_secret_lines(tmp_path: Path) -> None:
    secret_file = tmp_path / "secrets.txt"
    secret_file.write_text(
        "safe line\npassword = SUPERSECRET123456\nkeep me\n",
        encoding="utf-8",
    )

    config_payload = {
        "profile": {
            "name": "secret-scan",
            "sources": [str(secret_file)],
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
            "include_logs": True,
            "include_manifest": True,
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)

    log_contents = _read_runner_log_from_package(result.package_path)
    assert "secret candidate redacted" in log_contents

    collected_file = next(entry for entry in result.files if entry.source == str(secret_file))
    collected_text = _read_collected_file_from_package(result.package_path, collected_file.relative_path)
    assert "SUPERSECRET123456" not in collected_text
    assert collected_text.splitlines() == ["safe line", "[SECRET]", "keep me"]

    manifest = _read_manifest_from_package(result.package_path)
    assert "secret_scanner" in manifest["profile"]
    profile_scanner = manifest["profile"]["secret_scanner"]
    assert profile_scanner["ruleset_version"] == "2024-06-01"
    assert "rules" not in profile_scanner
    secrets = manifest["secrets"]
    assert secrets["ruleset_version"] == "2024-06-01"
    assert secrets["ignored_rules"] == []
    assert secrets["ignored_patterns"] == []
    assert len(secrets["findings"]) == 1
    finding = secrets["findings"][0]
    assert finding["rule"] in {"PasswordAssignment", "GenericApiToken"}
    assert finding["snippet"].endswith("[SECRET]") or "[SECRET]" in finding["snippet"]


def test_execute_offline_run_honours_secret_ignore_patterns(tmp_path: Path) -> None:
    secret_file = tmp_path / "allowlist.txt"
    secret_file.write_text(
        "password = ALLOW_ME",
        encoding="utf-8",
    )

    config_payload = {
        "profile": {
            "name": "secret-ignore",
            "sources": [str(secret_file)],
            "secret_scanner": {
                "ignore_patterns": ["ALLOW_ME"],
            },
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
            "include_logs": True,
            "include_manifest": True,
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)

    log_contents = _read_runner_log_from_package(result.package_path)
    assert "secret candidate redacted" not in log_contents

    collected_file = next(entry for entry in result.files if entry.source == str(secret_file))
    collected_text = _read_collected_file_from_package(result.package_path, collected_file.relative_path)
    assert "ALLOW_ME" in collected_text

    manifest = _read_manifest_from_package(result.package_path)
    secrets = manifest["secrets"]
    assert secrets["findings"] == []
    assert "ALLOW_ME" in secrets["ignored_patterns"]
    assert "ALLOW_ME" in manifest["profile"]["secret_scanner"]["ignore_patterns"]
    assert "rules" not in manifest["profile"]["secret_scanner"]


def test_execute_offline_run_prefers_ruleset_from_config(tmp_path: Path) -> None:
    secret_file = tmp_path / "custom.txt"
    secret_file.write_text(
        "token = TOTALLY_CUSTOM_SECRET",
        encoding="utf-8",
    )

    config_payload = {
        "profile": {
            "name": "secret-config",
            "sources": [str(secret_file)],
            "secret_scanner": {
                "ruleset": {
                    "version": "custom-1",
                    "rules": [
                        {
                            "name": "CustomToken",
                            "pattern": "TOTALLY_CUSTOM_SECRET",
                            "flags": "",
                        }
                    ],
                }
            },
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
            "include_logs": True,
            "include_manifest": True,
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)

    manifest = _read_manifest_from_package(result.package_path)
    secrets = manifest["secrets"]
    assert secrets["ruleset_version"] == "custom-1"
    assert secrets["findings"]
    profile_scanner = manifest["profile"]["secret_scanner"]
    assert profile_scanner["ruleset_version"] == "custom-1"
    assert "rules" not in profile_scanner


def test_execute_offline_run_retains_staging_when_cleanup_disabled(tmp_path: Path) -> None:
    sample = tmp_path / "artifact.txt"
    sample.write_text("data", encoding="utf-8")

    config_payload = {
        "profile": {
            "name": "no-cleanup",
            "sources": [str(sample)],
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
            "cleanup_staging": False,
            "include_manifest": True,
            "include_logs": True,
        },
    }
    config_path = _write_config(tmp_path, config_payload)

    result = offline_runner.execute_config_path(config_path)

    assert result.staging_dir is not None
    assert result.staging_dir.exists()
    assert result.manifest_path is not None
    assert result.manifest_path.exists()
    assert result.log_path is not None
    assert result.log_path.exists()


def test_compile_ruleset_from_mapping_handles_invalid_entries() -> None:
    assert _compile_ruleset_from_mapping(None) is None
    assert _compile_ruleset_from_mapping({"rules": "invalid"}) is None

    payload = {
        "version": "custom",
        "rules": [
            {"name": "Valid", "pattern": "secret", "flags": "i"},
            {"name": "Broken", "pattern": "["},
        ],
    }
    compiled = _compile_ruleset_from_mapping(payload)
    assert compiled is not None
    rules, version = compiled
    assert version == "custom"
    assert len(rules) == 1
    assert isinstance(rules[0], SecretDetectionRule)


def test_secret_option_values_and_manifest_helpers() -> None:
    assert _secret_option_values("a, b ; c") == ("a", "b", "c")
    assert _secret_option_values(["x", None, " y "]) == ("x", "y")

    context = SecretDetectionContext(
        rules=(),
        version="v1",
        ignore_rules=frozenset({"Skip"}),
        ignore_patterns=(re.compile("SKIP"),),
        ignore_pattern_text=("SKIP",),
        findings=[],
        rules_loaded=True,
    )
    manifest = _manifest_secret_scanner(
        {"secret_ignore_rules": "Skip"},
        {"ignore_patterns": ["SKIP"]},
        context,
    )
    assert manifest["ruleset_version"] == "v1"
    assert manifest["ignore_rules"] == ["Skip"]
    assert manifest["ignore_patterns"] == ["SKIP"]


def test_build_secret_context_prefers_inline_rules(tmp_path: Path) -> None:
    payload = {
        "ruleset": {
            "version": "inline",
            "rules": [
                {"name": "Token", "pattern": "VALUE"},
            ],
        },
        "ignore_rules": ["Token"],
    }
    context = _build_secret_context(
        {"secret_ignore_patterns": ["ALLOW"]},
        payload,
    )
    assert context.version == "inline"
    assert context.rules_loaded is True
    assert context.ignore_rules == frozenset({"Token"})
    assert "ALLOW" in context.ignore_pattern_text


def test_offline_collection_source_validations() -> None:
    with pytest.raises(ValueError):
        OfflineCollectionSource.from_dict({})

    source = OfflineCollectionSource.from_dict({"path": "~/data", "alias": "  ", "exclude": "*.tmp"})
    assert source.alias is None
    assert source.exclude == ("*.tmp",)

    root_source = OfflineCollectionSource.from_dict({"path": "/"})
    assert root_source.destination_name(fallback_index=7) == "source_07"


def test_offline_runner_profile_validations() -> None:
    payload = {"name": ""}
    with pytest.raises(ValueError):
        OfflineRunnerProfile.from_dict(payload)

    payload = {"name": "demo", "sources": ["/tmp/a"], "baseline": "missing"}
    with pytest.raises(ValueError):
        OfflineRunnerProfile.from_dict(payload)

    payload = {"name": "demo", "sources": ["/tmp/a"], "options": "invalid"}
    with pytest.raises(ValueError):
        OfflineRunnerProfile.from_dict(payload)

    payload = {"name": "demo", "sources": ["/tmp/a"], "secret_scanner": "invalid"}
    with pytest.raises(ValueError):
        OfflineRunnerProfile.from_dict(payload)

    with pytest.raises(ValueError):
        OfflineRunnerProfile.from_dict({"name": "demo"})

    profile = OfflineRunnerProfile.from_dict({"name": "tags", "sources": ["/tmp/a"], "tags": "prod"})
    assert profile.tags == ("prod",)


def test_offline_runner_settings_negative_limit_and_blank_package() -> None:
    settings = OfflineRunnerSettings.from_dict({"package_name": "  "})
    assert settings.package_name is None

    with pytest.raises(ValueError):
        OfflineRunnerSettings.from_dict({"max_total_bytes": -1})


def test_offline_runner_config_metadata_validation() -> None:
    payload = {"profile": {"name": "demo", "sources": ["/tmp/a"]}, "metadata": "invalid"}
    with pytest.raises(ValueError):
        OfflineRunnerConfig.from_dict(payload)

    with pytest.raises(ValueError):
        OfflineRunnerConfig.from_dict({})

    with pytest.raises(TypeError):
        OfflineRunnerConfig.from_dict("invalid")  # type: ignore[arg-type]


def test_offline_runner_config_default_package_name(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(offline_runner, "_timestamp", lambda: "20230101T000000Z")
    config = OfflineRunnerConfig.from_dict({"profile": {"name": "Demo", "sources": ["/tmp/a"]}})
    assert config.default_package_name() == "Demo-20230101T000000Z"


def test_execute_config_logs_when_secret_rules_missing(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    file_path = tmp_path / "data.txt"
    _create_text_file(file_path, "content")

    def fake_context(_options: Any, _secret: Any) -> SecretDetectionContext:
        return SecretDetectionContext(
            rules=(),
            version="v",
            ignore_rules=frozenset(),
            ignore_patterns=(),
            ignore_pattern_text=(),
            findings=[],
            rules_loaded=False,
        )

    monkeypatch.setattr(offline_runner, "_build_secret_context", fake_context)

    config = _build_config(
        tmp_path,
        profile={"name": "demo", "sources": [str(file_path)]},
        runner={"output_directory": str(tmp_path / "out"), "cleanup_staging": False},
    )

    result = offline_runner.execute_config(config, config_path=None, base_dir=None, timestamp="20230101T010101Z")

    assert result.log_path is not None
    log_contents = result.log_path.read_text(encoding="utf-8")
    assert "secret detection rules unavailable" in log_contents


def test_execute_config_optional_missing_file(tmp_path: Path) -> None:
    existing = tmp_path / "present.txt"
    _create_text_file(existing, "data")

    profile = {
        "name": "optional",
        "sources": [
            str(existing),
            {"path": str(tmp_path / "absent.txt"), "optional": True, "alias": "missing"},
        ],
    }
    config = _build_config(tmp_path, profile=profile)

    result = offline_runner.execute_config(config, base_dir=tmp_path)
    assert result.manifest_path is not None
    manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
    summary = next(item for item in manifest["sources"] if item["alias"] == "missing")
    assert summary["skipped"] is True
    assert summary["reason"] == "missing"


def test_execute_config_required_glob_without_matches(tmp_path: Path) -> None:
    profile = {
        "name": "required",
        "sources": [str(tmp_path / "missing" / "*.log")],
    }
    config = _build_config(tmp_path, profile=profile)

    with pytest.raises(FileNotFoundError):
        offline_runner.execute_config(config, base_dir=tmp_path)


def test_execute_config_skips_symlink(tmp_path: Path) -> None:
    real_file = tmp_path / "real.txt"
    _create_text_file(real_file, "data")
    symlink = tmp_path / "link.txt"
    os.symlink(real_file, symlink)

    profile = {
        "name": "symlinks",
        "sources": [str(real_file), str(symlink)],
    }
    config = _build_config(tmp_path, profile=profile)

    result = offline_runner.execute_config(config, base_dir=tmp_path)
    paths = [entry.source for entry in result.files]
    assert any(path.endswith("real.txt") for path in paths)
    assert all(path != str(symlink) for path in paths)


def test_execute_config_respects_max_total_bytes(tmp_path: Path) -> None:
    file_path = tmp_path / "data.txt"
    _create_text_file(file_path, "content")

    profile = {"name": "limit", "sources": [str(file_path)]}
    settings = {"output_directory": str(tmp_path / "out"), "max_total_bytes": 1}
    config = _build_config(tmp_path, profile=profile, runner=settings)

    with pytest.raises(ValueError):
        offline_runner.execute_config(config, base_dir=tmp_path)


def test_execute_config_directory_respects_max_total_bytes(tmp_path: Path) -> None:
    directory = tmp_path / "payload"
    directory.mkdir()
    _create_text_file(directory / "data.txt", "content")

    profile = {"name": "limit-dir", "sources": [str(directory)]}
    settings = {"output_directory": str(tmp_path / "out"), "max_total_bytes": 1}
    config = _build_config(tmp_path, profile=profile, runner=settings)

    with pytest.raises(ValueError):
        offline_runner.execute_config(config, base_dir=tmp_path)


def test_execute_config_excludes_single_file(tmp_path: Path) -> None:
    file_path = tmp_path / "secret.txt"
    _create_text_file(file_path, "content")

    profile = {
        "name": "exclude",
        "sources": [{"path": str(file_path), "exclude": ["secret.txt"]}],
    }
    config = _build_config(tmp_path, profile=profile)

    result = offline_runner.execute_config(config, base_dir=tmp_path)
    assert all(entry.relative_path.as_posix() != "secret.txt" for entry in result.files)


def test_execute_config_appends_zip_extension(tmp_path: Path) -> None:
    file_path = tmp_path / "file.log"
    _create_text_file(file_path, "data")

    profile = {"name": "archive", "sources": [str(file_path)]}
    settings = {
        "output_directory": str(tmp_path / "out"),
        "package_name": "artifact",
        "compress": True,
        "cleanup_staging": True,
    }
    config = _build_config(tmp_path, profile=profile, runner=settings)

    result = offline_runner.execute_config(config, base_dir=tmp_path)
    assert result.package_path is not None
    assert result.package_path.name.endswith(".zip")
