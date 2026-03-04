from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from scripts.coverage_report import (
    coverage_path_candidates,
    load_changed_production_lines,
    load_cobertura_summary,
    summarise_changed_dotnet_lines,
)


def test_coverage_path_candidates_adds_repo_relative_alias() -> None:
    candidates = coverage_path_candidates("DriftBuster.Gui/ViewModels/MainWindowViewModel.cs")

    assert "DriftBuster.Gui/ViewModels/MainWindowViewModel.cs" in candidates
    assert "gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs" in candidates


def test_load_changed_production_lines_parses_hunks_and_filters_non_production(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    diff = """diff --git a/gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs b/gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs
+++ b/gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs
@@ -10,0 +11,2 @@
+line a
+line b
@@ -30,1 +33 @@
+line c
diff --git a/gui/DriftBuster.Gui.Tests/ViewModels/MainWindowViewModelTests.cs b/gui/DriftBuster.Gui.Tests/ViewModels/MainWindowViewModelTests.cs
+++ b/gui/DriftBuster.Gui.Tests/ViewModels/MainWindowViewModelTests.cs
@@ -1 +1 @@
+ignored
"""

    def fake_check_output(*_args, **_kwargs) -> str:
        return diff

    monkeypatch.setattr(subprocess, "check_output", fake_check_output)

    changed = load_changed_production_lines("origin/main")

    assert changed == {
        "gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs": {11, 12, 33}
    }


def test_load_changed_production_lines_returns_empty_when_git_unavailable(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def fake_check_output(*_args, **_kwargs) -> str:
        raise subprocess.CalledProcessError(returncode=1, cmd=["git"])

    monkeypatch.setattr(subprocess, "check_output", fake_check_output)

    assert load_changed_production_lines("origin/main") == {}


def test_summarise_changed_dotnet_lines_counts_only_executable_lines() -> None:
    changed = {
        "gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs": {10, 11, 12},
        "gui/DriftBuster.Gui/ViewModels/NoExecutableHitMap.cs": {5},
    }
    line_hits = {
        "gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs": {
            10: 1,
            11: 0,
            # 12 intentionally omitted (non-executable)
        }
    }

    ratio, details, skipped = summarise_changed_dotnet_lines(changed, line_hits)

    assert ratio == 0.5
    assert details == [
        (
            "gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs",
            1,
            2,
            0.5,
        )
    ]
    assert skipped == ["gui/DriftBuster.Gui/ViewModels/NoExecutableHitMap.cs"]


def test_summarise_changed_dotnet_lines_returns_none_when_no_executable_lines() -> None:
    ratio, details, skipped = summarise_changed_dotnet_lines(
        {"gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs": {1, 2}},
        {},
    )

    assert ratio is None
    assert details == []
    assert skipped == ["gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs"]


def test_load_cobertura_summary_builds_line_hit_aliases(tmp_path: Path) -> None:
    xml = """<?xml version="1.0" ?>
<coverage line-rate="0.5">
  <packages>
    <package name="DriftBuster.Gui">
      <classes>
        <class name="MainWindowViewModel" filename="DriftBuster.Gui/ViewModels/MainWindowViewModel.cs" line-rate="0.5">
          <lines>
            <line number="10" hits="1"/>
            <line number="11" hits="0"/>
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"""
    path = tmp_path / "coverage.cobertura.xml"
    path.write_text(xml, encoding="utf-8")

    _line_rate, _classes, _files, line_hits = load_cobertura_summary(str(path))

    assert line_hits["DriftBuster.Gui/ViewModels/MainWindowViewModel.cs"][10] == 1
    assert line_hits["gui/DriftBuster.Gui/ViewModels/MainWindowViewModel.cs"][11] == 0
