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


def _read_versions_json() -> dict:
    import json
    path = REPO_ROOT / "versions.json"
    return json.loads(path.read_text(encoding="utf-8"))


def build_installer(*, rid: str, release_notes: Path, channel: str | None = None, pack_id: str | None = None) -> None:
    """Build a Velopack installer for the GUI.

    Requires: dotnet tool 'vpk' restored; release notes file per docs/release-notes.md.
    """
    if not release_notes.exists():
        raise SystemExit(f"Release notes not found: {release_notes}")

    versions = _read_versions_json()
    gui_version = str(versions.get("gui", "0.0.0"))

    script = REPO_ROOT / "scripts" / "build_velopack_release.sh"
    if not script.exists():
        raise SystemExit(f"Installer script not found: {script}")

    cmd = [
        "bash",
        str(script),
        "--version",
        gui_version,
        "--rid",
        rid,
        "--release-notes",
        str(release_notes),
    ]
    if channel:
        cmd.extend(["--channel", channel])
    if pack_id:
        cmd.extend(["--pack-id", pack_id])
    run(cmd)


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
    parser.add_argument(
        "--no-installer",
        action="store_true",
        help="Do not build a Velopack installer (default builds installer).",
    )
    parser.add_argument(
        "--installer-rid",
        default="win-x64",
        help="RID for installer packaging (default: win-x64).",
    )
    parser.add_argument(
        "--release-notes",
        help="Path to release notes markdown (required for installer packaging).",
    )
    parser.add_argument(
        "--channel",
        help="Optional installer update channel label.",
    )
    parser.add_argument(
        "--pack-id",
        help="Override installer pack id (defaults to com.driftbuster.gui).",
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

    # Build installer when explicitly supported by parsed args; fall back to
    # skipping in legacy test harnesses that don't pass new flags.
    if not getattr(args, "no_installer", True):
        release_notes_arg = getattr(args, "release_notes", None)
        if not release_notes_arg:
            raise SystemExit("--release-notes is required to build the installer. Use --no-installer to skip.")
        build_installer(
            rid=getattr(args, "installer_rid", "win-x64"),
            release_notes=Path(release_notes_arg),
            channel=getattr(args, "channel", None),
            pack_id=getattr(args, "pack_id", None),
        )

    print("\nRelease artifacts ready:")
    print(f" - Python dist: {PY_ARTIFACT_DIR}")
    gui_dir = GUI_ARTIFACT_DIR / (args.runtime if args.runtime else "framework")
    print(f" - GUI publish: {gui_dir}")
    if not getattr(args, "no_installer", True):
        print(f" - Installer: artifacts/velopack/releases/{getattr(args, 'installer_rid', 'win-x64')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
