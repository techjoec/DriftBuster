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


def test_xml_plugin_collects_schema_metadata() -> None:
    content = """
    <root xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
          xsi:schemaLocation="urn:test schema.xsd urn:other other.xsd"
          xsi:noNamespaceSchemaLocation="default.xsd">
      <child />
    </root>
    """

    match = _detect("schema.xml", content)

    assert match is not None
    assert match.metadata is not None
    schemas = match.metadata["schema_locations"]
    assert isinstance(schemas, list)
    assert schemas[0]["namespace"] == "urn:test"
    assert match.metadata["schema_no_namespace_location"] == "default.xsd"
    assert "urn:test" in match.metadata["schema_location_namespaces"]


def test_xml_plugin_records_multi_layer_transform_metadata() -> None:
    content = """
    <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
      <appSettings>
        <add key="Example" value="Test" xdt:Transform="Replace" />
      </appSettings>
    </configuration>
    """

    match = _detect("web.Release.Azure.config", content)

    assert match is not None
    assert match.metadata is not None
    assert match.metadata["config_transform"] is True
    assert match.metadata["config_transform_target"] == "Release.Azure"
    assert match.metadata["config_transform_layers"] == ["Release", "Azure"]
    assert match.metadata["config_transform_environment"] == "Azure"
