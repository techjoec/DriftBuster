from __future__ import annotations

import json
from pathlib import Path

import pytest

from driftbuster.offline_compliance import check_offline_compliance
from scripts import offline_compliance_audit


@pytest.fixture
def sample_artifacts(tmp_path: Path) -> Path:
    root = tmp_path / "artifacts"
    root.mkdir()

    (root / "README.md").write_text(
        "Offline packaging walkthrough with hash manifests.",
        encoding="utf-8",
    )

    for log_name in ("publish-framework-dependent.log", "publish-self-contained.log"):
        (root / log_name).write_text(
            "DriftBuster.Gui -> /fake/path\nPublish completed successfully.",
            encoding="utf-8",
        )

    digest_line = "a" * 64 + "  gui/DriftBuster.Gui/bin/Release/net8.0/win-x64/publish/DriftBuster.Gui.exe"
    for checksum_name in (
        "publish-framework-dependent.sha256",
        "publish-self-contained.sha256",
    ):
        (root / checksum_name).write_text(digest_line, encoding="utf-8")

    smoke_payload = {
        "scenarios": [
            {
                "platform": "Windows 10",
                "install_type": "MSIX",
                "prerequisites": [
                    "WebView2 Evergreen offline installer staged",
                    ".NET Desktop Runtime available locally",
                ],
                "result": "pass",
            }
        ]
    }
    (root / "windows-smoke-tests-2025-02-14.json").write_text(
        json.dumps(smoke_payload),
        encoding="utf-8",
    )

    return root


def test_check_offline_compliance_passes(sample_artifacts: Path) -> None:
    report = check_offline_compliance(sample_artifacts)

    assert report.is_compliant
    assert all(check.passed for check in report.checks)


def test_check_offline_compliance_flags_online_prerequisites(sample_artifacts: Path) -> None:
    report_path = sample_artifacts / "windows-smoke-tests-2025-02-14.json"
    payload = json.loads(report_path.read_text(encoding="utf-8"))
    payload["scenarios"][0]["prerequisites"].append("Download updates from https://example.com")
    report_path.write_text(json.dumps(payload), encoding="utf-8")

    report = check_offline_compliance(sample_artifacts)

    assert not report.is_compliant
    assert any("online resource" in issue for issue in report.issues)


def test_main_reports_failures(sample_artifacts: Path, capsys: pytest.CaptureFixture[str]) -> None:
    (sample_artifacts / "publish-self-contained.sha256").unlink()

    exit_code = offline_compliance_audit.main([str(sample_artifacts)])

    output = capsys.readouterr().out
    assert exit_code == 1
    assert "Missing publish-self-contained.sha256" in output
    assert "FAIL" in output


def test_main_succeeds_when_no_issues(sample_artifacts: Path, capsys: pytest.CaptureFixture[str]) -> None:
    exit_code = offline_compliance_audit.main([str(sample_artifacts)])

    output = capsys.readouterr().out
    assert exit_code == 0
    assert "Offline compliance evidence looks good." in output
    assert "FAIL" not in output
