from pathlib import Path

from driftbuster.formats.json import JsonPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = JsonPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_json_parse_failed_flag():
    content = '{"a": 1,}'  # trailing comma, no comments
    m = _detect("config.json", content)
    assert m is not None
    assert m.metadata and m.metadata.get("needs_review") is True
    assert m.metadata.get("parse_failed") is True
    assert any("parse failed" in r.lower() for r in m.metadata.get("review_reasons", []))


def test_json_parse_success_after_comment_stripping():
    content = """// leading comment
    {
        "a": 1,
        "b": 2 // trailing comment
    }
    """
    m = _detect("config.jsonc", content)
    assert m is not None
    assert m.metadata is not None
    assert "parse_failed" not in m.metadata
    assert m.metadata.get("parsed_with_comment_stripping") is True
    assert m.metadata.get("needs_review") is None

