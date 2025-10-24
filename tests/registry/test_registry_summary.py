from __future__ import annotations

import pytest

from driftbuster.registry import (
    SearchSpec,
    enumerate_installed_apps,
    find_app_registry_roots,
    registry_summary,
    search_registry,
)


_UNINSTALL_PATH = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"


class _RecordingBackend:
    def __init__(self) -> None:
        self._subkeys = {
            ("HKLM", _UNINSTALL_PATH, "64"): ["ExampleApp"],
        }
        uninstall_key = f"{_UNINSTALL_PATH}\\ExampleApp"
        self._subkeys[("HKLM", uninstall_key, "64")] = []
        self._subkeys[("HKCU", "Software\\ExampleApp", None)] = []

        self._values = {
            ("HKLM", uninstall_key, "64"): [
                ("DisplayName", "ExampleApp"),
                ("Publisher", "ExampleCorp"),
                ("DisplayVersion", "1.2.3"),
            ],
            ("HKCU", "Software\\ExampleApp", None): [
                ("SettingName", "Example value"),
                ("Another", 42),
            ],
        }

    def enum_subkeys(self, hive: str, path: str, view: str | None):
        return list(self._subkeys.get((hive, path, view), []))

    def enum_values(self, hive: str, path: str, view: str | None):
        return list(self._values.get((hive, path, view), []))


class _FailingBackend:
    def enum_subkeys(self, hive: str, path: str, view: str | None):
        raise RuntimeError("backend not initialised")

    def enum_values(self, hive: str, path: str, view: str | None):
        raise RuntimeError("backend not initialised")


def test_registry_summary_tracks_usage_statistics() -> None:
    registry_summary(reset=True)
    backend = _RecordingBackend()

    apps = enumerate_installed_apps(backend=backend)
    assert len(apps) == 1

    roots = find_app_registry_roots("ExampleApp", installed=apps)
    assert roots

    spec = SearchSpec(keywords=("example",), max_depth=0, max_hits=5)
    hits = search_registry((roots[0],), spec, backend=backend)
    assert hits

    summary = {entry["operation"]: entry for entry in registry_summary()}

    enumerate_stats = summary["enumerate_installed_apps"]
    assert enumerate_stats["calls"] == 1
    assert enumerate_stats["successes"] == 1
    assert enumerate_stats["errors"] == 0
    assert enumerate_stats["avg_duration_ms"] >= 0.0
    assert enumerate_stats["first_invocation"] is not None
    assert enumerate_stats["last_invocation"] is not None

    search_stats = summary["search_registry"]
    assert search_stats["calls"] == 1
    assert search_stats["successes"] == 1
    assert search_stats["errors"] == 0
    assert search_stats["last_error"] is None
    assert search_stats["last_duration_ms"] >= 0.0


def test_registry_summary_records_errors_and_reset() -> None:
    registry_summary(reset=True)

    failing_backend = _FailingBackend()
    with pytest.raises(RuntimeError):
        search_registry((("HKLM", "Software\\Broken", None),), SearchSpec(), backend=failing_backend)

    summary = {entry["operation"]: entry for entry in registry_summary()}
    stats = summary["search_registry"]
    assert stats["calls"] == 1
    assert stats["successes"] == 0
    assert stats["errors"] == 1
    assert isinstance(stats["last_error"], str)

    registry_summary(reset=True)
    reset_summary = {entry["operation"]: entry for entry in registry_summary()}
    assert reset_summary["search_registry"]["calls"] == 0
    assert reset_summary["search_registry"]["errors"] == 0
