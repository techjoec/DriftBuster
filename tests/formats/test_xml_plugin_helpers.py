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


def test_xml_plugin_append_schema_reason_handles_irregular_entries() -> None:
    plugin = XmlPlugin()
    metadata = {
        "schema_locations": [
            "invalid-entry",
            {"namespace": None, "location": "local.xsd"},
        ]
    }
    reasons: list[str] = []
    plugin._append_schema_reason(metadata, reasons)
    assert any("default namespace" in reason for reason in reasons)


def test_xml_plugin_append_resx_reason_without_preview() -> None:
    plugin = XmlPlugin()
    metadata = {"resource_keys": ["Alpha"], "resource_keys_preview": ""}
    reasons: list[str] = []
    plugin._append_resx_reason(metadata, reasons)
    assert reasons == ["Captured resource keys from .resx payload"]


def test_xml_plugin_extract_root_attributes_handles_truncated_tag() -> None:
    plugin = XmlPlugin()
    result = plugin._extract_root_attributes("<root", start_index=5)
    assert result == {}


def test_xml_plugin_extract_schema_locations_edge_cases() -> None:
    plugin = XmlPlugin()
    metadata = {
        "root_attributes": {
            "xsi:schemaLocation": "urn",  # insufficient tokens
            "xsi:noNamespaceSchemaLocation": " local.xsd ",
            "custom": 123,
            "empty": "   ",
        }
    }
    plugin._extract_schema_locations(metadata)
    entries = metadata.get("schema_locations", [])
    assert entries == [{"namespace": None, "location": "local.xsd"}]


def test_xml_plugin_extract_resx_keys_uses_default_namespace() -> None:
    plugin = XmlPlugin()
    metadata = {"namespaces": {"default": "http://schemas.microsoft.com/VisualStudio/2005/ResXSchema"}}
    xml = """
    <root>
      <data name="Entry"><value>Value</value></data>
    </root>
    """.strip()
    root = ET.fromstring(xml)
    plugin._extract_resx_keys(root, metadata)
    assert metadata["resource_keys"] == ["Entry"]


def test_xml_plugin_extract_attribute_hints_feature_element_without_key() -> None:
    plugin = XmlPlugin()
    xml = """
    <root>
      <FeatureToggle value="enabled" />
    </root>
    """.strip()
    root = ET.fromstring(xml)
    metadata: dict[str, object] = {}
    plugin._extract_attribute_hints(root, metadata)
    hints = metadata.get("attribute_hints")
    assert isinstance(hints, dict)
    feature_entries = hints.get("feature_flags")
    assert feature_entries and feature_entries[0]["attribute"].lower() == "value"


def test_xml_plugin_looks_like_msbuild_root_namespace_and_defaults() -> None:
    plugin = XmlPlugin()
    metadata = {"root_local_name": "Project", "root_namespace": "http://schemas.microsoft.com/developer/msbuild/2003"}
    assert plugin._looks_like_msbuild(".xml", metadata) is True

    metadata = {"root_local_name": "Project", "namespaces": {"default": "http://schemas.microsoft.com/developer/msbuild/2003"}}
    assert plugin._looks_like_msbuild(".xml", metadata) is True

    metadata = {"root_local_name": "Project", "root_attributes": {"DefaultTargets": "Build"}}
    assert plugin._looks_like_msbuild(".xml", metadata) is True


def test_xml_plugin_classify_msbuild_kind_default() -> None:
    plugin = XmlPlugin()
    assert plugin._classify_msbuild_kind(".unknown") == "project"


def test_xml_plugin_extract_msbuild_metadata_skips_blank_imports() -> None:
    plugin = XmlPlugin()
    xml = """
    <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
      <Import Project="   " />
      <Import Project="build.targets" Condition="   " />
    </Project>
    """.strip()
    root = ET.fromstring(xml)
    metadata = {"root_local_name": "Project", "root_namespace": "http://schemas.microsoft.com/developer/msbuild/2003"}
    plugin._extract_msbuild_metadata(root, metadata, ".xml")
    hints = metadata.get("msbuild_import_hints")
    assert isinstance(hints, list)
    assert hints and "condition_hash" not in hints[0]


def test_xml_plugin_add_attribute_hint_ignores_blank_values() -> None:
    plugin = XmlPlugin()
    hints = {"service_endpoints": [], "connection_strings": [], "feature_flags": []}
    seen = {"service_endpoints": set(), "connection_strings": set(), "feature_flags": set()}
    plugin._add_attribute_hint(
        hints=hints,
        seen=seen,
        category="service_endpoints",
        element_name="Endpoint",
        attribute_name="address",
        value="   ",
        key_value=None,
        key_attribute=None,
    )
    assert hints["service_endpoints"] == []


def test_xml_plugin_looks_like_endpoint_handles_whitespace_and_shares() -> None:
    assert XmlPlugin._looks_like_endpoint("   ") is False
    assert XmlPlugin._looks_like_endpoint("\\\\server\\share") is True


def test_xml_plugin_guess_variant_prefers_default_manifest_namespace() -> None:
    plugin = XmlPlugin()
    reasons: list[str] = []
    metadata = {
        "namespaces": {"asm": "urn:custom", "default": "urn:schemas-microsoft-com:asm.v1"},
        "root_namespace": "urn:custom",
    }
    _, variant, _ = plugin._guess_variant(".xml", "", reasons, metadata)
    assert variant == "app-manifest-xml"
    assert any("assembly manifest namespace" in reason for reason in reasons)


def test_xml_plugin_guess_variant_detects_resx_and_xaml_defaults() -> None:
    plugin = XmlPlugin()
    metadata = {"namespaces": {"default": "http://schemas.microsoft.com/VisualStudio/2005/ResXSchema"}, "root_namespace": "urn:other"}
    reasons: list[str] = []
    _, resx_variant, _ = plugin._guess_variant(".xml", "", reasons, metadata)
    assert resx_variant == "resource-xml"
    assert any("resx schema" in reason.lower() for reason in reasons)

    metadata = {"namespaces": {"default": "http://schemas.microsoft.com/winfx/2006/xaml"}, "root_namespace": "urn:other"}
    reasons = []
    _, xaml_variant, _ = plugin._guess_variant(".xml", "", reasons, metadata)
    assert xaml_variant == "interface-xml"
    assert any("xaml" in reason.lower() for reason in reasons)


def test_xml_plugin_extract_resx_keys_skips_missing_names() -> None:
    plugin = XmlPlugin()
    metadata = {"namespaces": {"default": "http://schemas.microsoft.com/VisualStudio/2005/ResXSchema"}}
    xml = """
    <root>
      <data><value>ignore</value></data>
      <data name="Keep"><value>keep</value></data>
    </root>
    """.strip()
    root = ET.fromstring(xml)
    plugin._extract_resx_keys(root, metadata)
    assert metadata["resource_keys"] == ["Keep"]


def test_xml_plugin_extract_resx_keys_limits_to_ten_entries() -> None:
    plugin = XmlPlugin()
    metadata = {"namespaces": {"default": "http://schemas.microsoft.com/VisualStudio/2005/ResXSchema"}}
    entries = "".join(
        f"<data name='Key{i}'><value>{i}</value></data>" for i in range(12)
    )
    xml = f"<root>{entries}</root>"
    root = ET.fromstring(xml)
    plugin._extract_resx_keys(root, metadata)
    keys = metadata["resource_keys"]
    assert len(keys) == 10
    assert keys[0] == "Key0"


def test_xml_plugin_extract_attribute_hints_feature_with_name() -> None:
    plugin = XmlPlugin()
    xml = """
    <root>
      <FeatureFlag name="" value="on" />
    </root>
    """.strip()
    root = ET.fromstring(xml)
    metadata: dict[str, object] = {}
    plugin._extract_attribute_hints(root, metadata)
    hints = metadata.get("attribute_hints")
    assert isinstance(hints, dict)
    feature_entries = hints.get("feature_flags")
    assert feature_entries and feature_entries[0]["attribute"].lower() == "value"


def test_xml_plugin_looks_like_msbuild_returns_false_without_signals() -> None:
    plugin = XmlPlugin()
    metadata = {"root_local_name": "Project"}
    assert plugin._looks_like_msbuild(".xml", metadata) is False


def test_xml_plugin_looks_like_endpoint_supports_service_bus() -> None:
    assert XmlPlugin._looks_like_endpoint("net.tcp://service") is True
    assert XmlPlugin._looks_like_endpoint("sb://queue") is True
