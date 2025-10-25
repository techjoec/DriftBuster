"""Cross-platform coverage verification helper.

This script mirrors ``scripts/verify_coverage.sh`` but avoids shell
assumptions so Windows contributors can run the same sweep.  It executes
Python coverage, Avalonia/.NET coverage with thresholds, and produces the
combined coverage summary used in release evidence.
"""
from __future__ import annotations

import argparse
import json
import shlex
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterable, List, Sequence

Command = Sequence[str]
Runner = Callable[[Command], None]


@dataclass
class VerifyOptions:
    run_python: bool = True
    run_dotnet: bool = True
    run_summary: bool = True
    run_perf_smoke: bool = False
    perf_filter: str = "Category=PerfSmoke"
    python_threshold: int = 90
    dotnet_threshold: int = 90
    python_source: str = "src/driftbuster"
    python_json: str = "coverage.json"
    dotnet_project: str = (
        "gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj"
    )
    dotnet_results_dir: str = "artifacts/coverage-dotnet"
    pytest_args: Sequence[str] = ("-q",)
    extra_python_args: Sequence[str] = ()
    skip_python_json: bool = False
    python_module_thresholds: dict[str, int] = field(
        default_factory=lambda: {"src/driftbuster/offline_runner.py": 90}
    )


def run_command(command: Command) -> None:
    """Execute ``command`` and raise if it fails."""
    subprocess.run(command, check=True)


def build_python_commands(opts: VerifyOptions) -> List[List[str]]:
    base: List[List[str]] = []
    pytest_cmd = [
        "coverage",
        "run",
        f"--source={opts.python_source}",
        "-m",
        "pytest",
    ]
    pytest_cmd.extend(opts.extra_python_args)
    pytest_cmd.extend(opts.pytest_args)
    base.append(pytest_cmd)
    base.append([
        "coverage",
        "report",
        f"--fail-under={opts.python_threshold}",
    ])
    if not opts.skip_python_json:
        base.append([
            "coverage",
            "json",
            "-o",
            opts.python_json,
        ])
    return base


def build_dotnet_commands(opts: VerifyOptions) -> List[List[str]]:
    Path(opts.dotnet_results_dir).mkdir(parents=True, exist_ok=True)
    return [
        [
            "dotnet",
            "test",
            opts.dotnet_project,
            f"-p:Threshold={opts.dotnet_threshold}",
            "-p:ThresholdType=line",
            "-p:ThresholdStat=total",
            '--collect:"XPlat Code Coverage"',
            f"--results-directory",
            opts.dotnet_results_dir,
            "-v",
            "minimal",
        ]
    ]


def build_summary_commands(opts: VerifyOptions) -> List[List[str]]:
    return [
        [
            sys.executable,
            "-m",
            "scripts.coverage_report",
            "--python-json",
            opts.python_json,
            "--dotnet-root",
            opts.dotnet_results_dir,
        ]
    ]


def build_perf_commands(opts: VerifyOptions) -> List[List[str]]:
    log_dir = Path("artifacts/perf")
    log_dir.mkdir(parents=True, exist_ok=True)
    return [
        [
            "dotnet",
            "test",
            opts.dotnet_project,
            "--filter",
            opts.perf_filter,
            "--logger",
            "trx;LogFileName=PerfSmoke.trx",
            "-v",
            "minimal",
            "--results-directory",
            str(log_dir),
        ]
    ]


def execute(commands: Iterable[Command], runner: Runner) -> None:
    for command in commands:
        runner(command)


def enforce_python_module_thresholds(opts: VerifyOptions) -> None:
    if not opts.python_module_thresholds:
        return
    if opts.skip_python_json:
        raise RuntimeError(
            "Module thresholds require coverage json output; remove --skip-python-json."
        )

    coverage_path = Path(opts.python_json)
    if not coverage_path.exists():
        raise FileNotFoundError(
            f"Python coverage json not found at {coverage_path} for module enforcement."
        )

    payload = json.loads(coverage_path.read_text(encoding="utf-8"))
    files = payload.get("files")
    if not isinstance(files, dict):
        raise RuntimeError("coverage json missing 'files' mapping")

    errors: list[str] = []
    for target, threshold in opts.python_module_thresholds.items():
        record = files.get(target)
        if not isinstance(record, dict):
            errors.append(f"coverage entry not found for {target}")
            continue
        summary = record.get("summary")
        if not isinstance(summary, dict):
            errors.append(f"coverage summary missing for {target}")
            continue
        percent = summary.get("percent_covered")
        try:
            percent_value = float(percent)
        except (TypeError, ValueError):
            errors.append(f"invalid percent_covered for {target}: {percent!r}")
            continue
        if percent_value < threshold:
            errors.append(
                f"{target} coverage {percent_value:.2f}% below required {threshold}%"
            )

    if errors:
        joined = "\n- ".join(errors)
        raise RuntimeError(f"Python module coverage check failed:\n- {joined}")


def verify(opts: VerifyOptions, runner: Runner = run_command) -> None:
    if opts.run_python:
        print("-- Python coverage sweep")
        execute(build_python_commands(opts), runner)
        enforce_python_module_thresholds(opts)
    if opts.run_dotnet:
        print("-- .NET coverage sweep")
        execute(build_dotnet_commands(opts), runner)
    if opts.run_summary:
        print("-- Coverage summary")
        execute(build_summary_commands(opts), runner)
    if opts.run_perf_smoke:
        print("-- Performance smoke suite")
        execute(build_perf_commands(opts), runner)


def parse_args(argv: Sequence[str] | None = None) -> VerifyOptions:
    parser = argparse.ArgumentParser(
        description="Run coverage gates for Python and .NET components."
    )
    parser.add_argument(
        "--skip-python",
        action="store_true",
        help="Skip Python coverage sweep.",
    )
    parser.add_argument(
        "--skip-dotnet",
        action="store_true",
        help="Skip .NET coverage sweep.",
    )
    parser.add_argument(
        "--skip-summary",
        action="store_true",
        help="Skip combined coverage summary output.",
    )
    parser.add_argument(
        "--perf-smoke",
        action="store_true",
        help="Run the performance smoke suite after coverage sweeps.",
    )
    parser.add_argument(
        "--perf-filter",
        default="Category=PerfSmoke",
        help="Filter expression for performance smoke tests.",
    )
    parser.add_argument(
        "--python-threshold",
        type=int,
        default=90,
        help="Fail coverage if Python percent falls below this threshold.",
    )
    parser.add_argument(
        "--dotnet-threshold",
        type=int,
        default=90,
        help="Fail coverage if .NET line coverage falls below this threshold.",
    )
    parser.add_argument(
        "--dotnet-project",
        default="gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj",
        help="Path to the .NET test project to execute.",
    )
    parser.add_argument(
        "--dotnet-results-dir",
        default="artifacts/coverage-dotnet",
        help="Directory to store .NET coverage results.",
    )
    parser.add_argument(
        "--python-json",
        default="coverage.json",
        help="Output path for coverage json results.",
    )
    parser.add_argument(
        "--python-source",
        default="src/driftbuster",
        help="Python package path for coverage measurement.",
    )
    parser.add_argument(
        "--skip-python-json",
        action="store_true",
        help="Do not emit coverage json output.",
    )
    parser.add_argument(
        "--extra-python-args",
        default="",
        help="Additional arguments inserted before pytest args for coverage run.",
    )
    parser.add_argument(
        "--python-module-threshold",
        action="append",
        default=[],
        metavar="PATH=PERCENT",
        help=(
            "Require PATH in coverage json to meet PERCENT (integer) coverage; "
            "may be specified multiple times."
        ),
    )

    args, pytest_args = parser.parse_known_args(argv)

    extra_args: Sequence[str]
    if args.extra_python_args:
        extra_args = shlex.split(args.extra_python_args)
    else:
        extra_args = ()

    module_thresholds: dict[str, int] = {}
    for entry in args.python_module_threshold:
        if "=" not in entry:
            parser.error("--python-module-threshold entries must be PATH=PERCENT")
        path, value = entry.split("=", 1)
        path = path.strip()
        if not path:
            parser.error("--python-module-threshold requires non-empty path")
        try:
            percent = int(value)
        except ValueError as exc:  # pragma: no cover - argparse error path
            parser.error(f"Invalid percent for module threshold '{entry}': {exc}")
        module_thresholds[path] = percent

    kwargs = dict(
        run_python=not args.skip_python,
        run_dotnet=not args.skip_dotnet,
        run_summary=not args.skip_summary,
        run_perf_smoke=args.perf_smoke,
        perf_filter=args.perf_filter,
        python_threshold=args.python_threshold,
        dotnet_threshold=args.dotnet_threshold,
        python_source=args.python_source,
        python_json=args.python_json,
        dotnet_project=args.dotnet_project,
        dotnet_results_dir=args.dotnet_results_dir,
        pytest_args=tuple(pytest_args) or ("-q",),
        extra_python_args=extra_args,
        skip_python_json=args.skip_python_json,
    )
    if module_thresholds:
        kwargs["python_module_thresholds"] = module_thresholds

    return VerifyOptions(**kwargs)


def main(argv: Sequence[str] | None = None) -> int:
    opts = parse_args(argv)
    try:
        verify(opts)
    except subprocess.CalledProcessError as exc:  # pragma: no cover - passthrough
        return exc.returncode or 1
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
