from __future__ import annotations

import json
from pathlib import Path

import pytest

from driftbuster import run_profiles


def test_save_and_load_profile(tmp_path: Path) -> None:
    profile = run_profiles.RunProfile(
        name="vdi",
        description="VDI configuration set",
        sources=("*.json",),
        options={"sample_size": 65536},
    )

    run_profiles.save_profile(profile, base_dir=tmp_path)

    loaded = run_profiles.load_profile("vdi", base_dir=tmp_path)
    assert loaded.name == "vdi"
    assert loaded.sources == ("*.json",)
    assert loaded.options["sample_size"] == "65536"


def test_execute_profile_collects_files(tmp_path: Path) -> None:
    profile_dir = tmp_path / "configs"
    profile_dir.mkdir()
    (profile_dir / "app.json").write_text("{\"key\": 1}", encoding="utf-8")
    (profile_dir / "web.config").write_text("<configuration />", encoding="utf-8")

    profile = run_profiles.RunProfile(
        name="demo",
        sources=(str(profile_dir),),
        options={},
    )

    result = run_profiles.execute_profile(profile, base_dir=tmp_path)

    assert result.output_dir.parent.parent.name == "demo"
    copied_files = list((result.output_dir).rglob("*"))
    assert any(path.name == "app.json" for path in copied_files)
    metadata = json.loads((result.output_dir / "metadata.json").read_text(encoding="utf-8"))
    assert metadata["profile"]["name"] == "demo"
    assert len(metadata["files"]) == 2


def test_execute_profile_respects_baseline_order(tmp_path: Path) -> None:
    file_a = tmp_path / "a.json"
    file_b = tmp_path / "b.json"
    file_a.write_text("{}", encoding="utf-8")
    file_b.write_text("{}", encoding="utf-8")

    profile = run_profiles.RunProfile(
        name="order",
        sources=(str(file_a), str(file_b)),
        baseline=str(file_b),
    )

    result = run_profiles.execute_profile(profile, base_dir=tmp_path)
    metadata = json.loads((result.output_dir / "metadata.json").read_text(encoding="utf-8"))
    assert metadata["baseline"].endswith("b.json")


def test_execute_profile_rejects_missing_source(tmp_path: Path) -> None:
    profile = run_profiles.RunProfile(
        name="missing",
        sources=(str(tmp_path / "missing.json"),),
    )

    with pytest.raises(FileNotFoundError):
        run_profiles.execute_profile(profile, base_dir=tmp_path)


def test_execute_profile_rejects_missing_glob_base(tmp_path: Path) -> None:
    profile = run_profiles.RunProfile(
        name="globby",
        sources=(str(tmp_path / "ghost" / "*.json"),),
    )

    with pytest.raises(FileNotFoundError, match="Glob base directory not found"):
        run_profiles.execute_profile(profile, base_dir=tmp_path)


def test_execute_profile_requires_baseline_in_sources(tmp_path: Path) -> None:
    source = tmp_path / "data.json"
    source.write_text("{}", encoding="utf-8")

    profile = run_profiles.RunProfile(
        name="baseline",
        sources=(str(source),),
        baseline=str(tmp_path / "other.json"),
    )

    with pytest.raises(ValueError):
        run_profiles.execute_profile(profile, base_dir=tmp_path)
