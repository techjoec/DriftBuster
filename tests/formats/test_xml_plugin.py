from __future__ import annotations

import hashlib
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


def test_xml_plugin_detects_app_config_variant() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration>
      <startup>
        <supportedRuntime version="v4.0" />
      </startup>
    </configuration>
    """
    match = _detect("App.config", content)

    assert match is not None
    assert match.format_name == "structured-config-xml"
    assert match.variant == "app-config"
    assert match.metadata is not None
    assert match.metadata["config_role"] == "app"
    assert any("app.config" in reason for reason in match.reasons)


def test_xml_plugin_detects_machine_config_variant() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration>
      <system.web>
        <trust level="Full" />
      </system.web>
    </configuration>
    """
    match = _detect("machine.config", content)

    assert match is not None
    assert match.format_name == "structured-config-xml"
    assert match.variant == "machine-config"
    assert match.metadata is not None
    assert match.metadata["config_role"] == "machine"
    assert any("machine.config" in reason for reason in match.reasons)


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


def test_xml_plugin_detects_app_config_transform_variant() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
      <startup>
        <supportedRuntime xdt:Transform="Replace" />
      </startup>
    </configuration>
    """
    match = _detect("app.Release.config", content)

    assert match is not None
    assert match.format_name == "structured-config-xml"
    assert match.variant == "app-config-transform"
    assert match.metadata is not None
    assert match.metadata["config_transform"] is True
    assert match.metadata["config_transform_scope"] == "app"
    assert match.metadata["config_transform_stages"] == ["Release"]
    assert match.metadata["config_transform_stage_count"] == 1


def test_xml_plugin_detects_generic_config_transform_variant() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
      <appSettings>
        <add key="Feature" value="true" />
      </appSettings>
    </configuration>
    """
    match = _detect("service.Stage.config", content)

    assert match is not None
    assert match.format_name == "structured-config-xml"
    assert match.variant == "config-transform"
    assert match.metadata is not None
    assert match.metadata["config_transform"] is True
    assert "config_transform_scope" not in match.metadata


def test_xml_plugin_classifies_generic_web_or_app_config() -> None:
    content = """
    <?xml version="1.0"?>
    <configuration>
      <appSettings />
    </configuration>
    """
    match = _detect("generic.config", content)

    assert match is not None
    assert match.format_name == "structured-config-xml"
    assert match.variant == "web-or-app-config"
    assert match.metadata is not None
    assert match.metadata["config_role"] == "generic"


def test_xml_plugin_identifies_custom_config_xml() -> None:
    content = """
    <settings>
      <item key="a" value="1" />
    </settings>
    """
    match = _detect("custom.config", content)

    assert match is not None
    assert match.format_name == "structured-config-xml"
    assert match.variant == "custom-config-xml"
    assert match.metadata is not None
    assert match.metadata.get("root_tag") == "settings"


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


def test_xml_plugin_detects_xslt_variant() -> None:
    content = """
    <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
      <xsl:template match="/">
        <root />
      </xsl:template>
    </xsl:stylesheet>
    """
    match = _detect("layout.xslt", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "xslt-xml"
    assert match.metadata is not None
    assert match.metadata.get("xslt_stylesheet") is True


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


def test_xml_plugin_collects_attribute_hints() -> None:
    content = """
    <?xml version="1.0" encoding="utf-8"?>
    <configuration>
      <connectionStrings>
        <add name="DefaultConnection" connectionString="Server=.;Database=App;User Id=app;Password=Pass123!;" />
      </connectionStrings>
      <appSettings>
        <add key="ServiceEndpoint" value="https://api.example.com/v1/" />
        <add key="FeatureFlag:NewUI" value="true" />
      </appSettings>
      <system.serviceModel>
        <client>
          <endpoint address="net.tcp://services.example.com:8443/Feed" />
        </client>
      </system.serviceModel>
    </configuration>
    """
    match = _detect("web.config", content)

    assert match is not None
    assert match.metadata is not None
    hints = match.metadata.get("attribute_hints")
    assert isinstance(hints, dict)

    connection_hints = hints.get("connection_strings")
    assert isinstance(connection_hints, list)
    assert len(connection_hints) == 1
    connection_entry = connection_hints[0]
    expected_connection_hash = hashlib.sha256(
        "Server=.;Database=App;User Id=app;Password=Pass123!;".encode("utf-8")
    ).hexdigest()
    assert connection_entry["hash"] == expected_connection_hash
    assert connection_entry["key"] == "DefaultConnection"

    endpoint_hints = hints.get("service_endpoints")
    assert isinstance(endpoint_hints, list)
    endpoint_hashes = {entry["hash"] for entry in endpoint_hints}
    assert hashlib.sha256("https://api.example.com/v1/".encode("utf-8")).hexdigest() in endpoint_hashes
    assert hashlib.sha256("net.tcp://services.example.com:8443/Feed".encode("utf-8")).hexdigest() in endpoint_hashes

    feature_hints = hints.get("feature_flags")
    assert isinstance(feature_hints, list)
    assert feature_hints
    assert feature_hints[0]["key"] == "FeatureFlag:NewUI"
    assert any("feature flag attribute hints" in reason.lower() for reason in match.reasons)


def test_xml_plugin_supports_targets_extension() -> None:
    content = r"""
    <Project ToolsVersion="Current"
             DefaultTargets="Build;Publish"
             xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
      <Import Project="$(VSToolsPath)\WebApplication.targets" Condition="Exists('$(VSToolsPath)')" />
      <Target Name="Publish">
        <Message Text="Publishing" />
      </Target>
    </Project>
    """
    match = _detect("build.targets", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "msbuild-targets"
    assert match.metadata is not None
    assert match.metadata["msbuild_default_targets"] == ["Build", "Publish"]
    assert match.metadata["msbuild_tools_version"] == "Current"
    assert match.metadata["msbuild_targets"] == ["Publish"]
    import_hints = match.metadata.get("msbuild_import_hints")
    assert isinstance(import_hints, list)
    assert import_hints and import_hints[0]["attribute"] == "Project"
    assert any("MSBuild default targets" in reason for reason in match.reasons)


def test_xml_plugin_detects_msbuild_props_variant() -> None:
    content = """
    <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
      <Import Project="shared.targets" />
    </Project>
    """
    match = _detect("common.props", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "msbuild-props"
    assert match.metadata is not None
    assert match.metadata.get("msbuild_kind") == "props"
    assert match.metadata.get("msbuild_detected") is True


def test_xml_plugin_detects_msbuild_project_metadata() -> None:
    content = """
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
      </PropertyGroup>
      <Import Sdk="Microsoft.Build.NoTargets/1.0.0" />
      <Target Name="Pack" />
    </Project>
    """
    match = _detect("App.csproj", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "msbuild-project"
    assert match.metadata is not None
    assert match.metadata["msbuild_sdk"] == "Microsoft.NET.Sdk"
    assert match.metadata["msbuild_targets"] == ["Pack"]
    import_hints = match.metadata.get("msbuild_import_hints")
    assert isinstance(import_hints, list)
    assert import_hints and import_hints[0]["attribute"] == "Sdk"
    assert any("MSBuild SDK specified" in reason for reason in match.reasons)


def test_xml_plugin_detects_generic_xml_variant() -> None:
    content = """
    <notes>
      <note>Hello</note>
    </notes>
    """
    match = _detect("notes.xml", content)

    assert match is not None
    assert match.format_name == "xml"
    assert match.variant == "generic"
    assert match.metadata is not None
    assert match.metadata.get("root_tag") == "notes"


def test_xml_plugin_rejects_plain_text() -> None:
    match = _detect("plain.txt", "Just text without any XML markers")

    assert match is None
