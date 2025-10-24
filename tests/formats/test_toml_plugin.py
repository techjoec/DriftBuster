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


def test_toml_spacing_metadata_and_inline_table_reason():
    content = """
    [tool.black]
    line-length = 88
    skip-string-normalization = true

    [tool.poetry.dependencies]
    python = "^3.11"

    [tool.poetry.group.dev.dependencies]
    pytest = { version = "^7.0", extras = ["cov"] }
    """.strip()
    match = _detect("pyproject.toml", content)
    assert match is not None
    assert any("inline table" in r for r in match.reasons)
    spacing = (match.metadata or {}).get("key_value_spacing")
    assert spacing is not None
    assert spacing.get("after") is not None
    assert spacing.get("allowed_after")

