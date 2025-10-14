from __future__ import annotations

from pathlib import Path

import pytest

from driftbuster.core.profiles import (
    AppliedProfileConfig,
    ConfigurationProfile,
    ProfileConfig,
    ProfileStore,
    diff_summary_snapshots,
)


def test_profile_config_matching_rules() -> None:
    config = ProfileConfig(
        identifier="cfg-web",
        path="configs/web.config",
        application="web",
        tags={"prod"},
    )

    matching_tags = frozenset({"prod", "application:web"})
    assert config.matches(relative_path="configs/web.config", provided_tags=matching_tags)

    assert not config.matches(relative_path="configs/web.config", provided_tags=frozenset())
    assert not config.matches(relative_path="other.config", provided_tags=matching_tags)


def test_profile_store_registration_and_matching() -> None:
    prod_profile = ConfigurationProfile(
        name="prod",
        tags={"prod"},
        configs=(
            ProfileConfig(
                identifier="cfg-app",
                path="appsettings.json",
                tags={"prod"},
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

