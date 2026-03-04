from __future__ import annotations

import csv
from pathlib import Path

import pytest

from scripts import coverage_history


def test_append_history_writes_changed_dotnet_column(tmp_path: Path) -> None:
    output = tmp_path / "history.csv"

    coverage_history.append_history(
        output_path=output,
        timestamp="2026-03-04T08:00:00+00:00",
        python_percent=93.41,
        dotnet_percent=81.54,
        dotnet_changed_percent=90.68,
        watch_lowest=90.91,
        notes="verify",
    )

    with output.open("r", encoding="utf-8", newline="") as fh:
        rows = list(csv.reader(fh))

    assert rows[0] == [
        "timestamp_utc",
        "python_percent",
        "dotnet_percent",
        "dotnet_changed_percent",
        "python_watch_min",
        "notes",
    ]
    assert rows[1] == [
        "2026-03-04T08:00:00+00:00",
        "93.41",
        "81.54",
        "90.68",
        "90.91",
        "verify",
    ]


def test_append_history_migrates_legacy_layout_when_existing_file_is_legacy(tmp_path: Path) -> None:
    output = tmp_path / "legacy-history.csv"
    output.write_text(
        "timestamp_utc,python_percent,dotnet_percent,python_watch_min,notes\n"
        "2026-03-03T00:00:00+00:00,93.00,81.00,90.00,old\n",
        encoding="utf-8",
    )

    coverage_history.append_history(
        output_path=output,
        timestamp="2026-03-04T08:00:00+00:00",
        python_percent=93.41,
        dotnet_percent=81.54,
        dotnet_changed_percent=90.68,
        watch_lowest=90.91,
        notes="verify",
    )

    with output.open("r", encoding="utf-8", newline="") as fh:
        rows = list(csv.reader(fh))

    assert rows[0] == [
        "timestamp_utc",
        "python_percent",
        "dotnet_percent",
        "dotnet_changed_percent",
        "python_watch_min",
        "notes",
    ]
    assert rows[1] == [
        "2026-03-03T00:00:00+00:00",
        "93.00",
        "81.00",
        "",
        "90.00",
        "old",
    ]
    assert rows[-1] == [
        "2026-03-04T08:00:00+00:00",
        "93.41",
        "81.54",
        "90.68",
        "90.91",
        "verify",
    ]


def test_compute_dotnet_changed_percent_uses_changed_ratio(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(coverage_history, "find_cobertura_xml", lambda _root: "fake.xml")
    monkeypatch.setattr(
        coverage_history,
        "load_cobertura_summary",
        lambda _path: (0.81, [], [], {"gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs": {10: 1}}),
    )
    monkeypatch.setattr(
        coverage_history,
        "load_changed_production_lines",
        lambda _base: {"gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs": {10}},
    )
    monkeypatch.setattr(
        coverage_history,
        "summarise_changed_dotnet_lines",
        lambda _changed, _hits: (0.9068, [], []),
    )

    percent = coverage_history.compute_dotnet_changed_percent(Path("artifacts/coverage-dotnet"), "origin/main")

    assert percent == 90.68


def test_compute_dotnet_changed_percent_returns_none_without_coverage_report(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(coverage_history, "find_cobertura_xml", lambda _root: None)

    percent = coverage_history.compute_dotnet_changed_percent(Path("artifacts/coverage-dotnet"), "origin/main")

    assert percent is None
