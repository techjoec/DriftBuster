from __future__ import annotations

from pathlib import Path

from driftbuster.core.types import DetectionMatch
from driftbuster.formats.ini import IniPlugin


def _detect(plugin: IniPlugin, filename: str, content: str) -> DetectionMatch | None:
    path = Path(filename)
    data = content.encode("utf-8")
    return plugin.detect(path, data, content)


def test_ini_plugin_detects_sections_and_keys() -> None:
    plugin = IniPlugin()
    match = _detect(
        plugin,
        "settings.ini",
        """
        [general]
        enabled = true
        threshold = 10
        [logging]
        level = info
        """.strip(),
    )

    assert match is not None
    assert match.format_name == "ini"
    assert match.variant == "sectioned-ini"
    assert match.metadata is not None
    assert match.metadata["section_count"] == 2
    assert match.metadata["key_value_pairs"] >= 3
    assert any("Section headers" in reason for reason in match.reasons)


def test_ini_plugin_detects_desktop_ini_variant() -> None:
    plugin = IniPlugin()
    match = _detect(
        plugin,
        "desktop.ini",
        """
        [.ShellClassInfo]
        IconResource=shell32.dll,3
        ConfirmFileOp=0
        """.strip(),
    )

    assert match is not None
    assert match.variant == "desktop-ini"
    assert match.metadata is not None
    assert ".ShellClassInfo" in match.metadata.get("sections", [])


def test_ini_plugin_classifies_env_files() -> None:
    plugin = IniPlugin()
    match = _detect(
        plugin,
        ".env",
        """
        DATABASE_URL=postgres://localhost/app
        export LOG_LEVEL=info
        FEATURE_FLAG=1
        """.strip(),
    )

    assert match is not None
    assert match.format_name == "env-file"
    assert match.variant == "dotenv"
    assert any("dotenv" in reason.lower() for reason in match.reasons)


def test_ini_plugin_classifies_unix_conf_variants() -> None:
    plugin = IniPlugin()
    match = _detect(
        plugin,
        "httpd.conf",
        """
        LoadModule authz_core_module modules/mod_authz_core.so
        ServerName example.com
        <Directory "/var/www/html">
            AllowOverride None
        </Directory>
        """.strip(),
    )

    assert match is not None
    assert match.format_name == "unix-conf"
    assert match.variant == "apache-conf"
    assert any("apache" in reason.lower() for reason in match.reasons)


def test_ini_plugin_detects_ini_json_hybrids() -> None:
    plugin = IniPlugin()
    match = _detect(
        plugin,
        "hybrid.conf",
        """
        [general]
        enabled=true
        {
            "extra": true
        }
        """.strip(),
    )

    assert match is not None
    assert match.format_name == "ini-json-hybrid"
    assert match.variant == "section-json-hybrid"
    assert any("hybrid" in reason.lower() for reason in match.reasons)


def test_ini_plugin_rejects_plain_text() -> None:
    plugin = IniPlugin()
    match = _detect(plugin, "notes.txt", "This is just a paragraph of text without configuration cues.")

    assert match is None


def test_ini_plugin_rejects_yaml_with_colons() -> None:
    plugin = IniPlugin()
    match = _detect(
        plugin,
        "config.yaml",
        """
        foo: bar
        bar: baz
        """.strip(),
    )

    assert match is None
