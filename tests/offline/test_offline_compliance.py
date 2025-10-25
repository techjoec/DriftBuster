from __future__ import annotations

import json

from driftbuster.offline_compliance import check_offline_compliance


def _read_detail(report, name: str) -> list[str]:
    return [check.detail or "" for check in report.checks if check.name == name]


def test_check_offline_compliance_missing_root(tmp_path):
    target = tmp_path / "does-not-exist"
    report = check_offline_compliance(target)

    assert not report.is_compliant
    assert any("does not exist" in detail for detail in _read_detail(report, "artifact-root"))
    assert report.artifact_root == target.resolve()


def test_check_offline_compliance_not_directory(tmp_path):
    file_path = tmp_path / "evidence.txt"
    file_path.write_text("placeholder", encoding="utf-8")

    report = check_offline_compliance(file_path)

    assert not report.is_compliant
    assert any("not a directory" in detail for detail in _read_detail(report, "artifact-root"))


def test_check_offline_compliance_success_bundle(tmp_path):
    bundle = tmp_path / "bundle"
    bundle.mkdir()

    (bundle / "README.md").write_text(
        "Offline packaging checklist\n- ensure offline prerequisites documented\n",
        encoding="utf-8",
    )

    for name in ("publish-framework-dependent.log", "publish-self-contained.log"):
        (bundle / name).write_text(
            "DriftBuster.Gui publish output completed successfully\n",
            encoding="utf-8",
        )

    for name in ("publish-framework-dependent.sha256", "publish-self-contained.sha256"):
        (bundle / name).write_text("a" * 64 + f"  {name}.zip\n", encoding="utf-8")

    smoke_payload = {
        "scenarios": [
            {
                "platform": "Windows 11",
                "prerequisites": ["Stage offline installer"],
                "result": "pass",
            }
        ]
    }
    (bundle / "windows-smoke-tests-20251025.json").write_text(
        json.dumps(smoke_payload),
        encoding="utf-8",
    )

    report = check_offline_compliance(bundle)

    assert report.is_compliant
    # ensure checksum helper surfaces digest detail
    checksum_details = _read_detail(report, "checksum")
    assert any("Checksum digest" in detail for detail in checksum_details)


def test_check_offline_compliance_detects_issues(tmp_path):
    bundle = tmp_path / "bundle-issues"
    bundle.mkdir()

    (bundle / "README.md").write_text("Packaging steps", encoding="utf-8")

    # Create one log lacking DriftBuster.Gui scope and omit the second entirely.
    (bundle / "publish-framework-dependent.log").write_text(
        "publish completed without expected scope marker\n",
        encoding="utf-8",
    )

    # Invalid checksum entries.
    (bundle / "publish-framework-dependent.sha256").write_text("invalid-digest\n", encoding="utf-8")
    (bundle / "publish-self-contained.sha256").write_text("", encoding="utf-8")

    # Smoke report with invalid JSON and one with online prerequisites + failing result.
    (bundle / "windows-smoke-tests-invalid.json").write_text("{not-json", encoding="utf-8")
    failing_scenarios = {
        "scenarios": [
            {
                "platform": "Windows 10",
                "prerequisites": "https://example.com/setup",
                "result": "fail",
            }
        ]
    }
    (bundle / "windows-smoke-tests-issues.json").write_text(
        json.dumps(failing_scenarios),
        encoding="utf-8",
    )

    report = check_offline_compliance(bundle)

    assert not report.is_compliant
    issues_text = "\n".join(report.issues)
    assert "README.md" in issues_text
    assert "Missing publish-self-contained.log" in issues_text
    assert "does not reference DriftBuster.Gui" in issues_text
    assert "does not contain a valid sha256" in issues_text
    assert "is not valid JSON" in issues_text
    assert "prerequisites entry is missing" in issues_text
    assert "scenario result is 'fail'" in issues_text


def test_check_offline_compliance_requires_smoke_reports(tmp_path):
    bundle = tmp_path / "bundle-no-smoke"
    bundle.mkdir()

    (bundle / "README.md").write_text("Offline doc", encoding="utf-8")

    report = check_offline_compliance(bundle)

    assert not report.is_compliant
    assert any("No windows-smoke-tests" in issue for issue in report.issues)
