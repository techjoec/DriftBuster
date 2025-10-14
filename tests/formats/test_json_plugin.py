from __future__ import annotations

from pathlib import Path

from driftbuster.core.types import DetectionMatch
from driftbuster.formats.json import JsonPlugin


def _detect(plugin: JsonPlugin, filename: str, content: str) -> DetectionMatch | None:
    path = Path(filename)
    data = content.encode("utf-8")
    return plugin.detect(path, data, content)


def test_json_plugin_detects_structured_settings() -> None:
    plugin = JsonPlugin()
    match = _detect(
        plugin,
        "appsettings.json",
        '{"Logging": {"LogLevel": {"Default": "Information"}}, "ConnectionStrings": {"Default": "sqlite"}}',
    )

    assert match is not None
    assert match.format_name == "json"
    assert match.variant == "structured-settings-json"
    assert match.metadata is not None
    assert match.metadata["settings_hint"] == "filename"


def test_json_plugin_detects_json_with_comments() -> None:
    plugin = JsonPlugin()
    match = _detect(
        plugin,
        "config.jsonc",
        "// comment\n{\"key\": 1, /* multi */ \"enabled\": true}",
    )

    assert match is not None
    assert match.variant == "jsonc"
    assert match.metadata is not None
    assert match.metadata["has_comments"] is True


def test_json_plugin_rejects_non_json_payloads() -> None:
    plugin = JsonPlugin()
    match = _detect(plugin, "notes.txt", "Just some text without braces")

    assert match is None
