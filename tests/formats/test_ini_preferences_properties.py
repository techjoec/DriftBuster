from pathlib import Path

from driftbuster.formats.ini import IniPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = IniPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_ini_preferences_fallback_sectionless():
    # Colon-only assignments with .preferences extension should classify
    content = "a: 1\nb: 2\n"
    m = _detect("settings.preferences", content)
    assert m is not None
    assert m.format_name == "ini"
    assert m.variant == "sectionless-ini"


def test_properties_basic_java_properties_recognition():
    # Simple Java properties file with key/value pairs should classify
    content = "a=b\nc=d\n"
    m = _detect("application.properties", content)
    assert m is not None
    assert m.format_name == "ini"
    assert m.variant == "java-properties"
