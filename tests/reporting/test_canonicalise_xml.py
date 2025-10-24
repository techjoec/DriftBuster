from __future__ import annotations

from driftbuster.reporting.diff import canonicalise_text, canonicalise_xml


def test_canonicalise_xml_normalises_structure_and_preserves_prolog() -> None:
    payload = (
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        "<!DOCTYPE note [\n"
        "<!ELEMENT note ANY>\n"
        "]>\n"
        "<note  b=\"2\"   a=\"1\">\n"
        "    <child other=\"two\" attr=\" value \">  spaced text  </child>  \n\n"
        "    <selfclosing   beta=\"b\"    alpha=\"a\"/>\n"
        "</note>\n"
    )

    expected = (
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        "<!DOCTYPE note [\n"
        "<!ELEMENT note ANY>\n"
        "]>\n"
        "<note a=\"1\" b=\"2\"><child attr=\" value \" other=\"two\">  spaced text  </child>"
        "<selfclosing alpha=\"a\" beta=\"b\" /></note>"
    )

    assert canonicalise_xml(payload) == expected



def test_canonicalise_xml_cleans_whitespace_only_nodes_and_sorts_attributes() -> None:
    payload = (
        "<root attr=' padded ' other='value'>\n"
        "    <empty>   </empty>   \n"
        "    <node b='2' a='1'>value</node>\n"
        "</root>\n"
    )

    expected = "<root attr=\" padded \" other=\"value\"><empty /><node a=\"1\" b=\"2\">value</node></root>"

    assert canonicalise_xml(payload) == expected



def test_canonicalise_xml_falls_back_to_text_on_parse_error() -> None:
    payload = (
        "<root>   \r\n"
        "  <child>value</child>   \r\n"
        "</root"
    )

    expected = canonicalise_text(payload)

    assert canonicalise_xml(payload) == expected


def test_canonicalise_xml_strips_bom_prefix() -> None:
    payload = "\ufeff<?xml version='1.0'?><root> value </root>"
    result = canonicalise_xml(payload)
    assert not result.startswith("\ufeff")
    assert result == "<?xml version='1.0'?>\n<root> value </root>"
