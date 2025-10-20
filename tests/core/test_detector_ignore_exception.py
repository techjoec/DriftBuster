from pathlib import Path

from driftbuster.core.detector import Detector


class RaisingCfg:
    def __getattribute__(self, name: str):  # type: ignore[override]
        # Force an exception when accessing any attribute
        raise RuntimeError("boom")


class StoreStub:
    def matching_configs(self, tags, *, relative_path):  # type: ignore[no-untyped-def]
        # Return an object that will blow up inside the ignore-review check
        return (RaisingCfg(),)


def test_scan_with_profiles_ignore_exception(monkeypatch, tmp_path: Path):
    # Create a YAML file with tabs to trigger needs_review in YAML plugin
    p = tmp_path / "config.yaml"
    p.write_text("apiVersion: v1\n\tkind: ConfigMap\n")

    detector = Detector()
    # Use our stub store to trigger the exception branch around ignore flag
    res = detector.scan_with_profiles(tmp_path, profile_store=StoreStub())
    assert res and res[0].detection is not None
    # needs_review should still be present (exception causes ignore=False)
    assert (res[0].detection.metadata or {}).get("needs_review") is True

