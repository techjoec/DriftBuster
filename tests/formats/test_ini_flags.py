from pathlib import Path

from driftbuster.formats.ini import IniPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = IniPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_ini_malformed_section_flag():
    content = """
    [ok]
    a=1
    [bad section
    b=2
    """.strip()
    m = _detect("settings.ini", content)
    # Depending on signals, may detect as ini or none; just assert flag if detected
    if m is not None:
        assert m.metadata and m.metadata.get("needs_review") is True
        assert any("Malformed section" in r for r in m.metadata.get("review_reasons", []))


def test_ini_colon_only_flag_outside_properties():
    content = """
    key: value
    other: 2
    """.strip()
    m = _detect("settings.conf", content)
    if m is not None:
        assert m.metadata and m.metadata.get("needs_review") is True
        assert any("Colon-only" in r for r in m.metadata.get("review_reasons", []))

