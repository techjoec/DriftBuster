from pathlib import Path

from driftbuster.formats.yaml.plugin import YamlPlugin
from driftbuster.formats.toml.plugin import TomlPlugin
from driftbuster.formats.text.plugin import TextPlugin
from driftbuster.formats.dockerfile.plugin import DockerfilePlugin
from driftbuster.formats.hcl.plugin import HclPlugin
from driftbuster.formats.conf.plugin import ConfPlugin
from driftbuster.formats.registry_live.plugin import RegistryLivePlugin


def test_plugins_return_none_when_text_is_none():
    p = Path("x")
    assert YamlPlugin().detect(p, b"{}", None) is None
    assert TomlPlugin().detect(p, b"{}", None) is None
    assert TextPlugin().detect(p, b"{}", None) is None
    assert DockerfilePlugin().detect(p, b"{}", None) is None
    assert HclPlugin().detect(p, b"{}", None) is None
    assert ConfPlugin().detect(p, b"{}", None) is None
    assert RegistryLivePlugin().detect(p, b"{}", None) is None

