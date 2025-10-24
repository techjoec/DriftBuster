import json
from datetime import datetime, timedelta, timezone
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
from scripts import font_health_summary


def _copy_fixture(name: str, destination: Path) -> Path:
    fixture = Path(__file__).resolve().parents[2] / "fixtures" / "font_telemetry" / name
    target = destination / name
    target.write_text(fixture.read_text())
    return target


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


def test_evaluate_scenarios_flags_stale_entries(sample_payload: Path) -> None:
    report = load_font_health_report(sample_payload)

    evaluations = evaluate_scenarios(
        report.scenarios,
        max_failure_rate=1.0,
        max_last_updated_age=timedelta(seconds=1),
        now=datetime(2025, 10, 23, 6, 5, 33, tzinfo=timezone.utc),
    )

    assert evaluations[0].issues == ()
    assert any("lastUpdated is stale" in issue for issue in evaluations[1].issues)


def test_evaluate_scenarios_flags_missing_last_updated_when_required() -> None:
    scenario = ScenarioHealth(
        name="stale-scenario",
        total_runs=1,
        passes=1,
        failures=0,
        last_status="pass",
        last_updated=None,
        last_details={},
    )

    evaluations = evaluate_scenarios(
        (scenario,),
        max_last_updated_age=timedelta(hours=1),
        now=datetime(2025, 1, 1, tzinfo=timezone.utc),
    )

    assert evaluations[0].issues == (
        "missing lastUpdated timestamp; expected telemetry within 1h 0m 0s",
    )


def test_evaluate_report_flags_stale_entries(sample_payload: Path) -> None:
    report = load_font_health_report(sample_payload)

    evaluation = evaluate_report(
        report,
        max_failure_rate=1.0,
        max_last_updated_age=timedelta(seconds=1),
        now=datetime(2025, 10, 23, 6, 5, 33, tzinfo=timezone.utc),
    )

    assert evaluation.has_issues is True
    stale_issues = [issue for item in evaluation.scenarios for issue in item.issues]
    assert any("lastUpdated is stale" in issue for issue in stale_issues)


def test_cli_rejects_negative_max_stale_hours(sample_payload: Path, capsys: pytest.CaptureFixture[str]) -> None:
    with pytest.raises(SystemExit) as exc:
        font_health_summary.main(
            [str(sample_payload), "--max-stale-hours", "-1"]
        )

    assert exc.value.code == 2
    err = capsys.readouterr().err
    assert "--max-stale-hours must be non-negative" in err


def test_cli_flags_stale_exit_code(
    sample_payload: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    class _FixedDateTime(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return datetime(2025, 10, 23, 6, 5, 33, 293680, tzinfo=tz)

    monkeypatch.setattr(font_health_summary, "datetime", _FixedDateTime)

    exit_code = font_health_summary.main(
        [
            str(sample_payload),
            "--max-stale-hours",
            "0.0002",
        ]
    )

    assert exit_code == 1


def test_cli_emits_structured_staleness_log(
    sample_payload: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    log_dir = tmp_path / "font-logs"
    monkeypatch.setenv("FONT_STALENESS_LOG_DIR", str(log_dir))

    class _FixedDateTime(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return datetime(2025, 10, 23, 6, 5, 33, 293680, tzinfo=tz)

    monkeypatch.setattr(font_health_summary, "datetime", _FixedDateTime)

    exit_code = font_health_summary.main(
        [
            str(sample_payload),
            "--max-stale-hours",
            "0.0002",
        ]
    )

    assert exit_code == 1
    written = sorted(log_dir.glob("font-staleness-*.json"))
    event_logs = [path for path in written if "-summary" not in path.name]
    assert len(event_logs) == 1
    payload = json.loads(event_logs[0].read_text())
    assert payload["hasIssues"] is True
    assert payload["maxLastUpdatedAgeSeconds"] == pytest.approx(0.72)
    assert payload["missingScenarios"] == []
    assert payload["scenarios"][1]["issues"]
    assert payload["scenarios"][1]["status"] == "drift"
    assert payload["scenarios"][0]["status"] == "ok"


def test_cli_writes_summary_payload(
    sample_payload: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    log_dir = tmp_path / "font-logs"
    monkeypatch.setenv("FONT_STALENESS_LOG_DIR", str(log_dir))

    class _FixedDateTime(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return datetime(2025, 10, 23, 6, 5, 33, 293680, tzinfo=tz)

    monkeypatch.setattr(font_health_summary, "datetime", _FixedDateTime)

    exit_code = font_health_summary.main(
        [
            str(sample_payload),
            "--max-stale-hours",
            "0.0002",
        ]
    )

    assert exit_code == 1
    summary_path = log_dir / "font-staleness-summary.json"
    assert summary_path.is_file()
    summary = json.loads(summary_path.read_text())
    assert summary["hasIssues"] is True
    assert summary["scenarioCounts"] == {"drift": 1, "ok": 1}
    assert summary["staleScenarioNames"] == ["flaky-debug"]
    assert summary["missingScenarios"] == []
    assert summary["issueCount"] >= 1


def test_cli_log_dir_override_takes_precedence(
    sample_payload: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    env_log_dir = tmp_path / "env-logs"
    override_dir = tmp_path / "override-logs"
    monkeypatch.setenv("FONT_STALENESS_LOG_DIR", str(env_log_dir))

    class _FixedDateTime(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return datetime(2025, 10, 23, 6, 5, 33, 293680, tzinfo=tz)

    monkeypatch.setattr(font_health_summary, "datetime", _FixedDateTime)

    exit_code = font_health_summary.main(
        [
            str(sample_payload),
            "--max-stale-hours",
            "0.0002",
            "--log-dir",
            str(override_dir),
        ]
    )

    assert exit_code == 1
    override_logs = sorted(override_dir.glob("font-staleness-*.json"))
    env_logs = sorted(env_log_dir.glob("font-staleness-*.json"))
    assert override_logs, "expected logs to be written to the override directory"
    assert not env_logs, "env directory should not receive logs when override is used"

    summary_path = override_dir / "font-staleness-summary.json"
    assert summary_path.is_file()


def test_within_threshold_fixture_remains_healthy(tmp_path: Path) -> None:
    payload_path = _copy_fixture("within_threshold.json", tmp_path)
    report = load_font_health_report(payload_path)

    evaluation = evaluate_report(
        report,
        max_failure_rate=0.0,
        max_last_updated_age=timedelta(minutes=20),
        now=datetime(2025, 10, 23, 6, 5, 33, tzinfo=timezone.utc),
    )

    assert evaluation.has_issues is False
    scenario = evaluation.scenarios[0]
    assert scenario.status == "ok"
    assert scenario.issues == ()


def test_stale_threshold_fixture_flags_drift(tmp_path: Path) -> None:
    payload_path = _copy_fixture("stale_threshold.json", tmp_path)
    report = load_font_health_report(payload_path)

    evaluation = evaluate_report(
        report,
        max_failure_rate=0.0,
        max_last_updated_age=timedelta(hours=1),
        now=datetime(2025, 10, 23, 6, 5, 33, tzinfo=timezone.utc),
    )

    assert evaluation.has_issues is True
    scenario = evaluation.scenarios[0]
    assert scenario.status == "drift"
    assert any("lastUpdated is stale" in issue for issue in scenario.issues)
