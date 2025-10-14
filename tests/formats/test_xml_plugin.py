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
