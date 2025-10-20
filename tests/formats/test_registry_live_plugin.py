from pathlib import Path

from driftbuster.formats.registry_live.plugin import RegistryLivePlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = RegistryLivePlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_registry_live_json_and_yaml_paths():
    json_content = """
    {"registry_scan": {"token": "App", "keywords": ["k"], "patterns": ["p"]}}
    """.strip()
    m_json = _detect("scan.json", json_content)
    assert m_json is not None
    assert m_json.format_name == "registry-live"
    assert any("Token provided" in r for r in m_json.reasons)

    yaml_content = """
    registry_scan:
      token: App
      keywords: [server, endpoint]
      patterns:
        - https://
        - api.internal.local
    """.strip()
    m_yaml = _detect("scan.yaml", yaml_content)
    assert m_yaml is not None
    assert m_yaml.format_name == "registry-live"
    assert any("registry_scan:" in r or "Detected 'registry_scan:'" in r for r in m_yaml.reasons)


def test_registry_live_invalid_json_with_key_reason():
    bad = '{"registry_scan": {"token": "App"'  # missing closing braces
    m = _detect("scan.json", bad)
    # Detection may fall through to None; ensure code path executes without error
    assert m is None


def test_registry_live_filename_hint_and_options():
    content = """
    {"registry_scan": {"token": "App", "max_depth": 5}}
    """.strip()
    m = _detect("myscan.regscan.json", content)
    assert m is not None
    # Filename hint reason and options captured
    assert any("Filename suggests a registry scan JSON manifest" in r for r in m.reasons)
    assert m.metadata and m.metadata.get("max_depth") == 5
