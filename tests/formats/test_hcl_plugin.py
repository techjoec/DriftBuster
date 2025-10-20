from pathlib import Path

from driftbuster.formats.hcl.plugin import HclPlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = HclPlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_hcl_nomad_detection():
    nomad = """
    job "example" {
      datacenters = ["dc1"]
      group "g" {
        task "t" { driver = "docker" }
      }
    }
    """
    m = _detect("client.hcl", nomad)
    assert m is not None
    assert m.format_name == "hcl"
    assert m.variant == "hashicorp-nomad"


def test_hcl_consul_detection():
    consul = """
    server {
      enabled = true
    }
    datacenter = "dc1"
    """
    m = _detect("consul.hcl", consul)
    assert m is not None
    assert m.variant in {"hashicorp-consul", "generic"}

