import json
from pathlib import Path

import pytest

from driftbuster import offline_runner


def test_load_secret_rules_caches_results() -> None:
    original_cache = offline_runner._SECRET_RULE_CACHE
    original_version = offline_runner._SECRET_RULE_VERSION
    try:
        offline_runner._SECRET_RULE_CACHE = None
        offline_runner._SECRET_RULE_VERSION = None
        rules, version, loaded = offline_runner._load_secret_rules()
        assert isinstance(loaded, bool)
        assert isinstance(rules, tuple)
        assert isinstance(version, str)

        sentinel_rules = (object(),)
        offline_runner._SECRET_RULE_CACHE = sentinel_rules  # type: ignore[assignment]
        offline_runner._SECRET_RULE_VERSION = "cache-version"
        cached_rules, cached_version, cached_loaded = offline_runner._load_secret_rules()
        assert cached_loaded is True
        assert cached_rules is sentinel_rules
        assert cached_version == "cache-version"
    finally:
        offline_runner._SECRET_RULE_CACHE = original_cache  # type: ignore[assignment]
        offline_runner._SECRET_RULE_VERSION = original_version


def test_offline_registry_scan_source_from_dict_normalises_values() -> None:
    payload = {
        "registry_scan": {
            "token": "ExampleApp ",
            "keywords": "alpha, beta",
            "patterns": ["value1", "value2"],
            "max_depth": "8",
            "max_hits": "150",
            "time_budget_s": "15",
        },
        "alias": "  ExampleAlias  ",
    }
    source = offline_runner.OfflineRegistryScanSource.from_dict(payload)
    assert source.token == "ExampleApp"
    assert source.keywords == ("alpha", "beta")
    assert source.patterns == ("value1", "value2")
    assert source.max_depth == 8
    assert source.max_hits == 150
    assert source.time_budget_s == pytest.approx(15.0)
    assert source.destination_name(fallback_index=1) == "--ExampleAlias--"

    payload_no_alias = {"registry_scan": {"token": "Example", "keywords": ["one"], "patterns": []}}
    no_alias = offline_runner.OfflineRegistryScanSource.from_dict(payload_no_alias)
    assert no_alias.destination_name(fallback_index=2).startswith("registry_")


def test_normalise_snapshot_columns_handles_sequences() -> None:
    mapping = offline_runner._normalise_snapshot_columns({
        "users": ["id", "email"],
        "events": ("timestamp", "severity"),
    })
    assert mapping["users"] == ("id", "email")
    assert mapping["events"] == ("timestamp", "severity")

    sequence = offline_runner._normalise_snapshot_columns([
        "audit.id",
        "audit.created",
        "logs.message",
        "invalid",
        "logs.",
    ])
    assert sequence["audit"] == ("id", "created")
    assert sequence["logs"] == ("message",)
    assert offline_runner._normalise_snapshot_columns(None) == {}


def test_offline_sql_snapshot_source_from_dict_and_kwargs(tmp_path: Path) -> None:
    payload = {
        "sql_snapshot": {
            "path": str(tmp_path / "db.sqlite"),
            "tables": ["users", "logs"],
            "exclude_tables": "audit",
            "mask_columns": {"users": ["password"]},
            "hash_columns": ["users.email", "users.id"],
            "limit": "25",
            "placeholder": "[MASKED]",
            "hash_salt": "pepper",
        },
        "alias": " database ",
    }
    source = offline_runner.OfflineSqlSnapshotSource.from_dict(payload)
    assert source.path == str(tmp_path / "db.sqlite")
    assert source.tables == ("users", "logs")
    assert source.exclude_tables == ("audit",)
    assert source.mask_columns["users"] == ("password",)
    assert source.hash_columns["users"] == ("email", "id")
    assert source.limit == 25
    assert source.placeholder == "[MASKED]"
    assert source.hash_salt == "pepper"
    assert source.destination_name(fallback_index=2) == "database"

    kwargs = source.snapshot_kwargs()
    assert kwargs["tables"] == ("users", "logs")
    assert kwargs["limit"] == 25


def test_offline_sql_snapshot_source_limit_validation() -> None:
    payload = {"sql_snapshot": {"path": "sample.db", "limit": 0}}
    with pytest.raises(ValueError):
        offline_runner.OfflineSqlSnapshotSource.from_dict(payload)


def test_offline_sql_snapshot_source_dialect_validation() -> None:
    payload = {"sql_snapshot": {"path": "sample.db", "dialect": "postgres"}}
    with pytest.raises(ValueError):
        offline_runner.OfflineSqlSnapshotSource.from_dict(payload)


def test_offline_runner_profile_with_registry_and_sql_sources(tmp_path: Path) -> None:
    file_path = tmp_path / "config.txt"
    file_path.write_text("example", encoding="utf-8")
    payload = {
        "name": "profile-sample",
        "sources": [
            {"path": str(file_path)},
            {
                "registry_scan": {
                    "token": "ExampleToken",
                    "keywords": ["alpha"],
                    "patterns": ["value"],
                }
            },
            {"sql_snapshot": {"path": "sample.db"}},
        ],
        "baseline": str(file_path),
        "tags": ["audit"],
        "options": {"secret_ignore_rules": ["PasswordAssignment"]},
        "secret_scanner": {"ignore_rules": ["GenericApiToken"]},
    }
    profile = offline_runner.OfflineRunnerProfile.from_dict(payload)
    assert len(profile.sources) == 3
    assert any(isinstance(src, offline_runner.OfflineRegistryScanSource) for src in profile.sources)
    assert any(isinstance(src, offline_runner.OfflineSqlSnapshotSource) for src in profile.sources)


def test_offline_encryption_settings_from_dict_formats_extension(tmp_path: Path) -> None:
    keyset_path = tmp_path / "key.json"
    keyset_path.write_text("{}", encoding="utf-8")
    settings = offline_runner.OfflineEncryptionSettings.from_dict(
        {
            "enabled": True,
            "mode": "DPAPI-AES",
            "keyset_path": str(keyset_path),
            "output_extension": "encpkg",
            "remove_plaintext": False,
        }
    )
    assert settings.enabled is True
    assert settings.mode == "dpapi-aes"
    assert settings.output_extension == ".encpkg"
    assert settings.remove_plaintext is False

    with pytest.raises(ValueError):
        offline_runner.OfflineEncryptionSettings.from_dict({"enabled": True})


def test_offline_runner_settings_from_dict_handles_defaults(tmp_path: Path) -> None:
    payload = {
        "output_directory": str(tmp_path),
        "package_name": " ",
        "max_total_bytes": "2048",
        "encryption": {"enabled": False},
    }
    settings = offline_runner.OfflineRunnerSettings.from_dict(payload)
    assert settings.output_directory == tmp_path
    assert settings.package_name is None
    assert settings.max_total_bytes == 2048
    assert settings.encryption is not None
    assert settings.encryption.enabled is False


def test_execute_config_skips_registry_scan_on_non_windows(tmp_path: Path) -> None:
    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "registry-only",
            "sources": [
                {
                    "registry_scan": {
                        "token": "ExampleToken",
                        "keywords": ["alpha"],
                        "patterns": ["value"],
                    }
                }
            ],
            "options": {},
            "secret_scanner": {},
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
            "compress": False,
            "cleanup_staging": False,
        },
        "metadata": {},
    }

    config = offline_runner.OfflineRunnerConfig.from_dict(config_payload)
    result = offline_runner.execute_config(config, base_dir=tmp_path, timestamp="20251025T083000Z")

    assert result.manifest_path is not None
    manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
    assert manifest["sources"]
    summary = manifest["sources"][0]
    assert summary["type"] == "registry_scan"
    assert summary["skipped"] is True
    assert summary["reason"] == "not-windows"
