from __future__ import annotations

from pathlib import Path

from driftbuster.core.types import DetectionMatch
from driftbuster.formats.xml.plugin import XmlPlugin


def _detect(filename: str, content: str) -> DetectionMatch | None:
    plugin = XmlPlugin()
    path = Path(filename)
    sample = content.encode("utf-8")
    return plugin.detect(path, sample, content)


def test_xml_plugin_detects_framework_config() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration>
      <system.web>
        <compilation debug="true" />
      </system.web>
    </configuration>
    """
    match = _detect("web.config", content)

    assert match is not None
    assert match.format_name == "structured-config-xml"
    assert match.variant == "web-config"
    assert match.metadata is not None
    assert match.metadata["config_role"] == "web"


def test_xml_plugin_detects_config_transform_scope() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
      <system.webServer>
        <modules>
          <add name="Example" xdt:Transform="Replace" />
        </modules>
      </system.webServer>
    </configuration>
    """
    match = _detect("web.Release.config", content)

    assert match is not None
    assert match.variant == "web-config-transform"
    assert match.metadata is not None
    assert match.metadata["config_transform"] is True
    assert match.metadata["config_transform_scope"] == "web"
    assert match.metadata["config_transform_stages"] == ["Release"]
    assert match.metadata["config_transform_primary_stage"] == "Release"
    assert match.metadata["config_transform_stage_count"] == 1


def test_xml_plugin_records_multi_stage_transform_metadata() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
      <system.webServer>
        <modules>
          <add name="Example" xdt:Transform="Replace" />
        </modules>
      </system.webServer>
    </configuration>
    """
    match = _detect("web.Release.QA.config", content)

    assert match is not None
    assert match.variant == "web-config-transform"
    assert match.metadata is not None
    assert match.metadata["config_transform_stages"] == ["Release", "QA"]
    assert match.metadata["config_transform_primary_stage"] == "QA"
    assert match.metadata["config_transform_stage_count"] == 2
    assert any("Release -> QA" in reason for reason in match.reasons)


def test_xml_plugin_detects_manifest_variant() -> None:
    content = """
    <?xml version="1.0" encoding="utf-8"?>
    <assembly xmlns="urn:schemas-microsoft-com:asm.v1">
      <assemblyIdentity name="App" version="1.0.0.0" />
    </assembly>
    """
    match = _detect("App.manifest", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "app-manifest-xml"
    assert match.metadata is not None
    assert match.metadata["root_local_name"].lower() == "assembly"


def test_xml_plugin_detects_resx_variant_via_namespace() -> None:
    content = """
    <?xml version="1.0" encoding="utf-8"?>
    <root xmlns="http://schemas.microsoft.com/VisualStudio/2005/ResXSchema">
      <data name="Sample">
        <value>Hello</value>
      </data>
    </root>
    """
    match = _detect("Strings.resx", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "resource-xml"
    assert match.metadata is not None
    assert match.metadata["resource_keys"] == ["Sample"]
    assert any("Captured resource keys" in reason for reason in match.reasons)


def test_xml_plugin_detects_xaml_variant_via_namespace() -> None:
    content = """
    <?xml version="1.0"?>
    <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Grid />
    </UserControl>
    """
    match = _detect("View.xaml", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "interface-xml"


def test_xml_plugin_detects_vendor_config_roots() -> None:
    cases = [
        ("nlog.config", "<nlog><targets /></nlog>", "nlog-config"),
        ("log4net.config", "<log4net><appender /></log4net>", "log4net-config"),
        ("serilog.config", "<serilog><writeTo /></serilog>", "serilog-config"),
    ]

    for filename, payload, expected in cases:
        match = _detect(filename, payload)
        assert match is not None
        assert match.format_name == "structured-config-xml"
        assert match.variant == expected


def test_xml_plugin_canonicalises_root_attributes() -> None:
    content = """
    <configuration attrB="  value-b " attrA="value-a" xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
      <appSettings />
    </configuration>
    """
    match = _detect("web.config", content)

    assert match is not None
    assert match.metadata is not None
    assert match.metadata["root_attributes"] == {"attrA": "value-a", "attrB": "value-b", "xmlns:xdt": "http://schemas.microsoft.com/XML-Document-Transform"}


def test_xml_plugin_extracts_schema_locations() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                   xsi:schemaLocation="http://schemas.microsoft.com/.NetConfiguration/v2.0 http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd">
      <appSettings />
    </configuration>
    """
    match = _detect("web.config", content)

    assert match is not None
    assert match.metadata is not None
    assert match.metadata["schema_locations"] == [
        {
            "namespace": "http://schemas.microsoft.com/.NetConfiguration/v2.0",
            "location": "http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd",
        }
    ]
    assert any("Schema http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd declared" in reason for reason in match.reasons)
