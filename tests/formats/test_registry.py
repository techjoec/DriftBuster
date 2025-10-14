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
    assert registry.looks_text(b"Plain ASCII text")
    assert not registry.looks_text(b"\x00\xff\x10\x80")

    utf16 = codecs.BOM_UTF16_LE + "value".encode("utf-16-le")
    text, encoding = registry.decode_text(utf16)
    assert text == "value"
    assert encoding in {"utf-16", "utf-16-le"}
