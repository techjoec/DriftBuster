from __future__ import annotations

import xml.etree.ElementTree as ET

from driftbuster.reporting.diff import canonicalise_xml


def test_canonicalise_xml_preserves_significant_whitespace() -> None:
    payload = """
    <root attr="  padded "><child>  spaced text  </child> tail  <leaf>value</leaf></root>
    """

    result = canonicalise_xml(payload)
    root = ET.fromstring(result)

    assert root.attrib["attr"] == "  padded "
    child = root.find("child")
    assert child is not None
    assert child.text == "  spaced text  "
    assert child.tail == " tail  "


def test_canonicalise_xml_discards_whitespace_only_nodes() -> None:
    payload = """
    <root>\n      <child>   </child>   \n      <leaf />\n    </root>
    """

    result = canonicalise_xml(payload)
    root = ET.fromstring(result)

    child = root.find("child")
    assert child is not None
    assert child.text in {"", None}
    assert child.tail in {"", None}

    assert root.text in {"", None}
