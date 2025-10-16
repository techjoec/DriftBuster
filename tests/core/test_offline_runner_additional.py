from __future__ import annotations

import json
import os
import re
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
    _copy_with_secret_filter,
    _load_secret_rules,
    _looks_binary,
    _relative_path,
    _secret_option_values,
)


def _build_config(tmp_path: Path, *, profile: dict[str, Any], runner: dict[str, Any] | None = None, metadata: dict[str, Any] | None = None) -> OfflineRunnerConfig:
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


def test_compile_ruleset_from_mapping_handles_invalid_entries() -> None:
    assert _compile_ruleset_from_mapping("not-a-mapping") is None
    assert _compile_ruleset_from_mapping({"rules": "not-sequence"}) is None
    assert _compile_ruleset_from_mapping({"rules": 123}) is None

    payload = {
        "rules": [
            {"pattern": "missing-name"},
            {"name": "invalid", "pattern": "["},
            {"name": "token", "pattern": "secret", "flags": "i"},
        ],
        "version": "1.2.3",
    }

    compiled = _compile_ruleset_from_mapping(payload)

    assert compiled is not None
    rules, version = compiled
    assert version == "1.2.3"
    assert len(rules) == 1 and rules[0].pattern.pattern == "secret"


def test_load_secret_rules_handles_missing_resource(monkeypatch: pytest.MonkeyPatch) -> None:
    offline_runner._SECRET_RULE_CACHE = None
    offline_runner._SECRET_RULE_VERSION = None

    class MissingResource:
        def joinpath(self, _name: str) -> "MissingResource":
            raise FileNotFoundError

    monkeypatch.setattr(offline_runner.resources, "files", lambda _pkg: MissingResource())

    rules, version, loaded = _load_secret_rules()

    assert rules == ()
    assert version == "none"
    assert loaded is False


def test_load_secret_rules_handles_missing_file(monkeypatch: pytest.MonkeyPatch) -> None:
    offline_runner._SECRET_RULE_CACHE = None
    offline_runner._SECRET_RULE_VERSION = None

    class NoFile:
        def joinpath(self, _name: str) -> "NoFile":
            return self

        def open(self, *_args: Any, **_kwargs: Any):  # pragma: no cover - delegated
            raise FileNotFoundError

    monkeypatch.setattr(offline_runner.resources, "files", lambda _pkg: NoFile())

    rules, version, loaded = _load_secret_rules()

    assert rules == ()
    assert version == "none"
    assert loaded is False


def test_load_secret_rules_ignores_invalid_config(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    offline_runner._SECRET_RULE_CACHE = None
    offline_runner._SECRET_RULE_VERSION = None

    resource_path = tmp_path / "secret_rules.json"
    resource_path.write_text(json.dumps({"version": "2.1", "rules": [{"name": "", "pattern": ""}]}), encoding="utf-8")

    class Resource:
        def joinpath(self, name: str) -> "Resource":  # pragma: no cover - helper
            return self

        def open(self, mode: str = "r", encoding: str | None = None):
            return resource_path.open(mode, encoding=encoding)

    monkeypatch.setattr(offline_runner.resources, "files", lambda _pkg: Resource())

    rules, version, loaded = _load_secret_rules()
    assert rules == ()
    assert version == "2.1"
    assert loaded is True


def test_build_secret_context_handles_duplicates_and_invalid_patterns(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(offline_runner, "_load_secret_rules", lambda: ((), "embedded", False))

    context = _build_secret_context(
        {"secret_ignore_patterns": ["skip", "skip", "("], "secret_ignore_rules": "ignored"},
        {
            "ignore_patterns": ["skip"],
            "ignore_rules": ["ignored"],
            "ruleset": {"rules": [{"name": "custom", "pattern": "secret"}]},
        },
    )

    assert context.ignore_pattern_text == ("skip", "(")
    assert context.rules_loaded is True
    assert _secret_option_values(123) == ()


def test_looks_binary_handles_os_error(tmp_path: Path) -> None:
    directory = tmp_path / "dir"
    directory.mkdir()
    assert _looks_binary(directory) is False


def test_copy_with_secret_filter_without_rules(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    source = tmp_path / "source.txt"
    destination = tmp_path / "dest" / "file.txt"
    _create_text_file(source, "plain text")

    context = SecretDetectionContext(
        rules=(),
        version="v",
        ignore_rules=frozenset(),
        ignore_patterns=(),
        ignore_pattern_text=(),
        findings=[],
        rules_loaded=False,
    )

    size, digest = _copy_with_secret_filter(
        source,
        destination,
        display_path="file.txt",
        context=context,
        log=lambda _msg: None,
    )

    assert size == destination.stat().st_size
    assert len(digest) == 64


def test_copy_with_secret_filter_binary(tmp_path: Path) -> None:
    source = tmp_path / "binary.bin"
    destination = tmp_path / "dest" / "binary.bin"
    source.write_bytes(b"\x00\x01\x02")

    context = SecretDetectionContext(
        rules=(SecretDetectionRule("rule", re.compile(".")),),
        version="v",
        ignore_rules=frozenset(),
        ignore_patterns=(),
        ignore_pattern_text=(),
        findings=[],
        rules_loaded=True,
    )

    size, _digest = _copy_with_secret_filter(
        source,
        destination,
        display_path="binary.bin",
        context=context,
        log=lambda _msg: None,
    )

    assert size == destination.stat().st_size


def test_copy_with_secret_filter_redacts_and_handles_zero_length(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    source = tmp_path / "input.txt"
    destination = tmp_path / "dest" / "input.txt"
    _create_text_file(source, "skip me\nsecret=42\n")

    rule_anchor = SecretDetectionRule("anchor", re.compile("^"))
    rule_secret = SecretDetectionRule("secret", re.compile("secret"))
    context = SecretDetectionContext(
        rules=(rule_secret, rule_anchor),
        version="1",
        ignore_rules=frozenset(),
        ignore_patterns=(re.compile("skip"),),
        ignore_pattern_text=("skip",),
        findings=[],
        rules_loaded=True,
    )

    logged: list[str] = []

    def fake_log(message: str) -> None:
        logged.append(message)

    monkeypatch.setattr(offline_runner.shutil, "copystat", lambda *args, **kwargs: (_ for _ in ()).throw(OSError()))

    size, _digest = _copy_with_secret_filter(
        source,
        destination,
        display_path="input.txt",
        context=context,
        log=fake_log,
    )

    assert size == destination.stat().st_size
    written = destination.read_text(encoding="utf-8")
    assert "[SECRET]" in written
    assert any("scrubbed" in entry for entry in logged)
    assert "secret" in {finding.rule for finding in context.findings}


def test_copy_with_secret_filter_long_line(tmp_path: Path) -> None:
    source = tmp_path / "long.txt"
    destination = tmp_path / "out" / "long.txt"
    _create_text_file(source, "secret=" + "x" * 300)

    context = SecretDetectionContext(
        rules=(SecretDetectionRule("secret", re.compile("secret")),),
        version="1",
        ignore_rules=frozenset(),
        ignore_patterns=(),
        ignore_pattern_text=(),
        findings=[],
        rules_loaded=True,
    )

    logs: list[str] = []

    size, _digest = _copy_with_secret_filter(
        source,
        destination,
        display_path="long.txt",
        context=context,
        log=logs.append,
    )

    assert size == destination.stat().st_size
    assert any("..." in entry for entry in logs)


def test_copy_with_secret_filter_respects_ignore_rules(tmp_path: Path) -> None:
    source = tmp_path / "ignored.txt"
    destination = tmp_path / "dest" / "ignored.txt"
    _create_text_file(source, "secret=42")

    context = SecretDetectionContext(
        rules=(SecretDetectionRule("secret", re.compile("secret")),),
        version="1",
        ignore_rules=frozenset({"secret"}),
        ignore_patterns=(),
        ignore_pattern_text=(),
        findings=[],
        rules_loaded=True,
    )

    size, _digest = _copy_with_secret_filter(
        source,
        destination,
        display_path="ignored.txt",
        context=context,
        log=lambda _: None,
    )

    assert size == destination.stat().st_size
    assert context.findings == []


def test_relative_path_fallback(tmp_path: Path) -> None:
    base = tmp_path / "base"
    base.mkdir()
    outside = tmp_path / "outside.txt"
    outside.write_text("data", encoding="utf-8")
    assert _relative_path(base, outside) == Path(outside.name)


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


def test_execute_config_handles_resolve_failure(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    source_dir = tmp_path / "dir"
    source_dir.mkdir()
    nested = source_dir / "nested"
    nested.mkdir()
    file_path = nested / "data.txt"
    _create_text_file(file_path, "data")

    original_resolve = Path.resolve

    def fake_resolve(self: Path) -> Path:
        if self in {nested, source_dir}:
            raise FileNotFoundError("gone")
        return original_resolve(self)

    monkeypatch.setattr(Path, "resolve", fake_resolve)

    profile = {
        "name": "resolve",
        "sources": [str(source_dir)],
    }
    config = _build_config(tmp_path, profile=profile)

    offline_runner.execute_config(config, base_dir=tmp_path)


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
