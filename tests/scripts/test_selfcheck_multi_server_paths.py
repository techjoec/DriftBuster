from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace

import scripts.selfcheck_multi_server_paths as selfcheck


def test_resolve_pythonpath_prefers_portable_src(tmp_path: Path) -> None:
    portable_root = tmp_path / "portable"
    (portable_root / "src" / "driftbuster").mkdir(parents=True)

    resolved = selfcheck.resolve_pythonpath(portable_root)

    assert resolved == portable_root / "src"


def test_main_writes_success_report(tmp_path: Path, monkeypatch) -> None:
    output = tmp_path / "report.json"
    portable_root = tmp_path / "portable"
    samples_root = tmp_path / "samples"
    pythonpath = tmp_path / "src"
    pythonpath.mkdir(parents=True)
    samples_root.mkdir(parents=True)

    monkeypatch.setattr(
        selfcheck,
        "parse_args",
        lambda: SimpleNamespace(portable_root=portable_root, output=output),
    )
    monkeypatch.setattr(selfcheck, "resolve_pythonpath", lambda _portable: pythonpath)
    monkeypatch.setattr(selfcheck, "resolve_samples", lambda _portable: samples_root)
    monkeypatch.setattr(
        selfcheck,
        "run_scenarios",
        lambda _samples, _py, _dir: [
            selfcheck.ScenarioResult(name="a", passed=True, details={"x": 1}),
            selfcheck.ScenarioResult(name="b", passed=True, details={"x": 2}),
        ],
    )

    code = selfcheck.main()

    assert code == 0
    payload = json.loads(output.read_text(encoding="utf-8"))
    assert payload["success"] is True
    assert payload["passed"] == 2
    assert payload["total"] == 2
