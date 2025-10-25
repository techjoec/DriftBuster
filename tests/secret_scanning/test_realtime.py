from __future__ import annotations

import json
from pathlib import Path

from driftbuster import run_profiles


def _read_metadata(result: run_profiles.ProfileRunResult) -> dict[str, object]:
    metadata_path = result.output_dir / "metadata.json"
    return json.loads(metadata_path.read_text(encoding="utf-8"))


def test_run_profile_redacts_secret_lines(tmp_path: Path) -> None:
    secret_file = tmp_path / "config.txt"
    secret_file.write_text("password = Hunter12345\n", encoding="utf-8")

    profile = run_profiles.RunProfile(
        name="secret",
        sources=(str(secret_file),),
    )

    result = run_profiles.execute_profile(profile, base_dir=tmp_path)

    copied = next(entry for entry in result.files if entry.source == str(secret_file))
    content = copied.destination.read_text(encoding="utf-8")
    assert "[SECRET]" in content

    metadata = _read_metadata(result)
    secrets = metadata["secrets"]
    assert secrets["rules_loaded"] is True
    findings = secrets["findings"]
    assert isinstance(findings, list) and findings
    finding = findings[0]
    assert finding["rule"] == "PasswordAssignment"
    assert finding["path"].endswith("config.txt")
    assert "[SECRET]" in finding["snippet"]

    assert result.secrets is not None
    assert result.secrets["findings"]


def test_run_profile_respects_ignore_rules(tmp_path: Path) -> None:
    secret_file = tmp_path / "config_ignore.txt"
    secret_file.write_text("password = Hunter12345\n", encoding="utf-8")

    profile = run_profiles.RunProfile(
        name="ignored",
        sources=(str(secret_file),),
        secret_scanner={"ignore_rules": ["PasswordAssignment"]},
    )

    result = run_profiles.execute_profile(profile, base_dir=tmp_path)

    copied = next(entry for entry in result.files if entry.source == str(secret_file))
    content = copied.destination.read_text(encoding="utf-8")
    assert "Hunter12345" in content  # no redaction applied

    metadata = _read_metadata(result)
    secrets = metadata["secrets"]
    assert secrets["findings"] == []
    assert result.secrets is not None
    assert result.secrets["findings"] == []
