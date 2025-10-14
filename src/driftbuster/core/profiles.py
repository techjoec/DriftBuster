"""Configuration profile utilities for DriftBuster.

Schema overview
================

``ProfileConfig`` fields:
    ``identifier`` (str): required stable key for the config entry.
    ``path`` (str | None): optional POSIX-style relative path (normalised).
    ``path_glob`` (str | None): optional POSIX glob for matching multiple files.
    ``application`` / ``version`` / ``branch`` (str | None): helper tags that
        check for ``"application:<value>"`` style tags when matching.
    ``tags`` (frozenset[str]): additional required tags beyond helpers (defaults
        to an empty frozenset when not provided).
    ``expected_format`` / ``expected_variant`` (str | None): optional hints for
        catalog alignment.
    ``metadata`` (Mapping[str, Any]): arbitrary metadata stored as a read-only
        mapping (defaults to ``{}``).

``ConfigurationProfile`` fields:
    ``name`` (str): required unique profile identifier.
    ``description`` (str | None): optional description (defaults to ``None``).
    ``tags`` (frozenset[str]): activation tags (defaults to an empty frozenset).
    ``configs`` (tuple[ProfileConfig, ...]): ordered collection of profile
        configs (defaults to ``()``).
    ``metadata`` (Mapping[str, Any]): additional profile metadata stored as a
        read-only mapping (defaults to ``{}``).

``ProfileStore`` responsibilities:
    maintain immutable profile/config instances, reject duplicate identifiers,
    provide summary/diff helpers, support copy-on-write updates, and expose
    targeted removal for configs while keeping lookups deterministic.
"""

from __future__ import annotations

from dataclasses import dataclass, field, replace
from fnmatch import fnmatch
from pathlib import Path, PurePosixPath
from types import MappingProxyType
from typing import Any, Callable, FrozenSet, Iterable, Mapping, Optional, Sequence, Tuple

from .types import DetectionMatch


def normalize_tags(tags: Optional[Iterable[str]]) -> FrozenSet[str]:
    """Return a normalised, deduplicated set of tag strings."""

    if tags is None:
        return frozenset()
    cleaned = {tag.strip() for tag in tags if tag and tag.strip()}
    return frozenset(cleaned)


def _freeze_mapping(data: Optional[Mapping[str, Any]]) -> Mapping[str, Any]:
    return MappingProxyType(dict(data or {}))


def _normalize_path(value: str) -> str:
    return str(PurePosixPath(value))


@dataclass(frozen=True)
class ProfileConfig:
    """Represents a single configuration expectation inside a profile."""

    identifier: str
    path: Optional[str] = None
    path_glob: Optional[str] = None
    application: Optional[str] = None
    version: Optional[str] = None
    branch: Optional[str] = None
    tags: FrozenSet[str] = field(default_factory=frozenset)
    expected_format: Optional[str] = None
    expected_variant: Optional[str] = None
    metadata: Mapping[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:  # pragma: no cover - small coercions
        object.__setattr__(self, "tags", normalize_tags(self.tags))
        object.__setattr__(self, "metadata", _freeze_mapping(self.metadata))
        if self.path is not None:
            object.__setattr__(self, "path", _normalize_path(self.path))
        if self.path_glob is not None:
            object.__setattr__(self, "path_glob", _normalize_path(self.path_glob))

    def matches(self, *, relative_path: Optional[str], provided_tags: FrozenSet[str]) -> bool:
        """Return ``True`` when the config applies to the given path + tags."""

        if self.tags and not self.tags.issubset(provided_tags):
            return False

        for prefix, value in (
            ("application", self.application),
            ("version", self.version),
            ("branch", self.branch),
        ):
            if value is None:
                continue
            if f"{prefix}:{value}" not in provided_tags:
                return False

        if self.path is None and self.path_glob is None:
            return True

        if relative_path is None:
            return False

        normalised = _normalize_path(relative_path)

        if self.path and normalised == self.path:
            return True
        if self.path_glob and fnmatch(normalised, self.path_glob):
            return True
        return False


@dataclass(frozen=True)
class ConfigurationProfile:
    """Container for a set of configuration expectations."""

    name: str
    description: Optional[str] = None
    tags: FrozenSet[str] = field(default_factory=frozenset)
    configs: Tuple[ProfileConfig, ...] = field(default_factory=tuple)
    metadata: Mapping[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:  # pragma: no cover - small coercions
        object.__setattr__(self, "tags", normalize_tags(self.tags))
        object.__setattr__(self, "configs", tuple(self.configs))
        object.__setattr__(self, "metadata", _freeze_mapping(self.metadata))

    def applies_to(self, provided_tags: FrozenSet[str]) -> bool:
        """Return True when the profile should activate for the tag set."""

        if not self.tags:
            return True
        return self.tags.issubset(provided_tags)

    def matching_configs(
        self,
        *,
        provided_tags: FrozenSet[str],
        relative_path: Optional[str],
    ) -> Tuple[ProfileConfig, ...]:
        matches = [
            config
            for config in self.configs
            if config.matches(relative_path=relative_path, provided_tags=provided_tags)
        ]
        return tuple(matches)


@dataclass(frozen=True)
class AppliedProfileConfig:
    """Specific profile/config pairing that applies to a path."""

    profile: ConfigurationProfile
    config: ProfileConfig


@dataclass(frozen=True)
class ProfiledDetection:
    """Detection result annotated with matching configuration profiles."""

    path: Path
    detection: Optional[DetectionMatch]
    profiles: Tuple[AppliedProfileConfig, ...]


class ProfileStore:
    """Registry for configuration profiles.

    Duplicate profile names and config identifiers are rejected at
    registration time, ``update_profile`` provides copy-on-write mutations,
    ``remove_config`` exposes targeted deletions, ``find_config`` provides a
    quick way to locate the owning profile/config pair for a given identifier,
    and ``summary`` exposes an immutable snapshot for manual audits without
    having to export the full profile payload.
    """

    def __init__(self, profiles: Optional[Sequence[ConfigurationProfile]] = None) -> None:
        self._profiles: dict[str, ConfigurationProfile] = {}
        self._config_index: dict[str, AppliedProfileConfig] = {}
        if profiles:
            for profile in profiles:
                self.register_profile(profile)

    def _validate_profile(self, profile: ConfigurationProfile) -> None:
        if profile.name in self._profiles:
            raise ValueError(f"Profile {profile.name!r} is already registered")

        seen_local: set[str] = set()
        for config in profile.configs:
            identifier = config.identifier
            if identifier in seen_local:
                raise ValueError(
                    f"Duplicate config identifier {identifier!r} within profile {profile.name!r}"
                )
            seen_local.add(identifier)
            existing = self._config_index.get(identifier)
            if existing is not None:
                raise ValueError(
                    f"Config identifier {identifier!r} already registered under profile "
                    f"{existing.profile.name!r}"
                )

    def _index_profile(self, profile: ConfigurationProfile) -> None:
        for config in profile.configs:
            self._config_index[config.identifier] = AppliedProfileConfig(
                profile=profile,
                config=config,
            )

    def _drop_profile_index(self, profile: ConfigurationProfile) -> None:
        for config in profile.configs:
            self._config_index.pop(config.identifier, None)

    def register_profile(self, profile: ConfigurationProfile) -> None:
        """Register ``profile`` ensuring duplicate names and config IDs are rejected."""

        self._validate_profile(profile)
        self._profiles[profile.name] = profile
        self._index_profile(profile)

    def update_profile(
        self,
        name: str,
        mutator: Callable[[ConfigurationProfile], ConfigurationProfile],
    ) -> ConfigurationProfile:
        """Clone ``name`` via ``mutator`` and replace it while preserving indexes."""

        if not callable(mutator):
            raise TypeError("mutator must be callable")

        try:
            original = self._profiles[name]
        except KeyError as exc:  # pragma: no cover - defensive guard
            raise KeyError(f"Profile {name!r} is not registered") from exc

        candidate_seed = replace(original)
        candidate = mutator(candidate_seed)
        if not isinstance(candidate, ConfigurationProfile):
            raise TypeError("mutator must return a ConfigurationProfile instance")

        if candidate.name != original.name and candidate.name in self._profiles:
            raise ValueError(f"Profile {candidate.name!r} is already registered")

        self._drop_profile_index(original)
        self._profiles.pop(name)

        try:
            self._validate_profile(candidate)
        except Exception:
            self._profiles[original.name] = original
            self._index_profile(original)
            raise

        self._profiles[candidate.name] = candidate
        self._index_profile(candidate)
        return candidate

    def remove_profile(self, name: str) -> None:
        profile = self._profiles.pop(name)
        self._drop_profile_index(profile)

    def remove_config(self, profile_name: str, config_id: str) -> ConfigurationProfile:
        """Remove ``config_id`` from ``profile_name`` raising descriptive errors."""

        def _remove(profile: ConfigurationProfile) -> ConfigurationProfile:
            remaining = tuple(
                config for config in profile.configs if config.identifier != config_id
            )
            if len(remaining) == len(profile.configs):
                raise ValueError(
                    f"Config identifier {config_id!r} is not registered under profile {profile.name!r}"
                )
            return replace(profile, configs=remaining)

        try:
            return self.update_profile(profile_name, _remove)
        except KeyError as exc:
            raise KeyError(f"Profile {profile_name!r} is not registered") from exc

    def get_profile(self, name: str) -> ConfigurationProfile:
        return self._profiles[name]

    def profiles(self) -> Tuple[ConfigurationProfile, ...]:
        return tuple(self._profiles.values())

    def summary(self) -> Mapping[str, Any]:
        """Return a read-only overview of stored profiles and configs."""

        ordered_names = sorted(self._profiles)
        profile_entries: list[Mapping[str, Any]] = []
        total_configs = 0
        for name in ordered_names:
            profile = self._profiles[name]
            config_ids = tuple(config.identifier for config in profile.configs)
            total_configs += len(config_ids)
            profile_entries.append(
                MappingProxyType(
                    {
                        "name": profile.name,
                        "description": profile.description,
                        "tags": tuple(sorted(profile.tags)),
                        "config_count": len(config_ids),
                        "config_ids": config_ids,
                    }
                )
            )

        payload = {
            "total_profiles": len(ordered_names),
            "total_configs": total_configs,
            "profiles": tuple(profile_entries),
        }
        return MappingProxyType(payload)

    def applicable_profiles(
        self, tags: Optional[Iterable[str]]
    ) -> Tuple[ConfigurationProfile, ...]:
        """Return profiles that apply to ``tags`` without mutating store state."""

        tag_set = normalize_tags(tags)
        return tuple(
            profile
            for profile in self._profiles.values()
            if profile.applies_to(tag_set)
        )

    def find_config(self, identifier: str) -> Tuple[AppliedProfileConfig, ...]:
        """Return the registered config matching ``identifier`` (empty when missing)."""

        match = self._config_index.get(identifier)
        if match is None:
            return ()
        return (match,)

    def matching_configs(
        self,
        tags: Optional[Iterable[str]],
        *,
        relative_path: Optional[str],
    ) -> Tuple[AppliedProfileConfig, ...]:
        """Return profile/config matches for ``relative_path`` under ``tags``."""

        tag_set = normalize_tags(tags)
        matches: list[AppliedProfileConfig] = []
        for profile in self.applicable_profiles(tag_set):
            for config in profile.matching_configs(
                provided_tags=tag_set,
                relative_path=relative_path,
            ):
                matches.append(AppliedProfileConfig(profile=profile, config=config))
        return tuple(matches)


def diff_summary_snapshots(
    baseline: Mapping[str, Any],
    current: Mapping[str, Any],
) -> Mapping[str, Any]:
    """Return a read-only diff between two ``ProfileStore.summary`` payloads."""

    def _as_int(value: Any, fallback: int) -> int:
        try:
            return int(value)
        except (TypeError, ValueError):  # pragma: no cover - defensive conversions
            return fallback

    def _profile_map(summary: Mapping[str, Any]) -> tuple[dict[str, Any], dict[str, int]]:
        entries: dict[str, Any] = {}
        totals = {
            "profiles": _as_int(summary.get("total_profiles"), 0),
            "configs": _as_int(summary.get("total_configs"), 0),
        }
        computed_config_total = 0
        for entry in summary.get("profiles", ()):  # type: ignore[union-attr]
            name = entry.get("name")
            if not name:
                continue
            config_ids = tuple(entry.get("config_ids", ()))
            config_count = _as_int(entry.get("config_count"), len(config_ids))
            computed_config_total += config_count
            entries[name] = {
                "config_ids": config_ids,
                "config_set": frozenset(config_ids),
                "config_count": config_count,
            }
        if not totals["profiles"]:
            totals["profiles"] = len(entries)
        if not totals["configs"]:
            totals["configs"] = computed_config_total
        return entries, totals

    baseline_profiles, baseline_totals = _profile_map(baseline)
    current_profiles, current_totals = _profile_map(current)

    added_profiles = sorted(set(current_profiles) - set(baseline_profiles))
    removed_profiles = sorted(set(baseline_profiles) - set(current_profiles))

    changed_profiles: list[Mapping[str, Any]] = []
    for name in sorted(set(baseline_profiles) & set(current_profiles)):
        base_entry = baseline_profiles[name]
        curr_entry = current_profiles[name]
        added_ids = tuple(sorted(curr_entry["config_set"] - base_entry["config_set"]))
        removed_ids = tuple(sorted(base_entry["config_set"] - curr_entry["config_set"]))
        if added_ids or removed_ids or base_entry["config_count"] != curr_entry["config_count"]:
            changed_profiles.append(
                MappingProxyType(
                    {
                        "name": name,
                        "baseline_config_count": base_entry["config_count"],
                        "current_config_count": curr_entry["config_count"],
                        "added_config_ids": added_ids,
                        "removed_config_ids": removed_ids,
                    }
                )
            )

    payload = {
        "totals": MappingProxyType(
            {
                "baseline": MappingProxyType(baseline_totals),
                "current": MappingProxyType(current_totals),
            }
        ),
        "added_profiles": tuple(added_profiles),
        "removed_profiles": tuple(removed_profiles),
        "changed_profiles": tuple(changed_profiles),
    }
    return MappingProxyType(payload)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "ProfileStore":
        profiles: list[ConfigurationProfile] = []
        for entry in payload.get("profiles", []):
            configs = []
            for cfg in entry.get("configs", []):
                configs.append(
                    ProfileConfig(
                        identifier=cfg["id"],
                        path=cfg.get("path"),
                        path_glob=cfg.get("path_glob"),
                        application=cfg.get("application"),
                        version=cfg.get("version"),
                        branch=cfg.get("branch"),
                        tags=cfg.get("tags"),
                        expected_format=cfg.get("expected_format"),
                        expected_variant=cfg.get("expected_variant"),
                        metadata=cfg.get("metadata", {}),
                    )
                )
            profiles.append(
                ConfigurationProfile(
                    name=entry["name"],
                    description=entry.get("description"),
                    tags=entry.get("tags"),
                    configs=tuple(configs),
                    metadata=entry.get("metadata", {}),
                )
            )
        return cls(profiles)

    def to_dict(self) -> Mapping[str, Any]:
        """Return a serialisable snapshot of stored profiles."""

        payload = {
            "profiles": [
                {
                    "name": profile.name,
                    "description": profile.description,
                    "tags": sorted(profile.tags),
                    "metadata": dict(profile.metadata),
                    "configs": [
                        {
                            "id": config.identifier,
                            "path": config.path,
                            "path_glob": config.path_glob,
                            "application": config.application,
                            "version": config.version,
                            "branch": config.branch,
                            "tags": sorted(config.tags),
                            "expected_format": config.expected_format,
                            "expected_variant": config.expected_variant,
                            "metadata": dict(config.metadata),
                        }
                        for config in profile.configs
                    ],
                }
                for profile in self._profiles.values()
            ]
        }
        return MappingProxyType(payload)


__all__ = [
    "AppliedProfileConfig",
    "ConfigurationProfile",
    "ProfileConfig",
    "ProfileStore",
    "ProfiledDetection",
    "diff_summary_snapshots",
    "normalize_tags",
]
