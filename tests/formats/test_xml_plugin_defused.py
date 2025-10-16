from __future__ import annotations

from typing import Any

import types

from driftbuster.formats.xml import plugin as xml_plugin


class _FakeDefused:
    def fromstring(self, text: str) -> _FakeElement:  # type: ignore[override]
        # Delegate to the stdlib XML parser to produce a real Element
        import xml.etree.ElementTree as ET  # local import for test isolation
        return ET.fromstring(text)


def test_collect_metadata_uses_defusedxml_branch(monkeypatch):
    # Arrange: ensure DEFUSED_ET is set to a fake defusedxml shim
    fake = _FakeDefused()
    monkeypatch.setattr(xml_plugin, "DEFUSED_ET", fake, raising=True)

    # Build a simple XML payload that is safe to parse and includes declaration + root
    xml_text = (
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        "<root attr=\"v\" xmlns=\"urn:test\">value</root>\n"
    )

    # Act: call the internal collector directly to exercise the branch
    md = xml_plugin.XmlPlugin()._collect_metadata(xml_text, extension=".xml")

    # Assert: metadata extracted as expected, implying defused branch executed
    assert md.get("xml_declaration") == {"version": "1.0", "encoding": "utf-8"}
    assert md.get("root_tag") == "root"
    assert md.get("root_local_name") == "root"
    assert md.get("root_namespace") == "urn:test"
    # When parsing succeeded, msbuild hints should not be injected spuriously
    assert "msbuild_detected" not in md
