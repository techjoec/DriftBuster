from pathlib import Path

from driftbuster.formats.yaml.plugin import YamlPlugin


def _detect(name: str, content: str):
    plugin = YamlPlugin()
    return plugin.detect(Path(name), content.encode("utf-8"), content)


def test_yaml_tabs_flag_needs_review():
    content = "apiVersion: v1\n\tkind: ConfigMap\nmetadata:\n  name: app\n"
    m = _detect("config.yaml", content)
    assert m is not None
    assert m.metadata and m.metadata.get("needs_review") is True
    assert any("Tab indentation" in r for r in m.metadata.get("review_reasons", []))


def test_yaml_doc_marker_reason():
    content = "---\nkey: value\n"
    m = _detect("simple.yaml", content)
    assert m is not None
    assert any("document start" in r.lower() for r in m.reasons)


def test_yaml_ini_like_extension_requires_structure():
    # .conf with weak structure should be rejected
    content = "key: value\n"
    m = _detect("mongod.conf", content)
    assert m is None

    # But a .conf with stronger YAML structure is accepted
    strong = "---\nservers:\n  item: true\n  nested:\n    key: value\n  - list: yes\n"
    m_strong = _detect("service.conf", strong)
    assert m_strong is not None
