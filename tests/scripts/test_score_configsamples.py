from __future__ import annotations

from pathlib import Path

from driftbuster.core.detector import Detector
from driftbuster.core.types import DetectionMatch

from scripts import score_configsamples as score


class _RecordingPlugin:
    name = "recording"
    priority = 1

    def detect(self, path: Path, sample: bytes, text: str | None) -> DetectionMatch | None:
        if not sample:
            return None
        return DetectionMatch(
            plugin_name=self.name,
            format_name="structured-config-xml",
            variant=None,
            confidence=0.5,
            reasons=["recording"],
        )


def _make_detector(max_total_sample_bytes: int) -> Detector:
    return Detector(
        plugins=(_RecordingPlugin(),),
        sample_size=256,
        max_total_sample_bytes=max_total_sample_bytes,
        sort_plugins=False,
    )


def test_scan_files_respects_sampling_budget(tmp_path: Path) -> None:
    root = tmp_path / "configs"
    root.mkdir()
    sources: list[Path] = []
    for index in range(5):
        path = root / f"config{index}.yaml"
        path.write_text("x" * 512, encoding="utf-8")
        sources.append(path)

    detector = _make_detector(max_total_sample_bytes=1024)
    outcome = score.scan_files(sources, detector=detector)

    assert outcome.budget_exhausted is True
    assert len(outcome.scanned_files) < len(sources)
    assert detector.sample_budget_remaining == 0


def test_generate_fuzz_inputs_creates_variants(tmp_path: Path) -> None:
    project_root = tmp_path / "repo"
    source_dir = project_root / "configsamples" / "library" / "by-format" / "yaml"
    source_dir.mkdir(parents=True)
    source_file = source_dir / "sample.yaml"
    source_file.write_text("key: value\nsecond: line\n", encoding="utf-8")

    output_dir = tmp_path / "fuzz"
    created = score.generate_fuzz_inputs(
        [source_file],
        root=project_root,
        output_dir=output_dir,
        per_file=3,
        seed=1234,
        max_bytes=32,
    )

    assert len(created) == 3
    for path in created:
        assert path.is_file()
        payload = path.read_bytes()
        assert 0 < len(payload) <= 32
        assert payload != source_file.read_bytes()[: len(payload)]


def test_generate_fuzz_inputs_disabled_without_count(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    file_path = root / "configsamples" / "library" / "by-format" / "text" / "sample.txt"
    file_path.parent.mkdir(parents=True)
    file_path.write_text("payload", encoding="utf-8")

    created = score.generate_fuzz_inputs(
        [file_path],
        root=root,
        output_dir=tmp_path / "out",
        per_file=0,
        seed=99,
    )

    assert created == []
    assert not (tmp_path / "out").exists()
