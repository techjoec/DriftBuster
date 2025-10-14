from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
PY_ARTIFACT_DIR = REPO_ROOT / "build" / "artifacts" / "python"
GUI_ARTIFACT_DIR = REPO_ROOT / "build" / "artifacts" / "gui"


def run(command: list[str], *, cwd: Path | None = None) -> None:
    display = " ".join(command)
    print(f"\n→ {display}")
    subprocess.run(command, cwd=cwd, check=True)


def ensure_dependency(module: str, package: str | None = None) -> None:
    try:
        __import__(module)
    except ImportError as exc:
        name = package or module
        raise SystemExit(
            f"Missing dependency '{name}'. Install it via 'pip install {name}' and retry."
        ) from exc


def clean_artifacts() -> None:
    build_root = REPO_ROOT / "build"
    if build_root.exists():
        shutil.rmtree(build_root)
    PY_ARTIFACT_DIR.mkdir(parents=True, exist_ok=True)
    GUI_ARTIFACT_DIR.mkdir(parents=True, exist_ok=True)


def run_tests(skip_tests: bool) -> None:
    if skip_tests:
        print("Skipping tests as requested.")
        return

    run([sys.executable, "-m", "pytest", "-q"])
    run(["dotnet", "test", "gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj", "-c", "Release"])


def build_python_package() -> None:
    ensure_dependency("build")
    run([sys.executable, "-m", "build", "--outdir", str(PY_ARTIFACT_DIR)])


def build_gui(runtime: str | None, self_contained: bool) -> None:
    target_dir = GUI_ARTIFACT_DIR / (runtime if runtime else "framework")
    target_dir.mkdir(parents=True, exist_ok=True)

    command = [
        "dotnet",
        "publish",
        "gui/DriftBuster.Gui/DriftBuster.Gui.csproj",
        "-c",
        "Release",
        "-o",
        str(target_dir),
    ]
    if runtime:
        command.extend(["-r", runtime])
        command.extend(["--self-contained", "true" if self_contained else "false"])

    run(command)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Prepare DriftBuster release artifacts.")
    parser.add_argument(
        "--skip-tests",
        action="store_true",
        help="Skip running pytest and dotnet test before building artifacts.",
    )
    parser.add_argument(
        "--runtime",
        help="Optional runtime identifier for dotnet publish (e.g., win-x64).",
    )
    parser.add_argument(
        "--self-contained",
        action="store_true",
        help="Produce a self-contained GUI publish when a runtime is specified.",
    )
    return parser.parse_args()


def main() -> int:
    if Path.cwd() != REPO_ROOT:
        raise SystemExit(f"Run this script from the repository root: {REPO_ROOT}")

    args = parse_args()

    clean_artifacts()
    run_tests(skip_tests=args.skip_tests)
    build_python_package()
    build_gui(runtime=args.runtime, self_contained=args.self_contained)

    print("\nRelease artifacts ready:")
    print(f" - Python dist: {PY_ARTIFACT_DIR}")
    gui_dir = GUI_ARTIFACT_DIR / (args.runtime if args.runtime else "framework")
    print(f" - GUI publish: {gui_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
