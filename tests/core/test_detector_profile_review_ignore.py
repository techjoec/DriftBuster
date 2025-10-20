from pathlib import Path
from textwrap import dedent

from driftbuster.core.detector import Detector
from driftbuster.core.profiles import ConfigurationProfile, ProfileConfig, ProfileStore


def test_scan_with_profiles_review_ignore(tmp_path: Path) -> None:
    # Create a YAML file with tabs to trigger needs_review in YAML plugin
    p = tmp_path / "config.yaml"
    p.write_text("apiVersion: v1\n\tkind: ConfigMap\n")

    # Profile that applies to this path and ignores review flags
    profile = ConfigurationProfile(
        name="default",
        configs=(
            ProfileConfig(
                identifier="cfg1",
                path="config.yaml",
                metadata={"ignore_review_flags": True},
            ),
        ),
    )
    store = ProfileStore([profile])

    detector = Detector()
    results = detector.scan_with_profiles(tmp_path, profile_store=store)
    assert results and results[0].detection is not None
    md = results[0].detection.metadata or {}
    # needs_review should be suppressed and review_ignored marked
    assert md.get("review_ignored") is True
    assert md.get("needs_review") is False

