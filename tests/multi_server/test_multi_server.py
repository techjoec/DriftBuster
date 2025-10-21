from __future__ import annotations

from pathlib import Path

import os
from unittest import mock

from driftbuster.core.detector import DetectorIOError
from driftbuster.multi_server import (
    BaselinePreference,
    ExportOptions,
    MultiServerRunner,
    Plan,
    SCHEMA_VERSION,
)


SAMPLES_ROOT = Path("samples/multi-server")


def _sample_plan(host: str, *, priority: int, is_preferred: bool = False) -> Plan:
    return Plan(
        host_id=host,
        label=host,
        scope="custom_roots",
        roots=(SAMPLES_ROOT / host,),
        baseline=BaselinePreference(is_preferred=is_preferred, priority=priority),
        export=ExportOptions(),
        throttle_seconds=None,
        cached_at=None,
    )


def test_multi_server_generates_catalog_and_drilldown(tmp_path) -> None:
    cache_dir = tmp_path / "cache"
    runner = MultiServerRunner(cache_dir)
    plans = [
        _sample_plan("server01", priority=10, is_preferred=True),
        _sample_plan("server02", priority=5),
    ]

    response = runner.run(plans)

    assert response["version"] == SCHEMA_VERSION
    assert len(response["results"]) == 2

    catalog = response["catalog"]
    assert catalog, "expected catalog entries"
    app_entry = next(entry for entry in catalog if entry["display_name"].endswith("appsettings.json"))
    assert set(app_entry["present_hosts"]) == {"server01", "server02"}
    assert app_entry["drift_count"] >= 1

    drilldown = next(entry for entry in response["drilldown"] if entry["display_name"].endswith("appsettings.json"))
    assert drilldown["baseline_host_id"] == "server01"
    server_entries = {entry["host_id"]: entry for entry in drilldown["servers"]}
    assert server_entries["server01"]["is_baseline"] is True
    assert server_entries["server02"]["status"] in {"Drift", "Match"}


def test_multi_server_uses_cache_on_subsequent_runs(tmp_path) -> None:
    cache_dir = tmp_path / "cache"
    runner = MultiServerRunner(cache_dir)
    plans = [
        _sample_plan("server01", priority=1, is_preferred=True),
        _sample_plan("server02", priority=0),
    ]

    first = runner.run(plans)
    assert any(not result["used_cache"] for result in first["results"]), "expected cold run without cache"

    second = runner.run(plans)
    cached_flags = [result["used_cache"] for result in second["results"] if result["availability"] == "found"]
    assert cached_flags and all(cached_flags), "expected hot run to reuse cache entries"


def test_config_ids_are_deterministic(tmp_path) -> None:
    cache_dir = tmp_path / "cache"
    runner = MultiServerRunner(cache_dir)
    plans = [
        _sample_plan("server01", priority=5, is_preferred=True),
        _sample_plan("server02", priority=1),
        _sample_plan("server03", priority=0),
    ]

    first = runner.run(plans)
    second = runner.run(plans)

    first_ids = sorted(entry["config_id"] for entry in first["catalog"])
    second_ids = sorted(entry["config_id"] for entry in second["catalog"])
    assert first_ids == second_ids


def test_missing_roots_marked_not_found(tmp_path) -> None:
    cache_dir = tmp_path / "cache"
    runner = MultiServerRunner(cache_dir)
    missing_plan = Plan(
        host_id="missing",
        label="Missing host",
        scope="custom_roots",
        roots=(Path("/does/not/exist"),),
    )

    response = runner.run([missing_plan])

    result = response["results"][0]
    assert result["availability"] == "not_found"
    assert result["status"] == "failed"
    assert response["catalog"] == []


def test_detector_permission_errors_are_reported(tmp_path, monkeypatch) -> None:
    cache_dir = tmp_path / "cache"
    runner = MultiServerRunner(cache_dir)
    plan = _sample_plan("server01", priority=1)

    class FakeDetector:
        def scan_path(self, *_args, **_kwargs):
            raise DetectorIOError(path=SAMPLES_ROOT / "server01" / "appsettings.json", reason="denied")

    monkeypatch.setattr(runner, "_detector", FakeDetector())

    response = runner.run([plan])
    result = response["results"][0]
    assert result["status"] == "failed"
    assert result["availability"] == "permission_denied"
    assert "Permission denied" in result["message"]
