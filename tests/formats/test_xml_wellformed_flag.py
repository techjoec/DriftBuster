from pathlib import Path

from driftbuster.formats.xml.plugin import XmlPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = XmlPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_xml_well_formed_true_and_false():
    ok = "<root><child/></root>"
    bad = "<root><child></root>"  # unbalanced

    m_ok = _detect("file.xml", ok)
    assert m_ok is not None
    assert m_ok.metadata and m_ok.metadata.get("xml_well_formed") is True

    m_bad = _detect("file.xml", bad)
    assert m_bad is not None
    assert m_bad.metadata and m_bad.metadata.get("xml_well_formed") is False
    assert m_bad.metadata.get("needs_review") is True
    assert any("not well-formed" in r.lower() for r in m_bad.reasons)

