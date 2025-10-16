from __future__ import annotations

from pathlib import Path

import pytest

from scripts import sync_versions


def test_update_file_replaces_text(tmp_path: Path) -> None:
    target = tmp_path / "config.txt"
    target.write_text("version = \"0.0.0\"\n", encoding="utf-8")

    sync_versions.update_file(target, r"version\s*=\s*\"[^\"]+\"", 'version = "1.2.3"')

    assert target.read_text(encoding="utf-8") == "version = \"1.2.3\"\n"


def test_update_file_raises_when_pattern_missing(tmp_path: Path) -> None:
    target = tmp_path / "config.txt"
    target.write_text("name = \"value\"\n", encoding="utf-8")

    with pytest.raises(sync_versions.SyncError):
        sync_versions.update_file(target, r"version", "replacement")


def test_main_invokes_expected_updates(monkeypatch: pytest.MonkeyPatch) -> None:
    recorded: list[tuple[Path, str, str, int]] = []

    def fake_update_file(path: Path, pattern: str, replacement: str, *, count: int = 0) -> None:
        recorded.append((path, pattern, replacement, count))

    def fake_load_versions() -> dict[str, object]:
        return {
            "core": "1.0.0",
            "catalog": "2.0.0",
            "gui": "3.0.0",
            "powershell": "4.0.0",
            "formats": {"ini": "5.0.0", "json": "6.0.0"},
        }

    monkeypatch.setattr(sync_versions, "update_file", fake_update_file)
    monkeypatch.setattr(sync_versions, "load_versions", fake_load_versions)
    monkeypatch.setattr(sync_versions, "ROOT", Path("/tmp/driftbuster"))

    sync_versions.main()

    assert recorded
    assert any(path.name == "pyproject.toml" for path, *_ in recorded)
    assert any("GuiVersion" in pattern for _path, pattern, *_ in recorded)
