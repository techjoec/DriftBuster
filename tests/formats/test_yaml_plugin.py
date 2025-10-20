from pathlib import Path

from driftbuster.formats.yaml.plugin import YamlPlugin


def _detect(name: str, content: str):
    plugin = YamlPlugin()
    return plugin.detect(Path(name), content.encode("utf-8"), content)


def test_yaml_generic_detection():
    content = """
    title: Example
    enabled: true
    servers:
      - host1
      - host2
    nested:
      key: value
    """
    match = _detect("settings.yaml", content)
    assert match is not None
    assert match.format_name == "yaml"
    assert match.variant == "generic"
    assert any("key: value" in r or "Found key:" in r for r in match.reasons)


def test_yaml_kubernetes_manifest_detection():
    content = """
    apiVersion: v1
    kind: ConfigMap
    metadata:
      name: app-config
    data:
      key: value
    """
    match = _detect("manifest.yml", content)
    assert match is not None
    assert match.variant == "kubernetes-manifest"
    assert any("apiVersion" in r for r in match.reasons)


def test_yaml_by_content_in_conf_filename():
    content = """
    storage:
      dbPath: /var/lib/data
    net:
      port: 27017
    """
    match = _detect("mongod.conf", content)
    assert match is not None
    assert match.format_name == "yaml"
    assert match.variant in {"generic", "kubernetes-manifest"}


def test_yaml_heavily_commented_reference_like_minion():
    content = """
    # master: salt
    # user: root
    # cachedir: /var/cache/salt/minion
    # ipv6: false
    # minion_id_caching: true
    # append_domain: example.com
    # grains:
    #   roles: [webserver]
    #
    # log_level: info
    #
    # Later in the file, uncomment to set:
    # master: salt-master.internal
    """
    match = _detect("minion", content)
    assert match is not None
    assert match.format_name == "yaml"
