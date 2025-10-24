from __future__ import annotations

from pathlib import Path

from driftbuster.formats.json import JsonPlugin


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
    assert "settings_environment" not in match.metadata


def test_json_plugin_detects_structured_settings_environment() -> None:
    plugin = JsonPlugin()
    match = _detect(
        plugin,
        "appsettings.Staging.json",
        '{"Logging": {"LogLevel": {"Default": "Warning"}}}',
    )

    assert match is not None
    assert match.variant == "structured-settings-json"
    assert match.metadata is not None
    assert match.metadata["settings_environment"] == "staging"


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
    assert match.metadata.get("parsed_with_comment_stripping") is True
    assert "top_level_keys" in match.metadata


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

    stripped, consumed = plugin._strip_leading_comments("// no newline")
    assert stripped == "" and consumed is True

    assert plugin._contains_comments('{"value": "// comment"}') is False
    assert plugin._contains_comments('{"value": true}// trailing') is True
    assert plugin._contains_comments('{"path": "C\\\\Temp"}') is False

    assert plugin._has_key_value_marker('{"text": "escaped \"quote\""}') is True
    assert plugin._has_key_value_marker('{"items": [1, 2]}') is True

    truncated = plugin._truncate_to_structural_boundary('{"path": "C\\\\Temp"} // trailing')
    assert truncated.startswith('{"path": "C\\\\Temp"}')

    assert plugin._is_structured_settings("config.json", '"ConnectionStrings": {}') == "content"
    parse = plugin._attempt_parse('{"unterminated": }', allow_comments=False)
    assert parse.success is False

    cleaned, removed = plugin._strip_json_comments(
        """{
        "key": "// inside string",
        // comment
        "flag": true
    }"""
    )
    assert removed is True
    assert "// inside string" in cleaned


def test_json_plugin_unknown_top_level_with_extension() -> None:
    plugin = JsonPlugin()
    match = _detect(plugin, "sample.json", '"text"')
    assert match is not None
    assert match.metadata is not None
    assert match.metadata["top_level_type"] == "unknown"


def test_json_plugin_signals_guard_for_custom_extension() -> None:
    plugin = JsonPlugin()
    match = _detect(plugin, "data.custom", "{{")
    assert match is None


def test_json_plugin_large_payload_gets_clamped() -> None:
    plugin = JsonPlugin()
    payload = '{"key": "' + ("a" * 250_000) + '"}'
    match = _detect(plugin, "massive.json", payload)

    assert match is not None
    assert match.metadata is not None
    assert match.metadata.get("analysis_window_truncated") is True
    assert match.metadata.get("analysis_window_chars") == 200_000


def test_json_plugin_comment_stripping_preserves_arrays() -> None:
    plugin = JsonPlugin()
    content = """{
        // comment about endpoints
        "endpoints": [
            "https://example.local",
            "https://api.local" // trailing comment
        ]
    }
    """
    match = _detect(plugin, "config.jsonc", content)

    assert match is not None
    assert match.metadata is not None
    assert match.metadata.get("parsed_with_comment_stripping") is True
    assert match.metadata.get("top_level_keys") == ["endpoints"]


def _detect(plugin: JsonPlugin, filename: str, payload: str):
    path = Path(filename)
    sample = payload.encode("utf-8")
    return plugin.detect(path, sample, payload)

