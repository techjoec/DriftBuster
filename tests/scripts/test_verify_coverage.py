from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import List

import pytest

from scripts.verify_coverage import (
    VerifyOptions,
    build_dotnet_commands,
    build_python_commands,
    build_summary_commands,
    enforce_python_module_thresholds,
    parse_args,
    verify,
)


class CommandRecorder:
    def __init__(self) -> None:
        self.commands: List[List[str]] = []

    def __call__(self, command: List[str]) -> None:
        self.commands.append(list(command))


def test_default_verify_runs_all_sections(tmp_path: Path) -> None:
    recorder = CommandRecorder()
    coverage_path = tmp_path / "coverage.json"
    coverage_payload = {
        "files": {
            "src/driftbuster/offline_runner.py": {
                "summary": {"percent_covered": 100.0}
            }
        }
    }
    coverage_path.write_text(json.dumps(coverage_payload), encoding="utf-8")

    opts = VerifyOptions(
        dotnet_results_dir=str(tmp_path / "coverage"),
        python_json=str(coverage_path),
    )
    verify(opts, runner=recorder)

    assert recorder.commands[0][:4] == [
        "coverage",
        "run",
        f"--source={opts.python_source}",
        "-m",
    ]
    assert ["coverage", "report", f"--fail-under={opts.python_threshold}"] in recorder.commands
    assert ["coverage", "json", "-o", opts.python_json] in recorder.commands
    assert any(cmd[:2] == ["dotnet", "test"] for cmd in recorder.commands)
    summary_cmd = [
        sys.executable,
        "-m",
        "scripts.coverage_report",
        "--python-json",
        opts.python_json,
        "--dotnet-root",
        opts.dotnet_results_dir,
    ]
    assert summary_cmd in recorder.commands


def test_parse_args_defaults_to_quiet_pytest() -> None:
    opts = parse_args([])
    assert opts.pytest_args == ("-q",)
    assert opts.run_python and opts.run_dotnet and opts.run_summary


def test_parse_args_respects_skip_flags() -> None:
    opts = parse_args(["--skip-python", "--skip-dotnet", "--skip-summary"])
    assert not opts.run_python
    assert not opts.run_dotnet
    assert not opts.run_summary


def test_extra_python_args_inserted_before_pytest_args() -> None:
    opts = parse_args(["--extra-python-args", "-k smoke", "-vv"])
    commands = build_python_commands(opts)
    assert commands[0][:7] == [
        "coverage",
        "run",
        f"--source={opts.python_source}",
        "-m",
        "pytest",
        "-k",
        "smoke",
    ]
    assert commands[0][-1] == "-vv"


def test_build_dotnet_commands_creates_directory(tmp_path: Path) -> None:
    results_dir = tmp_path / "coverage-dotnet"
    opts = VerifyOptions(dotnet_results_dir=str(results_dir))
    commands = build_dotnet_commands(opts)
    assert results_dir.exists()
    assert commands[0][0:2] == ["dotnet", "test"]


def test_build_summary_commands_uses_configured_paths() -> None:
    opts = VerifyOptions(python_json="out.json", dotnet_results_dir="dotnet-out")
    commands = build_summary_commands(opts)
    assert commands[0][-2:] == ["--dotnet-root", "dotnet-out"]


def test_enforce_python_module_thresholds_pass(tmp_path: Path) -> None:
    coverage_path = tmp_path / "coverage.json"
    coverage_payload = {
        "files": {
            "src/driftbuster/offline_runner.py": {
                "summary": {"percent_covered": 95.0}
            }
        }
    }
    coverage_path.write_text(json.dumps(coverage_payload), encoding="utf-8")

    opts = VerifyOptions(
        python_json=str(coverage_path),
        python_module_thresholds={"src/driftbuster/offline_runner.py": 90},
    )
    enforce_python_module_thresholds(opts)


def test_enforce_python_module_thresholds_raises_for_low_coverage(tmp_path: Path) -> None:
    coverage_path = tmp_path / "coverage.json"
    coverage_payload = {
        "files": {
            "src/driftbuster/offline_runner.py": {
                "summary": {"percent_covered": 42.0}
            }
        }
    }
    coverage_path.write_text(json.dumps(coverage_payload), encoding="utf-8")

    opts = VerifyOptions(
        python_json=str(coverage_path),
        python_module_thresholds={"src/driftbuster/offline_runner.py": 90},
    )
    with pytest.raises(RuntimeError):
        enforce_python_module_thresholds(opts)


def test_enforce_python_module_thresholds_raises_for_missing_entry(tmp_path: Path) -> None:
    coverage_path = tmp_path / "coverage.json"
    coverage_payload = {"files": {}}
    coverage_path.write_text(json.dumps(coverage_payload), encoding="utf-8")

    opts = VerifyOptions(
        python_json=str(coverage_path),
        python_module_thresholds={"src/driftbuster/offline_runner.py": 90},
    )
    with pytest.raises(RuntimeError):
        enforce_python_module_thresholds(opts)
