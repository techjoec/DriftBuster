from __future__ import annotations

from pathlib import Path

from driftbuster.core.detector import Detector


def run_detect(text: str, name: str = "scan.regscan.json"):
    p = Path("artifacts/tmp/registry_live_" + name)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")
    try:
        det = Detector(sample_size=4096)
        return det.scan_file(p)
    finally:
        try:
            p.unlink()
        except Exception:
            pass


def test_detects_json_manifest_with_metadata():
    payload = {
        "registry_scan": {
            "token": "VendorA AppA",
            "keywords": ["server", "api"],
            "patterns": ["https://", "api\\.internal\\.local"],
            "max_depth": 8,
            "max_hits": 50,
            "time_budget_s": 5.0,
        }
    }
    import json

    match = run_detect(json.dumps(payload, indent=2))
    assert match is not None
    assert match.format_name == "registry-live"
    assert match.variant == "scan-definition"
    md = match.metadata or {}
    assert md.get("token") == "VendorA AppA"
    assert md.get("keywords") == ["server", "api"]
    assert md.get("max_depth") == 8


def test_detects_yaml_manifest_heuristically():
    text = """
registry_scan:
  token: TinyTool
  keywords: [server]
  patterns:
    - https://
"""
    match = run_detect(text, name="scan.yaml")
    assert match is not None
    assert match.format_name == "registry-live"
    assert match.variant == "scan-definition"
    assert (match.metadata or {}).get("token") == "TinyTool"

