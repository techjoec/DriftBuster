from __future__ import annotations

from pathlib import Path

import pytest

from driftbuster.core import run_profiles


def test_glob_base_directory_variants(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.chdir(tmp_path)
    assert run_profiles._glob_base_directory("*.txt") == tmp_path

    pattern = "configs/*.json"
    expected = tmp_path / "configs"
    result = run_profiles._glob_base_directory(pattern)
    assert result == expected


def test_normalise_options_and_validation_errors(tmp_path: Path) -> None:
    assert run_profiles._normalise_options({"key": None}) == {"key": ""}

    profile = run_profiles.RunProfile(name="empty", sources=())
    with pytest.raises(ValueError):
        run_profiles._validate_profile(profile)

    profile = run_profiles.RunProfile(name="bad", sources=[" "])
    with pytest.raises(ValueError):
        run_profiles._validate_profile(profile)

    missing = tmp_path / "missing.txt"
    profile = run_profiles.RunProfile(name="missing", sources=[str(missing)])
    with pytest.raises(FileNotFoundError):
        run_profiles._validate_profile(profile)

    existing = tmp_path / "file.txt"
    existing.write_text("data", encoding="utf-8")
    profile = run_profiles.RunProfile(
        name="baseline",
        sources=[str(existing)],
        baseline="other",
    )
    with pytest.raises(ValueError):
        run_profiles._validate_profile(profile)

    profile = run_profiles.RunProfile(
        name="baseline-missing",
        sources=[str(existing), "other"],
        baseline="other",
    )
    with pytest.raises(FileNotFoundError):
        run_profiles._validate_profile(profile)

    absolute_missing = tmp_path / "absolute.txt"
    profile = run_profiles.RunProfile(
        name="absent-baseline",
        sources=[str(existing), str(absolute_missing)],
        baseline=str(absolute_missing),
    )
    with pytest.raises(FileNotFoundError):
        run_profiles._validate_profile(profile)


def test_collect_matches_and_copy_file(tmp_path: Path) -> None:
    sample = tmp_path / "sample.txt"
    sample.write_text("content", encoding="utf-8")

    matches = run_profiles._collect_matches(str(sample))
    assert matches == [sample]

    matches = run_profiles._collect_matches(str(tmp_path / "*.txt"))
    assert sample in matches

    with pytest.raises(FileNotFoundError):
        run_profiles._collect_matches(str(tmp_path / "absent"))

    destination_root = tmp_path / "dest"
    destination_root.mkdir()
    copied = run_profiles._copy_file(
        source=str(sample),
        file=sample,
        base=tmp_path / "other",
        destination_root=destination_root,
    )
    assert copied.destination.exists()
    assert copied.sha256


def test_validate_profile_baseline_glob_missing(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.chdir(tmp_path)
    data = tmp_path / "data.txt"
    data.write_text("value", encoding="utf-8")

    baseline_pattern = str(tmp_path / "missing" / "*.cfg")
    profile = run_profiles.RunProfile(
        name="glob",
        sources=[str(data), baseline_pattern],
        baseline=baseline_pattern,
    )

    with pytest.raises(FileNotFoundError):
        run_profiles._validate_profile(profile)


def test_execute_profile_orders_baseline_first(tmp_path: Path, monkeypatch) -> None:
    baseline = tmp_path / "baseline.txt"
    baseline.write_text("base", encoding="utf-8")
    other = tmp_path / "other.txt"
    other.write_text("other", encoding="utf-8")

    profile = run_profiles.RunProfile(
        name="ordering",
        sources=[str(other), str(baseline)],
        baseline=str(baseline),
    )

    result = run_profiles.execute_profile(profile, base_dir=tmp_path, timestamp="20240101T000000Z")
    run_dir = tmp_path / "Profiles" / "ordering" / "raw" / "20240101T000000Z"
    assert result.output_dir == run_dir
    # Baseline should be processed first resulting in source_00 being baseline file.
    baseline_dest = run_dir / "source_00"
    assert any(entry.destination.is_relative_to(baseline_dest) for entry in result.files)
