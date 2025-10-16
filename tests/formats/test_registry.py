from __future__ import annotations

from pathlib import Path

import codecs
import pytest

from driftbuster.core.types import DetectionMatch
from driftbuster.formats import registry


class _NullPlugin:
    name = "test-null-plugin"
    priority = 9999

    def detect(self, path: Path, sample: bytes, text: str | None) -> DetectionMatch | None:
        return None


def test_register_is_idempotent_for_same_instance() -> None:
    plugin = _NullPlugin()
    before = registry.get_plugins()
    registry.register(plugin)
    after = registry.get_plugins()
    registry.register(plugin)
    post = registry.get_plugins()

    assert len(after) == len(before) + 1
    assert len(post) == len(after)
    assert any(p.name == "test-null-plugin" for p in post)


def test_register_rejects_duplicate_names() -> None:
    class _DuplicateNamePlugin:
        name = "json"
        priority = 0

        def detect(self, path: Path, sample: bytes, text: str | None) -> DetectionMatch | None:
            return None

    with pytest.raises(ValueError):
        registry.register(_DuplicateNamePlugin())


def test_registry_summary_returns_plugin_metadata() -> None:
    summary = registry.registry_summary()
    assert summary
    entry = summary[0]
    assert {"name", "priority", "order", "module", "qualname"}.issubset(entry.keys())


def test_looks_text_and_decode_text() -> None:
    assert registry.looks_text(b"")
    assert registry.looks_text(codecs.BOM_UTF8 + b"payload")
    assert registry.looks_text(b"Plain ASCII text")
    assert not registry.looks_text(b"\x00\xff\x10\x80")

    mirrored = b"\x00A\x00B\x00C\x00D"
    assert registry.looks_text(mirrored)

    assert registry._ascii_ratio(b"") == 1.0

    utf16 = codecs.BOM_UTF16_LE + "value".encode("utf-16-le")
    text, encoding = registry.decode_text(utf16)
    assert text == "value"
    assert encoding in {"utf-16", "utf-16-le"}


def test_get_plugins_mutable_snapshot_and_versions() -> None:
    snapshot = registry.get_plugins(readonly=False)
    assert isinstance(snapshot, tuple)
    versions = registry.plugin_versions()
    assert all(name in versions for name in (plugin.name for plugin in snapshot))


def test_strip_bom_and_even_odd_heuristic() -> None:
    data = codecs.BOM_UTF8 + b"payload"
    stripped, had_bom = registry._strip_bom(data)
    assert had_bom is True and stripped == b"payload"

    # Alternating ASCII and null bytes should pass via even/odd heuristic.
    alternating = b"A\x00B\x00C\x00D\x00"
    assert registry.looks_text(alternating)


def test_decode_text_handles_duplicate_candidates() -> None:
    sample = codecs.BOM_UTF16_BE + "value".encode("utf-16-be")
    text, encoding = registry.decode_text(sample)
    assert text == "value"
    assert encoding.endswith("be")

    truncated = codecs.BOM_UTF16_LE + b"\x00"
    text, encoding = registry.decode_text(truncated)
    assert encoding == "latin-1"


def test_decode_text_fallback_to_latin1() -> None:
    binary = bytes(range(256))
    text, encoding = registry.decode_text(binary)
    assert encoding == "latin-1"
    assert len(text) == 256


def test_decode_text_prefers_utf8_sig_and_fallback_replace() -> None:
    utf8_sample = codecs.BOM_UTF8 + "value".encode("utf-8")
    text, encoding = registry.decode_text(utf8_sample)
    assert text == "value"
    assert encoding == "utf-8-sig"

    class FussyBytes(bytes):
        def decode(self, encoding: str, errors: str | None = None) -> str:
            if errors == "replace":
                return "fallback"
            raise UnicodeDecodeError(encoding, b"", 0, 1, "fail")

    sample = FussyBytes(b"\xff\xfe")
    text, encoding = registry.decode_text(sample)
    assert text == "fallback"
    assert encoding == "latin-1"
