from __future__ import annotations

from pathlib import Path

import pytest

from driftbuster.core.detector import (
    Detector,
    DetectorIOError,
    _normalise_reasons,
    scan_file,
    scan_path,
)
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


def test_detector_rejects_invalid_sample_size() -> None:
    with pytest.raises(ValueError):
        Detector(sample_size=0)


def test_detector_handles_io_error(tmp_path: Path, monkeypatch) -> None:
    target = tmp_path / "missing.txt"

    captured: list[Path] = []

    def on_error(path: Path, exc: Exception) -> None:
        captured.append(path)

    detector = Detector(on_error=on_error)
    with pytest.raises(FileNotFoundError):
        detector.scan_file(target)
    assert captured == []  # File existence check happens before callback

    target.write_text("data", encoding="utf-8")

    errors: list[Exception] = []

    def collect(path: Path, exc: Exception) -> None:
        captured.append(path)
        errors.append(exc)

    detector = Detector(on_error=collect)
    monkeypatch.setattr(Path, "open", lambda self, mode="rb", **_: (_ for _ in ()).throw(OSError("boom")))
    with pytest.raises(DetectorIOError):
        detector.scan_file(target)
    assert captured[-1] == target
    assert errors and "boom" in str(errors[-1])


def test_scan_path_handles_errors(tmp_path: Path, monkeypatch) -> None:
    root = tmp_path / "root"
    root.mkdir()
    (root / "a.txt").write_text("data", encoding="utf-8")

    calls: list[Path] = []

    def collect(path: Path, exc: Exception) -> None:
        calls.append(path)

    detector = Detector(on_error=collect)

    def boom_glob(self, pattern="**/*"):
        raise OSError("nope")

    monkeypatch.setattr(Path, "glob", boom_glob)
    with pytest.raises(DetectorIOError):
        detector.scan_path(root)
    assert calls and calls[-1] == root


def test_scan_with_profiles_requires_store(tmp_path: Path) -> None:
    detector = Detector()
    with pytest.raises(ValueError):
        detector.scan_with_profiles(tmp_path / "file", profile_store=None)  # type: ignore[arg-type]


def test_scan_path_convenience_with_file(tmp_path: Path) -> None:
    target = tmp_path / "file.txt"
    target.write_text("content", encoding="utf-8")
    results = scan_path(target)
    assert results and results[0][0] == target


def test_scan_file_convenience(tmp_path: Path) -> None:
    target = tmp_path / "data.txt"
    target.write_text("payload", encoding="utf-8")

    class SimplePlugin:
        name = "simple"
        priority = 1

        def detect(self, path: Path, sample: bytes, text: str | None):
            if text is None:
                return None
            return DetectionMatch(
                plugin_name=self.name,
                format_name="xml",
                variant=None,
                confidence=0.5,
                reasons=["manual"],
            )

    match = scan_file(target, plugins=(SimplePlugin(),), sort_plugins=False)
    assert match is not None and match.plugin_name == "simple"


def test_normalise_reasons_deduplicates_and_titleises() -> None:
    reasons = [" sample-token:value ", "", "sample-token:value", "data-loaded"]
    normalised = _normalise_reasons(reasons)
    assert normalised == ["Sample-Token:Value", "Data-Loaded"]


def test_handle_error_without_cause(tmp_path: Path) -> None:
    detector = Detector()
    error = DetectorIOError(tmp_path, "failed")
    with pytest.raises(DetectorIOError) as exc:
        detector._handle_error(tmp_path, error)
    assert exc.value is error
