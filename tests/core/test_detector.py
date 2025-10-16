from __future__ import annotations

from pathlib import Path

import pytest

from driftbuster.core.detector import (
    Detector,
    DetectorIOError,
    _normalise_reasons,
    _titleise_component,
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
    assert match.metadata["catalog_version"] == "0.0.2"
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


def test_scan_path_rejects_unknown_root(tmp_path: Path) -> None:
    with pytest.raises(DetectorIOError) as exc:
        Detector().scan_path(tmp_path / "missing" / "dir")
    assert "Path does not exist" in str(exc.value)


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


def test_titleise_component_handles_edge_cases() -> None:
    assert _titleise_component("") == ""
    assert _titleise_component("12345") == "12345"


def test_scan_path_swallows_glob_errors_when_handler_suppresses(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    class TolerantDetector(Detector):
        def __init__(self) -> None:
            super().__init__(plugins=(), sort_plugins=False)
            self.errors: list[tuple[Path, Exception]] = []

        def _handle_error(
            self,
            path: Path,
            error: DetectorIOError,
            *,
            cause: Exception | None = None,
        ) -> None:  # type: ignore[override]
            self.errors.append((path, error))

    root = tmp_path / "root"
    root.mkdir()

    detector = TolerantDetector()

    original_glob = Path.glob

    def failing_glob(self: Path, pattern: str = "**/*"):
        if self == root:
            raise OSError("boom")
        return original_glob(self, pattern)

    monkeypatch.setattr(Path, "glob", failing_glob)

    results = detector.scan_path(root)

    assert results == []
    assert detector.errors and detector.errors[0][0] == root


def test_scan_path_continues_when_individual_file_errors(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    class TolerantDetector(Detector):
        def __init__(self) -> None:
            super().__init__(plugins=(), sort_plugins=False)
            self.errors: list[tuple[Path, Exception]] = []

        def _handle_error(
            self,
            path: Path,
            error: DetectorIOError,
            *,
            cause: Exception | None = None,
        ) -> None:  # type: ignore[override]
            self.errors.append((path, error))

    root = tmp_path / "root"
    root.mkdir()
    bad_file = root / "bad.txt"
    bad_file.write_text("bad", encoding="utf-8")
    good_file = root / "good.txt"
    good_file.write_text("good", encoding="utf-8")

    detector = TolerantDetector()

    original_is_file = Path.is_file

    def flaky_is_file(self: Path) -> bool:
        if self == bad_file:
            raise OSError("unreadable")
        return original_is_file(self)

    monkeypatch.setattr(Path, "is_file", flaky_is_file)

    results = detector.scan_path(root)

    assert (good_file, None) in results
    assert detector.errors and detector.errors[0][0] == bad_file


def test_scan_with_profiles_falls_back_to_filename(tmp_path: Path) -> None:
    class DummyDetector(Detector):
        def __init__(self, paths: list[tuple[Path, None]]) -> None:
            super().__init__(plugins=(), sort_plugins=False)
            self._paths = paths

        def scan_path(self, root: Path, glob: str = "**/*") -> list[tuple[Path, None]]:
            return self._paths

    class RecordingStore:
        def __init__(self) -> None:
            self.paths: list[str | None] = []

        def matching_configs(self, tags: tuple[str, ...], *, relative_path: str | None) -> list[object]:
            self.paths.append(relative_path)
            return []

    store = RecordingStore()

    outside = tmp_path.parent / "external.txt"
    outside.write_text("content", encoding="utf-8")

    detector = DummyDetector([(outside, None)])
    detector.scan_with_profiles(tmp_path, profile_store=store, tags=None)

    assert store.paths == [outside.name]

    store.paths.clear()
    source_file = tmp_path / "file.txt"
    source_file.write_text("data", encoding="utf-8")

    detector = DummyDetector([(source_file, None)])
    detector.scan_with_profiles(source_file, profile_store=store, tags=None)

    assert store.paths == [source_file.name]


def test_scan_file_returns_none_when_handler_suppresses(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    target = tmp_path / "data.txt"
    target.write_text("content", encoding="utf-8")

    class SuppressingDetector(Detector):
        def __init__(self) -> None:
            super().__init__(plugins=(), sort_plugins=False)
            self.errors: list[Exception] = []

        def _handle_error(self, path: Path, error: DetectorIOError, *, cause: Exception | None = None) -> None:  # type: ignore[override]
            self.errors.append(error)

    detector = SuppressingDetector()

    def explode_open(self, *args, **kwargs):  # type: ignore[override]
        raise OSError("boom")

    monkeypatch.setattr(Path, "open", explode_open)

    result = detector.scan_file(target)
    assert result is None
    assert detector.errors
