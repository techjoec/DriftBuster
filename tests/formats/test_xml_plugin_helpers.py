from __future__ import annotations

import xml.etree.ElementTree as ET

from driftbuster.formats.xml.plugin import XmlPlugin, _split_qualified_name


def test_split_qualified_name_parses_namespace() -> None:
    assert _split_qualified_name("ns:tag") == ("ns", "tag")
    assert _split_qualified_name("tag") == (None, "tag")


def test_xml_plugin_helper_reason_builders() -> None:
    plugin = XmlPlugin()
    metadata = {
        "xml_declaration": {"version": "1.0", "encoding": "utf-8", "standalone": "yes"},
        "namespaces": {"default": "urn:example"},
        "schema_locations": [
            {"namespace": "urn:example", "location": "http://example.com/schema.xsd"},
            {"namespace": None, "location": "local.xsd"},
        ],
        "resource_keys": ["Key1", "Key2"],
        "resource_keys_preview": "Key1, Key2",
        "msbuild_detected": True,
        "msbuild_default_targets": ["Build"],
        "msbuild_tools_version": "4.0",
        "msbuild_sdk": "Microsoft.NET.Sdk",
        "msbuild_targets": ["Clean", "Build"],
        "msbuild_import_hints": ["props"],
        "attribute_hints": {
            "connection_strings": [{"value": "Server=.;"}],
            "service_endpoints": [{"value": "https://api"}],
            "feature_flags": [{"value": "true"}],
        },
        "doctype": "<!DOCTYPE project>",
    }

    reasons: list[str] = []
    plugin._append_declaration_reasons(metadata, reasons)
    plugin._append_namespace_reason(metadata, reasons)
    plugin._append_schema_reason(metadata, reasons)
    plugin._append_resx_reason(metadata, reasons)
    plugin._append_msbuild_reasons(metadata, reasons)
    plugin._append_attribute_hint_reasons(metadata, reasons)
    plugin._append_doctype_reason(metadata, reasons)
    assert any("XML version declared" in reason for reason in reasons)
    assert any("namespace declarations" in reason for reason in reasons)
    assert any("Schema" in reason for reason in reasons)
    assert any("resource keys" in reason for reason in reasons)
    assert any("MSBuild" in reason for reason in reasons)
    assert any("feature flag" in reason for reason in reasons)
    assert any("DOCTYPE" in reason for reason in reasons)


def test_xml_plugin_metadata_extractors() -> None:
    plugin = XmlPlugin()
    sample = """
    <?xml version="1.0" encoding="utf-8"?>
    <configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                   xsi:schemaLocation="urn:example http://example.com/schema.xsd"
                   xmlns="urn:example">
      <connectionStrings>
        <add name="Default" connectionString="Server=.;Database=App" />
      </connectionStrings>
      <appSettings>
        <add key="ServiceEndpoint" value="https://api.example.com" />
        <add key="FeatureFlag:NewUI" value="true" />
      </appSettings>
    </configuration>
    """
    sample = sample.strip()
    metadata = plugin._collect_metadata(sample, extension=".config")
    root = ET.fromstring(sample)
    plugin._extract_schema_locations(metadata)
    plugin._extract_resx_keys(root, metadata)
    plugin._extract_attribute_hints(root, metadata)

    assert metadata["root_tag"].lower() == "configuration"
    assert metadata["schema_locations"]
    hints = metadata["attribute_hints"]
    assert hints["connection_strings"]
    assert hints["service_endpoints"]
    assert hints["feature_flags"]
