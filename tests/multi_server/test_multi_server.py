from __future__ import annotations

from pathlib import Path

import io
import json
import os
import sys
from unittest import mock

from driftbuster.core.detector import DetectorIOError
from driftbuster.multi_server import (
    BaselinePreference,
    ExportOptions,
    MultiServerRunner,
    Plan,
    SCHEMA_VERSION,
    _reset_progress_throttle_state,
    _resolve_cache_dir,
    emit_progress,
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



def test_resolve_cache_dir_uses_data_root_env(monkeypatch, tmp_path) -> None:
    data_root = tmp_path / "data-root"
    monkeypatch.setenv("DRIFTBUSTER_DATA_ROOT", str(data_root))
    cache_dir = _resolve_cache_dir(None)
    assert cache_dir == (data_root / "cache" / "diffs").resolve()
    assert cache_dir.exists()


def test_resolve_cache_dir_migrates_legacy_cache(monkeypatch, tmp_path) -> None:
    data_root = tmp_path / "data-root"
    repo_root = tmp_path / "repo"
    legacy_dir = repo_root / "artifacts" / "cache" / "diffs"
    legacy_dir.mkdir(parents=True)
    legacy_file = legacy_dir / "legacy.json"
    legacy_file.write_text("{}", encoding="utf-8")

    monkeypatch.setenv("DRIFTBUSTER_DATA_ROOT", str(data_root))
    monkeypatch.chdir(repo_root)

    cache_dir = _resolve_cache_dir(None)
    migrated = cache_dir / legacy_file.name
    assert migrated.exists()
    assert migrated.read_text(encoding="utf-8") == "{}"


def test_build_catalog_handles_offline_and_partial_hosts(monkeypatch, tmp_path) -> None:
    cache_dir = tmp_path / "cache"
    runner = MultiServerRunner(cache_dir)
    plans = [
        _sample_plan("server01", priority=10, is_preferred=True),
        _sample_plan("server02", priority=5),
    ]

    original_scan_plan = MultiServerRunner._scan_plan

    def fake_scan_plan(self, plan, existing_roots, secret_hits):  # type: ignore[override]
        if plan.host_id == "server02":
            raise RuntimeError("simulated offline host")
        return original_scan_plan(self, plan, existing_roots, secret_hits)

    monkeypatch.setattr(MultiServerRunner, "_scan_plan", fake_scan_plan)

    try:
        response = runner.run(plans)
    finally:
        monkeypatch.setattr(MultiServerRunner, "_scan_plan", original_scan_plan)

    offline_result = next(result for result in response["results"] if result["host_id"] == "server02")
    assert offline_result["availability"] == "offline"
    assert offline_result["status"] == "failed"

    catalog = response["catalog"]
    assert catalog, "expected catalog entries"
    assert any("server02" in entry["missing_hosts"] for entry in catalog)
    assert any(entry["coverage_status"] == "partial" for entry in catalog)

    drilldown = response["drilldown"]
    assert drilldown, "expected drilldown entries"
    offline_server = next(
        server
        for entry in drilldown
        for server in entry["servers"]
        if server["host_id"] == "server02"
    )
    assert offline_server["status"] == "Offline"
    assert offline_server["presence_status"] == "offline"


def test_drilldown_includes_sanitized_diff_summary(tmp_path) -> None:
    cache_dir = tmp_path / "cache"
    runner = MultiServerRunner(cache_dir)
    plans = [
        _sample_plan("server01", priority=10, is_preferred=True),
        _sample_plan("server02", priority=5),
    ]

    response = runner.run(plans)

    summaries = [entry.get("diff_summary") for entry in response["drilldown"] if entry.get("diff_summary")]
    assert summaries, "expected sanitized diff summary payload"
    summary = summaries[0]
    assert summary["comparison_count"] >= 1
    comparison = summary["comparisons"][0]
    assert comparison["summary"]["before_digest"].startswith("sha256:")

def test_emit_progress_throttles_duplicate_messages(monkeypatch) -> None:
    buffer = io.StringIO()
    monkeypatch.setattr(sys, "stdout", buffer, raising=False)
    _reset_progress_throttle_state()

    emit_progress("host-a", "running", "Scanning", _now=1.0)
    emit_progress("host-a", "running", "Scanning", _now=1.01)

    payloads = [line for line in buffer.getvalue().splitlines() if line.strip()]
    assert len(payloads) == 1


def test_emit_progress_emits_when_message_changes(monkeypatch) -> None:
    buffer = io.StringIO()
    monkeypatch.setattr(sys, "stdout", buffer, raising=False)
    _reset_progress_throttle_state()

    emit_progress("host-a", "running", "Scanning", _now=2.0)
    emit_progress("host-a", "running", "Finishing", _now=2.01)

    payloads = [json.loads(line) for line in buffer.getvalue().splitlines() if line.strip()]
    messages = [payload["payload"]["message"] for payload in payloads]
    assert messages == ["Scanning", "Finishing"]


def test_emit_progress_emits_after_interval(monkeypatch) -> None:
    buffer = io.StringIO()
    monkeypatch.setattr(sys, "stdout", buffer, raising=False)
    _reset_progress_throttle_state()

    emit_progress("host-a", "running", "Scanning", _now=5.0)
    emit_progress("host-a", "running", "Scanning", _now=5.2)

    payloads = [json.loads(line) for line in buffer.getvalue().splitlines() if line.strip()]
    assert len(payloads) == 2
