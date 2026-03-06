from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import pytest

import scripts.stage_portable_dev as stage_portable_dev


def test_stage_bundle_replaces_existing_directory(tmp_path: Path) -> None:
    bundle_dir = tmp_path / "bundle"
    bundle_dir.mkdir()
    (bundle_dir / "new.txt").write_text("new", encoding="utf-8")

    stage_dir = tmp_path / "stage"
    stage_dir.mkdir()
    (stage_dir / "old.txt").write_text("old", encoding="utf-8")

    stage_portable_dev.stage_bundle(bundle_dir, stage_dir)

    assert not (stage_dir / "old.txt").exists()
    assert (stage_dir / "new.txt").read_text(encoding="utf-8") == "new"


def test_try_write_launchers_creates_expected_files(tmp_path: Path) -> None:
    bundle_dir = tmp_path / "bundle"
    bundle_dir.mkdir()

    stage_portable_dev.write_debug_launchers(bundle_dir)

    assert (bundle_dir / "Run-DriftBuster-Debug.cmd").exists()
    assert (bundle_dir / "Run-DriftBuster-Debug.ps1").exists()
    assert (bundle_dir / "README.debug.txt").exists()


def test_copy_python_sources_copies_src_tree(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    package_dir = root / "src" / "driftbuster"
    package_dir.mkdir(parents=True, exist_ok=True)
    (package_dir / "__init__.py").write_text("", encoding="utf-8")
    (package_dir / "multi_server.py").write_text("print('ok')\n", encoding="utf-8")

    bundle_dir = tmp_path / "bundle"
    bundle_dir.mkdir()

    stage_portable_dev.copy_python_sources(root, bundle_dir)

    assert (bundle_dir / "src" / "driftbuster" / "multi_server.py").exists()


def test_main_builds_bundle_and_stages_to_target(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    root = tmp_path
    publish_dir = root / "gui" / "DriftBuster.Gui" / "bin" / "Release" / "net10.0" / "win-x64" / "publish"
    publish_dir.mkdir(parents=True, exist_ok=True)
    (publish_dir / "DriftBuster.Gui.exe").write_text("binary", encoding="utf-8")
    package_dir = root / "src" / "driftbuster"
    package_dir.mkdir(parents=True, exist_ok=True)
    (package_dir / "__init__.py").write_text("", encoding="utf-8")
    (package_dir / "multi_server.py").write_text("print('ok')\n", encoding="utf-8")

    stage_dir = tmp_path / "lap_temp" / "DriftBuster-Portabletest"
    stage_dir.mkdir(parents=True, exist_ok=True)
    (stage_dir / "stale.txt").write_text("stale", encoding="utf-8")

    monkeypatch.setattr(stage_portable_dev, "ROOT", root)
    monkeypatch.setattr(stage_portable_dev.Path, "cwd", lambda: root)
    monkeypatch.setattr(stage_portable_dev, "read_gui_version", lambda _root: "0.1.0")
    monkeypatch.setattr(
        stage_portable_dev,
        "build_publish",
        lambda _root, configuration, rid: publish_dir,
    )
    monkeypatch.setattr(
        stage_portable_dev,
        "parse_args",
        lambda: SimpleNamespace(
            stage_dir=stage_dir,
            rid="win-x64",
            configuration="Release",
            timestamp="20260305-000000Z",
        ),
    )

    result = stage_portable_dev.main()

    assert result == 0
    assert not (stage_dir / "stale.txt").exists()
    assert (stage_dir / "DriftBuster.Gui.exe").exists()
    assert (stage_dir / "Run-DriftBuster-Debug.cmd").exists()
    assert (stage_dir / "src" / "driftbuster" / "multi_server.py").exists()
    assert (
        root
        / "artifacts"
        / "gui-packaging"
        / "portable"
        / "DriftBuster.Gui-0.1.0-win11-portable-debug-20260305-000000Z.zip"
    ).exists()


def test_main_requires_repo_root(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(stage_portable_dev, "ROOT", tmp_path)
    monkeypatch.setattr(stage_portable_dev.Path, "cwd", lambda: tmp_path / "child")

    with pytest.raises(SystemExit) as exc:
        stage_portable_dev.main()

    assert "Run this script from repository root" in str(exc.value)


def test_stage_bundle_falls_back_when_existing_exe_is_locked(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    bundle_dir = tmp_path / "bundle"
    bundle_dir.mkdir()
    (bundle_dir / "DriftBuster.Gui.exe").write_text("new-binary", encoding="utf-8")
    stage_portable_dev.write_debug_launchers(bundle_dir)

    stage_dir = tmp_path / "stage"
    stage_dir.mkdir()
    (stage_dir / "DriftBuster.Gui.exe").write_text("old-binary", encoding="utf-8")

    def fail_rmtree(_path: Path) -> None:
        raise PermissionError("locked")

    original_copy2 = stage_portable_dev.shutil.copy2

    def copy2_with_locked_exe(src: Path, dst: Path, *args, **kwargs):
        target = Path(dst)
        if target.name == "DriftBuster.Gui.exe":
            raise PermissionError("busy")
        return original_copy2(src, dst, *args, **kwargs)

    monkeypatch.setattr(stage_portable_dev.shutil, "rmtree", fail_rmtree)
    monkeypatch.setattr(stage_portable_dev.shutil, "copy2", copy2_with_locked_exe)

    stage_portable_dev.stage_bundle(bundle_dir, stage_dir)

    assert (stage_dir / "DriftBuster.Gui.next.exe").exists()
    cmd = (stage_dir / "Run-DriftBuster-Debug.cmd").read_text(encoding="utf-8")
    assert "DriftBuster.Gui.next.exe" in cmd
