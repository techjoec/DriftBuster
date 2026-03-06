from __future__ import annotations

import argparse
import hashlib
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_STAGE_DIR = Path("/lap_temp/DriftBuster-Portabletest")
TARGET_FRAMEWORK = "net10.0"


def run(command: list[str], *, cwd: Path | None = None) -> None:
    display = " ".join(command)
    print(f"\n-> {display}")
    subprocess.run(command, cwd=cwd, check=True)


def read_gui_version(root: Path) -> str:
    import json

    versions_path = root / "versions.json"
    data = json.loads(versions_path.read_text(encoding="utf-8"))
    version = str(data.get("gui", "")).strip()
    if not version:
        raise SystemExit(f"Missing GUI version in {versions_path}")

    return version


def build_publish(root: Path, *, configuration: str, rid: str) -> Path:
    project = root / "gui" / "DriftBuster.Gui" / "DriftBuster.Gui.csproj"
    run(
        [
            "dotnet",
            "publish",
            str(project),
            "-c",
            configuration,
            "-r",
            rid,
            "/p:PublishSingleFile=true",
            "/p:SelfContained=false",
            "/p:IncludeNativeLibrariesForSelfExtract=true",
        ],
        cwd=root,
    )

    publish_dir = root / "gui" / "DriftBuster.Gui" / "bin" / configuration / TARGET_FRAMEWORK / rid / "publish"
    if not publish_dir.exists():
        raise SystemExit(f"Publish output not found: {publish_dir}")

    return publish_dir


def write_debug_launchers(bundle_dir: Path) -> None:
    cmd = bundle_dir / "Run-DriftBuster-Debug.cmd"
    cmd.write_text(
        "@echo off\n"
        "setlocal\n"
        "set \"DRIFTBUSTER_DEBUG=1\"\n"
        "start \"\" \"%~dp0DriftBuster.Gui.exe\" %*\n"
        "endlocal\n",
        encoding="utf-8",
    )

    ps1 = bundle_dir / "Run-DriftBuster-Debug.ps1"
    ps1.write_text(
        "$env:DRIFTBUSTER_DEBUG = \"1\"\n"
        "Start-Process -FilePath (Join-Path $PSScriptRoot \"DriftBuster.Gui.exe\") -ArgumentList $args\n",
        encoding="utf-8",
    )

    readme = bundle_dir / "README.debug.txt"
    readme.write_text(
        "DriftBuster Win11 Portable Debug Bundle\n\n"
        "Use Run-DriftBuster-Debug.cmd (or .ps1) to force DRIFTBUSTER_DEBUG=1.\n"
        "Logs are written to %LOCALAPPDATA%\\DriftBuster\\logs\\debug.jsonl.\n",
        encoding="utf-8",
    )


def copy_python_sources(root: Path, bundle_dir: Path) -> None:
    src_root = root / "src"
    if not src_root.exists():
        raise SystemExit(f"Python source directory not found: {src_root}")

    destination = bundle_dir / "src"
    if destination.exists():
        shutil.rmtree(destination)

    shutil.copytree(
        src_root,
        destination,
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc", ".pytest_cache"),
    )


def zip_bundle(bundle_dir: Path) -> Path:
    zip_path = Path(f"{bundle_dir}.zip")
    if zip_path.exists():
        zip_path.unlink()

    archive_base = Path(str(bundle_dir))
    shutil.make_archive(str(archive_base), "zip", root_dir=bundle_dir.parent, base_dir=bundle_dir.name)
    return zip_path


def write_sha256(path: Path) -> Path:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    sha_path = Path(f"{path}.sha256")
    sha_path.write_text(f"{digest}  {path}\n", encoding="utf-8")
    return sha_path


def stage_bundle(bundle_dir: Path, stage_dir: Path) -> None:
    if stage_dir.exists():
        try:
            shutil.rmtree(stage_dir)
        except PermissionError:
            _stage_bundle_with_locked_executable_fallback(bundle_dir, stage_dir)
            return

    stage_dir.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(bundle_dir, stage_dir)


def _rewrite_launchers_for_next_executable(stage_dir: Path) -> None:
    cmd = stage_dir / "Run-DriftBuster-Debug.cmd"
    cmd.write_text(
        "@echo off\n"
        "setlocal\n"
        "set \"DRIFTBUSTER_DEBUG=1\"\n"
        "start \"\" \"%~dp0DriftBuster.Gui.next.exe\" %*\n"
        "endlocal\n",
        encoding="utf-8",
    )

    ps1 = stage_dir / "Run-DriftBuster-Debug.ps1"
    ps1.write_text(
        "$env:DRIFTBUSTER_DEBUG = \"1\"\n"
        "Start-Process -FilePath (Join-Path $PSScriptRoot \"DriftBuster.Gui.next.exe\") -ArgumentList $args\n",
        encoding="utf-8",
    )


def _stage_bundle_with_locked_executable_fallback(bundle_dir: Path, stage_dir: Path) -> None:
    stage_dir.mkdir(parents=True, exist_ok=True)
    copied_next_executable = False

    for source in bundle_dir.rglob("*"):
        relative = source.relative_to(bundle_dir)
        destination = stage_dir / relative

        if source.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
            continue

        destination.parent.mkdir(parents=True, exist_ok=True)
        try:
            shutil.copy2(source, destination)
        except PermissionError:
            if destination.name.lower() == "driftbuster.gui.exe":
                next_executable = destination.with_name("DriftBuster.Gui.next.exe")
                shutil.copy2(source, next_executable)
                copied_next_executable = True
            else:
                raise

    if copied_next_executable:
        _rewrite_launchers_for_next_executable(stage_dir)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build a portable debug bundle and stage it for local Win11 testing."
    )
    parser.add_argument(
        "--stage-dir",
        type=Path,
        default=DEFAULT_STAGE_DIR,
        help=f"Local stage directory (default: {DEFAULT_STAGE_DIR}).",
    )
    parser.add_argument(
        "--rid",
        default="win-x64",
        help="dotnet publish runtime identifier (default: win-x64).",
    )
    parser.add_argument(
        "--configuration",
        default="Release",
        help="dotnet publish configuration (default: Release).",
    )
    parser.add_argument(
        "--timestamp",
        help="Optional UTC timestamp override (format: YYYYMMDD-HHMMSSZ).",
    )
    return parser.parse_args()


def main() -> int:
    if Path.cwd() != ROOT:
        raise SystemExit(f"Run this script from repository root: {ROOT}")

    args = parse_args()
    timestamp = args.timestamp or datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ")
    version = read_gui_version(ROOT)
    publish_dir = build_publish(ROOT, configuration=args.configuration, rid=args.rid)

    portable_root = ROOT / "artifacts" / "gui-packaging" / "portable"
    portable_root.mkdir(parents=True, exist_ok=True)

    bundle_name = f"DriftBuster.Gui-{version}-win11-portable-debug-{timestamp}"
    bundle_dir = portable_root / bundle_name
    if bundle_dir.exists():
        shutil.rmtree(bundle_dir)

    shutil.copytree(publish_dir, bundle_dir)
    copy_python_sources(ROOT, bundle_dir)
    write_debug_launchers(bundle_dir)

    zip_path = zip_bundle(bundle_dir)
    sha_path = write_sha256(zip_path)

    stage_dir = args.stage_dir.expanduser()
    stage_bundle(bundle_dir, stage_dir)

    print("\nPortable debug bundle ready:")
    print(f" - Bundle dir: {bundle_dir}")
    print(f" - Zip: {zip_path}")
    print(f" - Zip SHA256: {sha_path}")
    print(f" - Staged run dir: {stage_dir}")
    print(f" - Launch: {stage_dir / 'Run-DriftBuster-Debug.cmd'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
