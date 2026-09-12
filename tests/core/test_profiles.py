from __future__ import annotations

from collections.abc import Iterable
from dataclasses import replace

import pytest

from driftbuster.core.profiles import (
    AppliedProfileConfig,
    ConfigurationProfile,
    ProfileConfig,
    ProfileStore,
    diff_summary_snapshots,
    normalize_tags,
)


def test_profile_config_matching_rules() -> None:
    config = ProfileConfig(
        identifier="cfg-web",
        path="configs/web.config",
        application="web",
        tags=frozenset({"prod"}),
    )

    matching_tags = frozenset({"prod", "application:web"})
    assert config.matches(relative_path="configs/web.config", provided_tags=matching_tags)

    assert not config.matches(relative_path="configs/web.config", provided_tags=frozenset())
    assert not config.matches(relative_path="other.config", provided_tags=matching_tags)


def test_profile_store_registration_and_matching() -> None:
    prod_profile = ConfigurationProfile(
        name="prod",
        tags=frozenset({"prod"}),
        configs=(
            ProfileConfig(
                identifier="cfg-app",
                path="appsettings.json",
                tags=frozenset({"prod"}),
                application="api",
            ),
        ),
    )

    store = ProfileStore([prod_profile])
    matches = store.matching_configs(
        ["prod", "application:api"],
        relative_path="appsettings.json",
    )

    assert matches
    applied = matches[0]
    assert isinstance(applied, AppliedProfileConfig)
    assert applied.profile.name == "prod"
    assert applied.config.identifier == "cfg-app"

    with pytest.raises(ValueError):
        store.register_profile(prod_profile)

    with pytest.raises(ValueError):
        store.register_profile(
            ConfigurationProfile(
                name="dupe-config",
                configs=(ProfileConfig(identifier="cfg-app"),),
            )
        )


def test_profile_store_summary_and_diff() -> None:
    baseline_store = ProfileStore(
        [
            ConfigurationProfile(
                name="prod",
                configs=(ProfileConfig(identifier="cfg1"),),
            )
        ]
    )
    current_store = ProfileStore(
        [
            ConfigurationProfile(
                name="prod",
                configs=(
                    ProfileConfig(identifier="cfg1"),
                    ProfileConfig(identifier="cfg2"),
                ),
            ),
            ConfigurationProfile(
                name="staging",
                configs=(ProfileConfig(identifier="cfg3"),),
            ),
        ]
    )

    summary = baseline_store.summary()
    assert summary["total_profiles"] == 1
    assert summary["total_configs"] == 1

    diff = diff_summary_snapshots(baseline_store.summary(), current_store.summary())
    assert diff["added_profiles"] == ("staging",)
    assert diff["removed_profiles"] == ()
    assert diff["totals"]["current"]["profiles"] == 2
    changed = diff["changed_profiles"]
    assert len(changed) == 1
    assert changed[0]["name"] == "prod"
    assert changed[0]["added_config_ids"] == ("cfg2",)


def test_profile_store_update_remove_and_serialisation() -> None:
    profile = ConfigurationProfile(
        name="default",
        configs=(
            ProfileConfig(identifier="cfg1"),
            ProfileConfig(identifier="cfg2"),
        ),
    )
    store = ProfileStore([profile])

    with pytest.raises(TypeError):
        store.update_profile("default", mutator="not-callable")  # type: ignore[arg-type]

    def mutate(original: ConfigurationProfile) -> ConfigurationProfile:
        return ConfigurationProfile(
            name=original.name,
            configs=original.configs[:-1],
        )

    updated = store.update_profile("default", mutate)
    assert len(updated.configs) == 1

    with pytest.raises(ValueError):
        store.remove_config("default", "cfg-missing")

    store.remove_config("default", "cfg1")
    assert store.find_config("cfg1") == ()

    payload = store.to_dict()
    assert len(payload["profiles"]) == 1
    exported = payload["profiles"][0]
    assert exported["configs"] == []

    rebuilt = ProfileStore.from_dict(
        {
            "profiles": [
                {
                    "name": "imported",
                    "configs": [
                        {"id": "cfg", "path": "path\\file.txt", "tags": [" prod "]}
                    ],
                }
            ]
        }
    )
    assert rebuilt.find_config("cfg")


def test_applicable_profiles_and_normalise_tags() -> None:
    profile = ConfigurationProfile(name="tagged", tags=frozenset({"prod"}))
    store = ProfileStore([profile])

    matches = store.applicable_profiles(["prod"])  # Tag matched
    assert matches[0].name == "tagged"

    assert store.applicable_profiles(["dev"]) == ()
    assert normalize_tags([" prod ", ""]) == frozenset({"prod"})


def _config(identifier: str, *, path: str | None = None, path_glob: str | None = None, tags: Iterable[str] | None = None) -> ProfileConfig:
    return ProfileConfig(
        identifier=identifier,
        path=path,
        path_glob=path_glob,
        tags=frozenset(tags or ()),
    )


def test_profile_config_matches_tagged_and_glob_paths() -> None:
    config = _config("cfg", path="configs/app.config", tags=frozenset({"prod"}))
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


def test_configuration_profile_matching_configs_respects_path_filters() -> None:
    config = _config("cfg", path="service.json", tags=frozenset({"svc"}))
    profile = ConfigurationProfile(name="svc", configs=(config,), tags=frozenset({"svc"}))
    matched = profile.matching_configs(provided_tags=frozenset({"svc"}), relative_path="service.json")
    assert matched == (config,)
    assert profile.matching_configs(provided_tags=frozenset(), relative_path="service.json") == ()


def test_profile_store_update_mutator_validation() -> None:
    profile = ConfigurationProfile(name="demo", configs=(_config("cfg"),))
    store = ProfileStore([profile])

    with pytest.raises(TypeError):
        store.update_profile("demo", mutator="not callable")  # type: ignore[arg-type]

    with pytest.raises(TypeError):
        store.update_profile("demo", lambda _: "invalid")  # pyright: ignore[reportArgumentType]  # exercises the runtime type check

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


def test_profile_store_remove_profile_and_config() -> None:
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
