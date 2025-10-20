from pathlib import Path

from driftbuster.formats.toml.plugin import TomlPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = TomlPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_toml_generic_and_array_of_tables():
    generic = """
    title = "Example"
    [server]
    host = "127.0.0.1"
    ports = [8000, 8001]
    """
    m1 = _detect("config.toml", generic)
    assert m1 is not None
    assert m1.format_name == "toml"
    assert m1.variant == "generic"

    aot = """
    [[inputs.cpu]]
    percpu = true
    [[inputs.mem]]
    [[outputs.influxdb]]
    url = "http://localhost"
    """
    m2 = _detect("telegraf.toml", aot)
    assert m2 is not None
    assert m2.variant == "array-of-tables"

