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


def test_json_plugin_handles_comment_only_payload() -> None:
    plugin = JsonPlugin()
    match = _detect(plugin, "config.json", "// comment only\n/* block */")
    assert match is None


def test_json_plugin_detects_generic_without_extension() -> None:
    plugin = JsonPlugin()
    match = _detect(plugin, "config.settings", '{"key": 1, "list": [1, 2, 3]}')
    assert match is not None
    assert match.variant == "generic"
    assert match.metadata is not None
    assert "top_level_keys" in match.metadata


def test_json_plugin_attempt_parse_array_metadata() -> None:
    plugin = JsonPlugin()
    match = _detect(plugin, "data.json", "[{\"value\": 1}, {\"value\": 2}]")
    assert match is not None
    assert match.metadata is not None
    assert "top_level_sample_types" in match.metadata


def test_json_plugin_requires_signals_for_custom_extension() -> None:
    plugin = JsonPlugin()
    match = _detect(plugin, "config.settings", "[1, 2")
    assert match is None


def test_json_plugin_detect_returns_none_for_missing_text() -> None:
    plugin = JsonPlugin()
    path = Path("config.json")
    assert plugin.detect(path, b"{}", None) is None


def test_json_plugin_comment_helpers() -> None:
    plugin = JsonPlugin()
    stripped, consumed = plugin._strip_leading_comments("/*unterminated")
    assert stripped == "" and consumed is True

    assert plugin._contains_comments('{"value": "// comment"}') is False
