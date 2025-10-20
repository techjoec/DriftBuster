from pathlib import Path

from driftbuster.formats.toml.plugin import TomlPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = TomlPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_toml_trailing_comma_flag():
    content = """
    [server]
    ports = [8000,]
    """.strip()
    m = _detect("config.toml", content)
    assert m is not None
    assert m.metadata and m.metadata.get("needs_review") is True
    assert any("trailing comma" in r for r in m.metadata.get("review_reasons", []))


def test_toml_bare_keys_flag():
    content = """
    title = "Example"
    key1
    key2
    key3
    """.strip()
    m = _detect("plain.toml", content)
    # content_signals < 2 -> None (no detection)
    if m is None:
        # add one valid pair to enable detection
        content2 = content + "\nname = 'x'\n"
        m = _detect("plain.toml", content2)
    assert m is not None
    assert m.metadata and m.metadata.get("needs_review") is True
    assert any("bare key" in r for r in m.metadata.get("review_reasons", []))

