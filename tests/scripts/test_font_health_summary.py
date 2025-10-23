import json
from datetime import datetime, timezone
from pathlib import Path

import pytest

from driftbuster.font_health import (
    FontHealthError,
    FontHealthReport,
    ScenarioHealth,
    evaluate_report,
    evaluate_scenarios,
    format_report,
    load_font_health_report,
)


@pytest.fixture
def sample_payload(tmp_path: Path) -> Path:
    payload = {
        "generatedAt": "2025-10-23T06:05:33.293680+00:00",
        "scenarios": [
            {
                "name": "passes-release",
                "totalRuns": 4,
                "passes": 4,
                "failures": 0,
                "lastStatus": "pass",
                "lastUpdated": "2025-10-23T06:05:33.289930+00:00",
                "lastDetails": {"glyph_family": "Inter"},
            },
            {
                "name": "flaky-debug",
                "totalRuns": 5,
                "passes": 3,
                "failures": 2,
                "lastStatus": "fail",
                "lastUpdated": "2025-10-23T06:05:31.000000+00:00",
                "lastDetails": {},
            },
        ],
    }
    path = tmp_path / "headless-font-health.json"
    path.write_text(json.dumps(payload))
    return path


def test_load_font_health_report_parses_metadata(sample_payload: Path) -> None:
    report = load_font_health_report(sample_payload)

    assert isinstance(report, FontHealthReport)
    assert report.generated_at == datetime(2025, 10, 23, 6, 5, 33, 293680, tzinfo=timezone.utc)
    assert len(report.scenarios) == 2
    assert report.scenarios[0].name == "passes-release"
    assert report.scenarios[1].failures == 2


def test_evaluate_scenarios_flags_thresholds(sample_payload: Path) -> None:
    report = load_font_health_report(sample_payload)

    evaluations = evaluate_scenarios(
        report.scenarios, max_failure_rate=0.2, min_total_runs=2
    )

    assert evaluations[0].status == "ok"
    assert evaluations[0].issues == ()

    assert evaluations[1].status == "drift"
    assert "failure rate" in evaluations[1].issues[0]
    assert "latest status" in evaluations[1].issues[1]


@pytest.mark.parametrize("missing_key", ["generatedAt", "scenarios"])
def test_load_font_health_report_tolerates_missing_keys(
    sample_payload: Path, missing_key: str
) -> None:
    data = json.loads(sample_payload.read_text())
    data.pop(missing_key, None)
    sample_payload.write_text(json.dumps(data))

    report = load_font_health_report(sample_payload)
    assert isinstance(report, FontHealthReport)


def test_format_report_renders_issues(sample_payload: Path, capsys: pytest.CaptureFixture[str]) -> None:
    report = load_font_health_report(sample_payload)
    evaluation = evaluate_report(report, max_failure_rate=0.1)

    for line in format_report(evaluation):
        print(line)

    output = capsys.readouterr().out
    assert "passes-release" in output
    assert "flaky-debug" in output
    assert "↳ failure rate" in output


def test_load_font_health_report_errors_when_missing_file(tmp_path: Path) -> None:
    missing = tmp_path / "absent.json"
    with pytest.raises(FontHealthError):
        load_font_health_report(missing)


def test_evaluate_report_flags_missing_required_scenarios(sample_payload: Path) -> None:
    report = load_font_health_report(sample_payload)

    evaluation = evaluate_report(
        report,
        max_failure_rate=0.2,
        required_scenarios=("passes-release", "missing-scenario"),
    )

    assert evaluation.has_issues is True
    assert evaluation.missing_scenarios == ("missing-scenario",)


def test_format_report_lists_missing_scenarios(sample_payload: Path, capsys: pytest.CaptureFixture[str]) -> None:
    report = load_font_health_report(sample_payload)
    evaluation = evaluate_report(report, required_scenarios=("Absent",))

    for line in format_report(evaluation):
        print(line)

    output = capsys.readouterr().out
    assert "Missing scenarios:" in output
    assert "Absent" in output


def test_evaluate_report_normalises_required_scenario_names() -> None:
    scenario = ScenarioHealth(
        name="  passes-release  ",
        total_runs=3,
        passes=3,
        failures=0,
        last_status="pass",
        last_updated=None,
        last_details={},
    )
    report = FontHealthReport(generated_at=None, scenarios=(scenario,))

    evaluation = evaluate_report(
        report,
        required_scenarios=("passes-release", "  PASSES-RELEASE  ", ""),
    )

    assert evaluation.missing_scenarios == ()
    assert evaluation.has_issues is False
