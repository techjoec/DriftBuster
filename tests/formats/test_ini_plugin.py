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
    assert match.variant is None
    assert match.metadata is not None
    assert match.metadata["section_count"] == 2
    assert match.metadata["key_value_pairs"] >= 3


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


def test_ini_plugin_rejects_plain_text() -> None:
    plugin = IniPlugin()
    match = _detect(plugin, "notes.txt", "This is just a paragraph of text without configuration cues.")

    assert match is None
