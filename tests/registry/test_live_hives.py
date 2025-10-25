import json
from pathlib import Path

import pytest

from driftbuster.registry import parse_registry_root_descriptor
import driftbuster.registry_cli as registry_cli
from driftbuster import offline_runner
from scripts import capture as capture_script


def test_parse_registry_root_descriptor_variants():
    root = parse_registry_root_descriptor("HKLM\\Software\\Vendor,view=32")
    assert root.hive == "HKLM"
    assert root.path == "Software\\Vendor"
    assert root.view == "32"

    auto = parse_registry_root_descriptor("HKCU\\Software\\Tool,view=auto")
    assert auto.hive == "HKCU"
    assert auto.view is None


def test_registry_cli_emit_config_with_roots(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    monkeypatch.setattr(registry_cli, "is_windows", lambda: True)
    monkeypatch.setattr(registry_cli, "enumerate_installed_apps", lambda: ())
    monkeypatch.setattr(registry_cli, "find_app_registry_roots", lambda token, installed: ())

    exit_code = registry_cli.main([
        "emit-config",
        "VendorA",
        "--keyword",
        "server",
        "--root",
        "HKLM\\Software\\VendorA,view=64",
    ])
    assert exit_code == 0

    payload = json.loads(capsys.readouterr().out)
    roots = payload["registry_scan"].get("roots")
    assert roots == [{"hive": "HKLM", "path": "Software\\VendorA", "view": "64"}]


def test_offline_runner_uses_explicit_roots(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "registry-only",
            "sources": [
                {
                    "registry_scan": {
                        "token": "VendorA",
                        "roots": [{"hive": "HKLM", "path": r"Software\\VendorA", "view": "64"}],
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

    class DummyHit:
        def __init__(self) -> None:
            self.hive = "HKLM"
            self.path = r"Software\\VendorA"
            self.value_name = "Server"
            self.data_preview = "api.internal"
            self.reason = "keyword"

    recorded: dict[str, object] = {}

    monkeypatch.setattr("driftbuster.registry.is_windows", lambda: True)

    def raise_if_called(*_args, **_kwargs):
        raise AssertionError("find_app_registry_roots should not run when roots are supplied")

    monkeypatch.setattr("driftbuster.registry.find_app_registry_roots", raise_if_called)
    monkeypatch.setattr("driftbuster.registry.enumerate_installed_apps", raise_if_called)

    def fake_search(roots, spec):
        recorded["roots"] = roots
        recorded["keywords"] = spec.keywords
        return (DummyHit(),)

    monkeypatch.setattr("driftbuster.registry.search_registry", fake_search)

    result = offline_runner.execute_config(config, base_dir=tmp_path, timestamp="20250312T010101Z")
    assert "roots" in recorded
    assert recorded["roots"] == (("HKLM", r"Software\\VendorA", "64"),)

    manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
    summary = manifest["sources"][0]
    assert "registry_scan" == summary["type"]
    normalised_roots = [entry.replace("\\\\", "\\") for entry in summary["roots"]]
    assert normalised_roots == ["HKLM \\ Software\\VendorA"]
    normalised_requested = [entry.replace("\\\\", "\\") for entry in summary["requested_roots"]]
    assert normalised_requested == ["HKLM \\ Software\\VendorA (view 64)"]

    assert result.staging_dir is not None
    alias = config.profile.sources[0].destination_name(fallback_index=1)
    data_path = Path(result.staging_dir) / config.settings.data_directory_name / alias / "registry_scan.json"
    payload = json.loads(data_path.read_text(encoding="utf-8"))
    assert payload["requested_roots"][0]["view"] == "64"


def test_capture_manifest_embeds_registry_scans(tmp_path: Path) -> None:
    registry_json = tmp_path / "registry_scan.json"
    registry_json.write_text(
        json.dumps(
            {
                "token": "VendorA",
                "roots": [{"hive": "HKLM", "path": r"Software\\VendorA"}],
                "requested_roots": [
                    {"hive": "HKLM", "path": r"Software\\VendorA", "view": "64"}
                ],
                "hits": [
                    {
                        "hive": "HKLM",
                        "path": r"Software\\VendorA",
                        "value_name": "Server",
                        "data_preview": "api",
                        "reason": "keyword",
                    }
                ],
            }
        ),
        encoding="utf-8",
    )

    summaries = capture_script._load_registry_scan_summaries([str(registry_json)])
    normalised_roots = [entry.replace("\\\\", "\\") for entry in summaries[0]["roots"]]
    normalised_requested = [entry.replace("\\\\", "\\") for entry in summaries[0]["requested_roots"]]
    assert normalised_roots == ["HKLM \\ Software\\VendorA"]
    assert normalised_requested == ["HKLM \\ Software\\VendorA (view 64)"]
    manifest = capture_script._build_manifest_payload(
        capture={
            "id": "capture",
            "captured_at": "2025-03-12T00:00:00Z",
            "root": str(tmp_path),
            "operator": "tester",
            "environment": "lab",
            "reason": "validation",
            "host": "test-host",
        },
        snapshot_path=tmp_path / "snapshot.json",
        manifest_path=tmp_path / "manifest.json",
        detection_duration=1.0,
        hunt_duration=0.5,
        total_duration=1.5,
        detection_count=0,
        profile_match_count=0,
        hunt_count=0,
        profile_summary={},
        placeholder="[REDACTED]",
        mask_token_count=0,
        total_redactions=0,
        registry_scans=summaries,
    )
    assert manifest["counts"]["registry_scans"] == 1
    assert manifest["registry_scans"][0]["file"] == registry_json.name
