from __future__ import annotations

import json
from pathlib import Path

import pytest

from driftbuster import run_profiles
from driftbuster.core import run_profiles as core_run_profiles


def test_save_and_load_profile(tmp_path: Path) -> None:
    profile = run_profiles.RunProfile(
        name="vdi",
        description="VDI configuration set",
        sources=("*.json",),
        options={"sample_size": 65536},
        secret_scanner={"ignore_rules": ["db"], "ignore_patterns": ["ALLOW"]},
    )

    run_profiles.save_profile(profile, base_dir=tmp_path)

    loaded = run_profiles.load_profile("vdi", base_dir=tmp_path)
    assert loaded.name == "vdi"
    assert loaded.sources == ("*.json",)
    assert loaded.options["sample_size"] == "65536"
    assert tuple(loaded.secret_scanner.get("ignore_rules", ())) == ("db",)
    assert tuple(loaded.secret_scanner.get("ignore_patterns", ())) == ("ALLOW",)


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
    assert metadata["secrets"]["findings"] == []
    assert "ruleset_version" in metadata["secrets"]
    snapshot = result.to_dict()
    assert snapshot["profile"]["name"] == "demo"
    assert snapshot["secrets"]["findings"] == []


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


def test_load_profile_missing(tmp_path: Path) -> None:
    with pytest.raises(FileNotFoundError):
        run_profiles.load_profile("absent", base_dir=tmp_path)


def test_execute_profile_baseline_glob_missing_base(tmp_path: Path) -> None:
    source = tmp_path / "exists.txt"
    source.write_text("data", encoding="utf-8")

    baseline_glob = str(tmp_path / "missing" / "*.txt")
    profile = run_profiles.RunProfile(
        name="glob",
        sources=(baseline_glob, str(source)),
        baseline=baseline_glob,
    )

    with pytest.raises(FileNotFoundError, match="Glob base directory not found"):
        run_profiles.execute_profile(profile, base_dir=tmp_path)


def test_execute_profile_baseline_glob_with_existing_base(tmp_path: Path) -> None:
    base_dir = tmp_path / "glob"
    base_dir.mkdir()
    (base_dir / "one.txt").write_text("1", encoding="utf-8")

    baseline_glob = str(base_dir / "*.txt")
    profile = run_profiles.RunProfile(
        name="glob-existing",
        sources=(baseline_glob,),
        baseline=baseline_glob,
    )

    result = run_profiles.execute_profile(profile, base_dir=tmp_path)
    assert any(file.source == baseline_glob for file in result.files)


def test_execute_profile_baseline_missing_path(tmp_path: Path) -> None:
    existing = tmp_path / "exists.txt"
    existing.write_text("data", encoding="utf-8")

    baseline_path = str(tmp_path / "missing.txt")
    profile = run_profiles.RunProfile(
        name="baseline-missing",
        sources=(baseline_path, str(existing)),
        baseline=baseline_path,
    )

    with pytest.raises(FileNotFoundError, match="Path does not exist"):
        run_profiles.execute_profile(profile, base_dir=tmp_path)


def test_copy_file_handles_non_relative_paths(tmp_path: Path, monkeypatch) -> None:
    source = tmp_path / "outside.txt"
    source.write_text("content", encoding="utf-8")
    destination_root = tmp_path / "dest"
    destination_root.mkdir()

    profile_file = core_run_profiles._copy_file(
        source=str(source),
        file=source,
        base=source.parent.parent,  # intentionally not parent
        destination_root=destination_root,
    )

    assert profile_file.destination.exists()
    assert profile_file.destination.name == source.name


def test_collect_matches_returns_glob_results(tmp_path: Path) -> None:
    match = tmp_path / "data"
    match.mkdir()
    (match / "one.txt").write_text("1", encoding="utf-8")
    (match / "two.txt").write_text("2", encoding="utf-8")

    results = core_run_profiles._collect_matches(str(match / "*.txt"))
    assert len(results) == 2


def test_validate_profile_glob_base_missing(tmp_path: Path) -> None:
    pattern = str(tmp_path / "missing" / "*.log")
    profile = core_run_profiles.RunProfile(name="glob", sources=(pattern,), baseline=None)
    with pytest.raises(FileNotFoundError):
        core_run_profiles._validate_profile(profile)


def test_validate_profile_baseline_missing_path(tmp_path: Path) -> None:
    missing = str(tmp_path / "nope.txt")
    profile = core_run_profiles.RunProfile(name="base", sources=(missing,), baseline=missing)
    with pytest.raises(FileNotFoundError):
        core_run_profiles._validate_profile(profile)
