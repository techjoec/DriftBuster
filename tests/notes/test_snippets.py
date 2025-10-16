from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path

import pytest

from driftbuster import profile_cli


SNIPPETS_DIR = Path(__file__).resolve().parents[2] / "notes" / "snippets"


def _load_snippet(name: str):
    path = SNIPPETS_DIR / f"{name}.py"
    spec = importlib.util.spec_from_file_location(f"snippet_{name}", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Unable to load snippet module {name}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_profile_summary_cli_generates_outputs(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.chdir(tmp_path)

    store_payload = {
        "profiles": [
            {
                "name": "prod",
                "configs": [
                    {
                        "id": "cfg1",
                        "path": "configs/app.config",
                    }
                ],
            }
        ]
    }
    baseline_store_payload = {"profiles": []}

    (tmp_path / "profiles.json").write_text(json.dumps(store_payload), encoding="utf-8")
    baseline_store_path = tmp_path / "baseline-store.json"
    baseline_store_path.write_text(json.dumps(baseline_store_payload), encoding="utf-8")

    baseline_summary_path = tmp_path / "baseline-summary.json"
    profile_cli.main(
        [
            "summary",
            str(baseline_store_path),
            "--output",
            str(baseline_summary_path),
            "--indent",
            "2",
        ]
    )

    cli_module = _load_snippet("profile-summary-cli")
    exit_code = cli_module.main()

    assert exit_code == 0

    summary_path = tmp_path / "profile-summary.json"
    diff_path = tmp_path / "profile-summary-diff.json"
    assert summary_path.exists()
    assert diff_path.exists()

    summary_payload = json.loads(summary_path.read_text(encoding="utf-8"))
    assert summary_payload["total_profiles"] == 1

    diff_payload = json.loads(diff_path.read_text(encoding="utf-8"))
    assert diff_payload["totals"]["current"]["profiles"] == 1


def test_profile_summary_diff_reports_changes(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    baseline_store = {
        "profiles": [
            {
                "name": "prod",
                "configs": [
                    {
                        "id": "cfg1",
                        "path": "configs/app.config",
                    }
                ],
            }
        ]
    }
    current_store = {
        "profiles": [
            {
                "name": "prod",
                "configs": [
                    {
                        "id": "cfg1",
                        "path": "configs/app.config",
                    },
                    {
                        "id": "cfg2",
                        "path": "configs/appsettings.json",
                    },
                ],
            },
            {
                "name": "staging",
                "configs": [],
            },
        ]
    }

    baseline_path = tmp_path / "baseline.json"
    current_path = tmp_path / "current.json"
    baseline_path.write_text(json.dumps(baseline_store), encoding="utf-8")
    current_path.write_text(json.dumps(current_store), encoding="utf-8")

    monkeypatch.setattr(sys, "argv", ["profile-summary-diff.py", str(baseline_path), str(current_path)])

    diff_module = _load_snippet("profile-summary-diff")
    exit_code = diff_module.main()

    assert exit_code == 0
    captured = capsys.readouterr()
    assert "Baseline totals:" in captured.out
    assert "Added profiles:" in captured.out


def test_registry_summary_outputs_json(capsys: pytest.CaptureFixture[str]) -> None:
    registry_module = _load_snippet("registry-summary")
    registry_module.main()
    output = capsys.readouterr().out
    data = json.loads(output)
    assert isinstance(data, list)
    assert data


def test_token_catalog_main_produces_catalog(tmp_path: Path) -> None:
    hunts_payload = [
        {
            "rule": {"name": "server-name", "token_name": "server"},
            "relative_path": "configs/app.config",
            "excerpt": "server=prod",
        }
    ]
    hunts_path = tmp_path / "hunts.json"
    hunts_path.write_text(json.dumps(hunts_payload), encoding="utf-8")

    output_path = tmp_path / "catalog.json"
    token_module = _load_snippet("token-catalog")
    exit_code = token_module.main(
        [
            "--hunts",
            str(hunts_path),
            "--catalog-variant",
            "structured-settings-json",
            "--output",
            str(output_path),
        ]
    )

    assert exit_code == 0
    catalog = json.loads(output_path.read_text(encoding="utf-8"))
    assert catalog[0]["token_name"] == "server"
    assert catalog[0]["catalog_variant"] == "structured-settings-json"
    assert len(catalog[0]["excerpt_hash"]) == 64
