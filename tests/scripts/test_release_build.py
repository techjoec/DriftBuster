from __future__ import annotations

import builtins
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

from scripts import release_build


def test_ensure_dependency_raises_for_missing(monkeypatch: pytest.MonkeyPatch) -> None:
    original_import = builtins.__import__

    def fake_import(name: str, *args: Any, **kwargs: Any):
        if name == "missing_module":
            raise ImportError("boom")
        return original_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", fake_import)

    with pytest.raises(SystemExit) as exc:
        release_build.ensure_dependency("missing_module")

    assert "Missing dependency 'missing_module'" in str(exc.value)


def test_run_invokes_subprocess(monkeypatch: pytest.MonkeyPatch) -> None:
    captured: list[list[str]] = []

    def fake_run(command: list[str], *, cwd: Path | None = None, check: bool) -> None:
        captured.append(command)
        assert check is True

    monkeypatch.setattr(release_build.subprocess, "run", fake_run)

    release_build.run(["echo", "hello"], cwd=None)

    assert captured == [["echo", "hello"]]


def test_clean_artifacts_resets_directories(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    build_root = tmp_path / "build"
    python_dir = build_root / "artifacts" / "python"
    gui_dir = build_root / "artifacts" / "gui"

    (python_dir / "placeholder.txt").parent.mkdir(parents=True, exist_ok=True)
    (python_dir / "placeholder.txt").write_text("data", encoding="utf-8")

    monkeypatch.setattr(release_build, "REPO_ROOT", tmp_path)
    monkeypatch.setattr(release_build, "PY_ARTIFACT_DIR", python_dir)
    monkeypatch.setattr(release_build, "GUI_ARTIFACT_DIR", gui_dir)

    release_build.clean_artifacts()

    assert python_dir.exists()
    assert gui_dir.exists()
    assert not (python_dir / "placeholder.txt").exists()


def test_run_tests_honours_skip(monkeypatch: pytest.MonkeyPatch) -> None:
    commands: list[tuple[list[str], Path | None, bool]] = []

    def fake_subprocess(command: list[str], *, cwd: Path | None = None, check: bool) -> None:
        commands.append((command, cwd, check))

    monkeypatch.setattr(release_build.subprocess, "run", fake_subprocess)

    release_build.run_tests(skip_tests=True)
    assert commands == []

    release_build.run_tests(skip_tests=False)
    assert commands[0][0][0:3] == [release_build.sys.executable, "-m", "pytest"]
    assert commands[0][2] is True


def test_main_executes_pipeline(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: list[Any] = []

    monkeypatch.setattr(release_build.Path, "cwd", lambda: release_build.REPO_ROOT)
    monkeypatch.setattr(release_build, "clean_artifacts", lambda: calls.append("clean"))
    monkeypatch.setattr(
        release_build,
        "run_tests",
        lambda skip_tests: calls.append(("tests", skip_tests)),
    )
    monkeypatch.setattr(
        release_build,
        "build_python_package",
        lambda: calls.append("python"),
    )
    monkeypatch.setattr(
        release_build,
        "build_gui",
        lambda runtime, self_contained: calls.append(("gui", runtime, self_contained)),
    )
    monkeypatch.setattr(
        release_build,
        "parse_args",
        lambda: SimpleNamespace(skip_tests=False, runtime=None, self_contained=False),
    )

    result = release_build.main()

    assert result == 0
    assert calls == ["clean", ("tests", False), "python", ("gui", None, False)]


def test_main_requires_repository_root(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(release_build.Path, "cwd", lambda: release_build.REPO_ROOT / "child")

    with pytest.raises(SystemExit) as exc:
        release_build.main()

    assert "Run this script from the repository root" in str(exc.value)
