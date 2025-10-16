from __future__ import annotations

from dataclasses import replace
from pathlib import Path
from typing import Mapping

import pytest

from driftbuster.core.profiles import (
    AppliedProfileConfig,
    ConfigurationProfile,
    ProfileConfig,
    ProfileStore,
    diff_summary_snapshots,
    normalize_tags,
)


def _config(identifier: str, *, path: str | None = None, path_glob: str | None = None, tags: set[str] | None = None) -> ProfileConfig:
    return ProfileConfig(
        identifier=identifier,
        path=path,
        path_glob=path_glob,
        tags=tags or set(),
    )


def test_profile_config_matches_tagged_and_glob_paths(tmp_path: Path) -> None:
    config = _config("cfg", path="configs/app.config", tags={"prod"})
    other = _config("glob", path_glob="configs/*.json")
    tags = normalize_tags(["prod", "application:demo"])

    assert config.matches(relative_path="configs/app.config", provided_tags=tags) is True
    assert config.matches(relative_path="configs/app.config", provided_tags=frozenset()) is False
    assert config.matches(relative_path=None, provided_tags=tags) is False
    assert other.matches(relative_path="configs/settings.json", provided_tags=frozenset()) is True
    assert other.matches(relative_path="other.json", provided_tags=frozenset()) is False

    application_config = ProfileConfig(identifier="app", application="service")
    assert application_config.matches(relative_path=None, provided_tags=frozenset({"application:service"})) is True
    assert application_config.matches(relative_path=None, provided_tags=frozenset()) is False


def test_configuration_profile_matching_configs_respects_path_filters(tmp_path: Path) -> None:
    config = _config("cfg", path="service.json", tags={"svc"})
    profile = ConfigurationProfile(name="svc", configs=(config,), tags={"svc"})
    matched = profile.matching_configs(provided_tags=frozenset({"svc"}), relative_path="service.json")
    assert matched == (config,)
    assert profile.matching_configs(provided_tags=frozenset(), relative_path="service.json") == ()


def test_profile_store_prevents_duplicate_configs() -> None:
    store = ProfileStore()
    profile = ConfigurationProfile(name="demo", configs=(_config("cfg"),))
    store.register_profile(profile)

    with pytest.raises(ValueError):
        store.register_profile(profile)

    duplicate_config = ConfigurationProfile(name="other", configs=(_config("cfg"),))
    with pytest.raises(ValueError):
        store.register_profile(duplicate_config)


def test_profile_store_update_mutator_validation() -> None:
    profile = ConfigurationProfile(name="demo", configs=(_config("cfg"),))
    store = ProfileStore([profile])

    with pytest.raises(TypeError):
        store.update_profile("demo", mutator="not callable")  # type: ignore[arg-type]

    with pytest.raises(TypeError):
        store.update_profile("demo", lambda _: "invalid")

    def rename(profile: ConfigurationProfile) -> ConfigurationProfile:
        return replace(profile, name="other")

    store.register_profile(ConfigurationProfile(name="other", configs=()))
    with pytest.raises(ValueError):
        store.update_profile("demo", rename)


def test_profile_store_update_reverts_on_failure() -> None:
    original = ConfigurationProfile(name="demo", configs=(_config("cfg"),))
    store = ProfileStore([original])

    def bad_mutator(profile: ConfigurationProfile) -> ConfigurationProfile:
        new_config = _config("cfg")  # duplicate on purpose
        return replace(profile, configs=(new_config, new_config))

    with pytest.raises(ValueError):
        store.update_profile("demo", bad_mutator)

    assert store.get_profile("demo").configs == original.configs


def test_profile_store_remove_profile_and_config(tmp_path: Path) -> None:
    profile = ConfigurationProfile(name="demo", configs=(_config("cfg"), _config("other")))
    store = ProfileStore([profile])

    updated = store.remove_config("demo", "other")
    assert len(updated.configs) == 1

    with pytest.raises(KeyError):
        store.remove_config("missing", "cfg")

    with pytest.raises(ValueError):
        store.remove_config("demo", "missing")

    store.remove_profile("demo")
    assert store.find_config("cfg") == ()


def test_profile_store_summary_and_applicable_profiles() -> None:
    prod_profile = ConfigurationProfile(name="prod", tags={"prod"}, configs=(_config("cfg"),))
    qa_profile = ConfigurationProfile(name="qa", tags={"qa"}, configs=())
    store = ProfileStore([prod_profile, qa_profile])

    summary = store.summary()
    assert summary["total_profiles"] == 2
    assert summary["total_configs"] == 1

    applicable = store.applicable_profiles(["qa"])
    assert applicable == (qa_profile,)

    profiles = store.profiles()
    assert len(profiles) == 2

    serialised = store.to_dict()
    restored = ProfileStore.from_dict(serialised)
    assert restored.summary()["total_profiles"] == 2


def test_diff_summary_snapshots_skips_entries_without_name() -> None:
    baseline = {
        "profiles": [
            {"name": "demo", "config_ids": ("a",)},
            {"config_ids": ("b",)},
        ]
    }
    current = {
        "profiles": [
            {"name": "demo", "config_ids": ("a", "b")},
        ]
    }
    result = diff_summary_snapshots(baseline, current)
    assert result["totals"]["current"]["configs"] == 2


def test_profile_store_matching_configs_returns_indexed_results() -> None:
    profile = ConfigurationProfile(name="demo", configs=(_config("cfg"),))
    store = ProfileStore([profile])
    result = store.matching_configs(tags=None, relative_path="whatever")
    assert isinstance(result[0], AppliedProfileConfig)
    assert store.find_config("missing") == ()
