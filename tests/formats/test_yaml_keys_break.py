from pathlib import Path

from driftbuster.formats.yaml.plugin import YamlPlugin


def test_yaml_top_keys_preview_breaks_at_eight():
    # Build 10 top-level keys to trigger the preview break logic
    lines = [f"k{i}: v{i}" for i in range(10)]
    content = "\n".join(lines)
    plugin = YamlPlugin()
    m = plugin.detect(Path("many.yaml"), content.encode("utf-8"), content)
    assert m is not None
    md = m.metadata or {}
    keys = md.get("top_level_keys_preview")
    assert keys is not None
    assert len(keys) == 8  # preview caps at 8 and breaks

