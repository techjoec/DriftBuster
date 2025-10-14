from __future__ import annotations

from pathlib import Path

import pytest

from driftbuster.core.detector import Detector
from driftbuster.core.profiles import ConfigurationProfile, ProfileConfig, ProfileStore
from driftbuster.core.types import DetectionMatch


class _XmlRecordingPlugin:
    name = "test-xml-recorder"
    priority = 5

    def detect(self, path: Path, sample: bytes, text: str | None) -> DetectionMatch | None:
        if text is None:
            return None
        metadata = {"sample_length": len(sample)}
        return DetectionMatch(
            plugin_name=self.name,
            format_name="xml",
            variant="generic",
            confidence=0.6,
            reasons=["matched fixture"],
            metadata=metadata,
        )


def test_scan_file_enriches_metadata(tmp_path: Path) -> None:
    target = tmp_path / "sample.xml"
    target.write_text("<?xml version=\"1.0\"?><root><value>text</value></root>", encoding="utf-8")

    detector = Detector(plugins=(_XmlRecordingPlugin(),), sample_size=4, sort_plugins=False)
    match = detector.scan_file(target)

    assert match is not None
    assert match.plugin_name == "test-xml-recorder"
    assert match.metadata is not None
    assert match.metadata["catalog_version"] == "0.0.1"
    assert match.metadata["catalog_format"] == "xml"
    assert match.metadata["catalog_variant"] == "generic"
    assert match.metadata["bytes_sampled"] == 4
    assert match.metadata["encoding"] == "utf-8"
    assert match.metadata["sample_truncated"] is True
    assert "Decoded Content Using Utf-8 Encoding" in match.reasons
    assert "Truncated Sample To 4B" in match.reasons


def test_detector_clamps_requested_sample_size(tmp_path: Path) -> None:
    target = tmp_path / "large.bin"
    target.write_bytes(b"a" * 600_000)

    detector = Detector(plugins=(_XmlRecordingPlugin(),), sample_size=600_000, sort_plugins=False)
    match = detector.scan_file(target)

    assert match is not None
    assert match.metadata is not None
    assert match.metadata["bytes_sampled"] == 512 * 1024
    assert match.metadata["sample_truncated"] is True
    assert match.metadata["sample_length"] == 512 * 1024


def test_scan_with_profiles_attaches_matches(tmp_path: Path) -> None:
    target_dir = tmp_path / "configs"
    target_dir.mkdir()
    sample = target_dir / "appsettings.json"
    sample.write_text("{\"Logging\": {\"LogLevel\": \"Information\"}}", encoding="utf-8")

    detector = Detector()

    profile = ConfigurationProfile(
        name="prod",
        tags={"prod"},
        configs=(
            ProfileConfig(
                identifier="cfg-prod",
                path="appsettings.json",
                expected_format="json",
                expected_variant="structured-settings-json",
            ),
        ),
    )
    store = ProfileStore([profile])

    results = detector.scan_with_profiles(
        target_dir,
        profile_store=store,
        tags=["prod"],
    )

    assert len(results) == 1
    profiled = results[0]
    assert profiled.detection is not None
    assert profiled.detection.format_name == "json"
    assert profiled.profiles
    applied = profiled.profiles[0]
    assert applied.profile.name == "prod"
    assert applied.config.identifier == "cfg-prod"
