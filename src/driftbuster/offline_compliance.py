"""Offline packaging compliance validation helpers."""
from __future__ import annotations

from dataclasses import dataclass
import json
import re
from pathlib import Path
from typing import Iterable, Sequence

_SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class ArtifactCheck:
    """Represents the outcome of a single packaging evidence check."""

    name: str
    path: Path | None
    passed: bool
    detail: str | None = None


@dataclass(frozen=True)
class OfflineComplianceReport:
    """Aggregate result of auditing offline packaging artefacts."""

    artifact_root: Path
    checks: tuple[ArtifactCheck, ...]
    issues: tuple[str, ...]

    @property
    def is_compliant(self) -> bool:
        return not self.issues


def check_offline_compliance(artifact_root: Path) -> OfflineComplianceReport:
    """Validate that offline packaging evidence satisfies compliance expectations."""

    checks: list[ArtifactCheck] = []
    issues: list[str] = []

    root = artifact_root.resolve()

    def record(name: str, path: Path | None, passed: bool, detail: str | None = None) -> None:
        checks.append(ArtifactCheck(name=name, path=path, passed=passed, detail=detail))
        if not passed and detail:
            issues.append(detail)

    if not root.exists():
        record(
            "artifact-root",
            root,
            False,
            f"Artifact directory does not exist: {root}",
        )
        return OfflineComplianceReport(root, tuple(checks), tuple(issues))

    if not root.is_dir():
        record(
            "artifact-root",
            root,
            False,
            f"Artifact path is not a directory: {root}",
        )
        return OfflineComplianceReport(root, tuple(checks), tuple(issues))

    record("artifact-root", root, True, "Packaging evidence directory located.")

    readme_path = root / "README.md"
    if not readme_path.is_file():
        record(
            "readme",
            readme_path,
            False,
            "Missing README.md describing offline packaging reproduction steps.",
        )
    else:
        readme_text = readme_path.read_text(encoding="utf-8")
        record("readme", readme_path, True, "README.md present.")
        if "offline" not in readme_text.lower():
            record(
                "readme-offline",
                readme_path,
                False,
                "README.md does not mention offline distribution guidance.",
            )
        else:
            record(
                "readme-offline",
                readme_path,
                True,
                "README.md references offline distribution.",
            )

    for log_name in (
        "publish-framework-dependent.log",
        "publish-self-contained.log",
    ):
        log_path = root / log_name
        if not log_path.is_file():
            record("log", log_path, False, f"Missing {log_name} evidence log.")
            continue

        log_text = log_path.read_text(encoding="utf-8").strip()
        if not log_text:
            record("log", log_path, False, f"Evidence log {log_name} is empty.")
            continue

        line_count = len(log_text.splitlines())
        record(
            "log",
            log_path,
            True,
            f"{log_name} contains {line_count} lines of publish output.",
        )

        lower_log = log_text.lower()
        if "driftbuster.gui" not in lower_log:
            record(
                "log-scope",
                log_path,
                False,
                f"Evidence log {log_name} does not reference DriftBuster.Gui publish output.",
            )
        else:
            record(
                "log-scope",
                log_path,
                True,
                f"{log_name} references DriftBuster.Gui publish output.",
            )

    for checksum_name in (
        "publish-framework-dependent.sha256",
        "publish-self-contained.sha256",
    ):
        checksum_path = root / checksum_name
        if not checksum_path.is_file():
            record(
                "checksum",
                checksum_path,
                False,
                f"Missing {checksum_name} checksum evidence.",
            )
            continue

        digest_detail = _validate_sha256_file(checksum_path)
        if digest_detail:
            record("checksum", checksum_path, True, digest_detail)
        else:
            record(
                "checksum",
                checksum_path,
                False,
                f"Checksum file {checksum_name} does not contain a valid sha256 digest entry.",
            )

    smoke_reports = sorted(root.glob("windows-smoke-tests-*.json"))
    if not smoke_reports:
        record(
            "smoke-reports",
            root,
            False,
            "No windows-smoke-tests-*.json files found for packaging smoke validation.",
        )
    else:
        for smoke_report in smoke_reports:
            issues_for_report = _evaluate_smoke_report(smoke_report)
            if issues_for_report:
                for item in issues_for_report:
                    record("smoke-report", smoke_report, False, item)
            else:
                record(
                    "smoke-report",
                    smoke_report,
                    True,
                    "Smoke test report scenarios pass with offline prerequisites.",
                )

    return OfflineComplianceReport(root, tuple(checks), tuple(issues))


def _validate_sha256_file(path: Path) -> str | None:
    content = path.read_text(encoding="utf-8").strip()
    if not content:
        return None

    first_line = content.splitlines()[0]
    parts = first_line.split()
    if not parts:
        return None

    digest = parts[0]
    if not _SHA256_PATTERN.fullmatch(digest):
        return None

    target = parts[1] if len(parts) > 1 else "<unknown>"
    return f"Checksum digest {digest[:8]}… recorded for {target}" 


def _evaluate_smoke_report(path: Path) -> Sequence[str]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        return [f"Smoke test report {path.name} is not valid JSON: {exc}"]

    scenarios = payload.get("scenarios")
    if not isinstance(scenarios, list) or not scenarios:
        return [f"Smoke test report {path.name} does not define any scenarios."]

    issues: list[str] = []

    for scenario in scenarios:
        platform = scenario.get("platform") or scenario.get("install_type") or "unnamed scenario"
        name = str(platform)

        prereqs = scenario.get("prerequisites")
        if not isinstance(prereqs, Iterable) or isinstance(prereqs, (str, bytes)):
            issues.append(f"{name} prerequisites entry is missing or not a list.")
        else:
            for prereq in prereqs:
                prereq_text = str(prereq)
                lowered = prereq_text.lower()
                if "http://" in lowered or "https://" in lowered:
                    issues.append(
                        f"{name} prerequisite references an online resource: {prereq_text}",
                    )

        result = scenario.get("result")
        if result != "pass":
            issues.append(f"{name} scenario result is '{result}', expected 'pass'.")

    return issues


__all__ = [
    "ArtifactCheck",
    "OfflineComplianceReport",
    "check_offline_compliance",
]
