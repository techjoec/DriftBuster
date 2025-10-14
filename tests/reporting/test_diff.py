from driftbuster.reporting.diff import canonicalise_xml


def test_canonicalise_xml_preserves_namespace_order_and_sorts_attributes() -> None:
    xml = """
    <configuration xmlns='urn:config' xmlns:x='urn:x' xmlns:y='urn:y'>
      <child y:attr='1' x:beta='2' alpha='3'/>
    </configuration>
    """

    canonical = canonicalise_xml(xml)

    assert canonical.startswith(
        "<configuration xmlns=\"urn:config\" xmlns:x=\"urn:x\" xmlns:y=\"urn:y\">"
    )
    assert "<child alpha=\"3\" x:beta=\"2\" y:attr=\"1\" />" in canonical


def test_canonicalise_xml_trims_schema_attributes_consistently() -> None:
    xml = """
    <root xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'
          xsi:schemaLocation=' urn:test schema.xsd '>
      <item value='1'/>
    </root>
    """

    canonical = canonicalise_xml(xml)

    assert "xsi:schemaLocation=\"urn:test schema.xsd\"" in canonical
    assert "<item value=\"1\" />" in canonical
