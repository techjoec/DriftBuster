from pathlib import Path

from driftbuster.formats.text import TextPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = TextPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_text_marker_flag():
    content = """
    client
    dev tun
    remote 1.2.3.4 1194
    <<<DRIFT>>>
    """.strip()
    m = _detect("client.conf", content)
    assert m is not None
    assert m.metadata and m.metadata.get("needs_review") is True
    assert any("Nonstandard marker" in r for r in m.metadata.get("review_reasons", []))

