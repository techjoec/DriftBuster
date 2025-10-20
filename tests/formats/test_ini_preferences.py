from pathlib import Path

from driftbuster.formats.ini import IniPlugin


def test_colon_only_preferences_short_file():
    content = """
    gui.column.format:
        "No.", "%m",
        "Time", "%Yt"
    gui.layout_type: 3
    """
    p = Path("bluetooth.preferences")
    pl = IniPlugin()
    m = pl.detect(p, content.encode("utf-8"), content)
    assert m is not None
    assert m.format_name == "ini"
    assert m.variant == "sectionless-ini"

