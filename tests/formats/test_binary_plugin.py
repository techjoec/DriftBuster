from pathlib import Path

from driftbuster.formats.binary.plugin import BinaryHybridPlugin


FIXTURES = Path("fixtures/binary")


def test_detects_sqlite_database(tmp_path: Path) -> None:
    plugin = BinaryHybridPlugin()
    path = FIXTURES / "settings.sqlite"
    sample = path.read_bytes()
    match = plugin.detect(path, sample, None)
    assert match is not None
    assert match.format_name == "embedded-sql-db"
    assert match.metadata["signature"] == "sqlite-format-3"
    assert "SQLite database" in match.reasons[0]


def test_detects_binary_plist() -> None:
    plugin = BinaryHybridPlugin()
    path = FIXTURES / "preferences.plist"
    sample = path.read_bytes()
    match = plugin.detect(path, sample, None)
    assert match is not None
    assert match.format_name == "plist"
    assert match.variant == "xml-or-binary"
    assert match.metadata["signature"] == "bplist00"


def test_detects_markdown_front_matter() -> None:
    plugin = BinaryHybridPlugin()
    path = FIXTURES / "config_frontmatter.md"
    sample = path.read_bytes()
    text = sample.decode("utf-8")
    match = plugin.detect(path, sample, text)
    assert match is not None
    assert match.format_name == "markdown-config"
    assert "front_matter_keys" in match.metadata
    assert "environment" in match.metadata["front_matter_keys"]


def test_returns_none_for_unmatched_payload() -> None:
    plugin = BinaryHybridPlugin()
    path = Path("binary.dat")
    match = plugin.detect(path, b"\x00\x01\x02", None)
    assert match is None
