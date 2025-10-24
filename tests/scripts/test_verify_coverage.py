from __future__ import annotations

import sys
from pathlib import Path
from typing import List

import pytest

from scripts.verify_coverage import (
    VerifyOptions,
    build_dotnet_commands,
    build_python_commands,
    build_summary_commands,
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
    opts = VerifyOptions(dotnet_results_dir=str(tmp_path / "coverage"))
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
