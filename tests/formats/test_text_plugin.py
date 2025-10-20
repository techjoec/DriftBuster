from pathlib import Path

from driftbuster.formats.text.plugin import TextPlugin


def _detect(name: str, content: str):
    plugin = TextPlugin()
    return plugin.detect(Path(name), content.encode("utf-8"), content)


def test_openssh_sshd_config_detection():
    content = """
    # OpenSSH server config
    Port 22
    PermitRootLogin no
    PasswordAuthentication yes
    Subsystem sftp C:/Windows/System32/OpenSSH/sftp-server.exe
    """
    match = _detect("sshd_config", content)
    assert match is not None
    assert match.format_name == "unix-conf"
    assert match.variant == "openssh-conf"


def test_openvpn_client_conf_detection():
    content = """
    client
    dev tun
    proto udp
    remote vpn.example.com 1194
    resolv-retry infinite
    nobind
    persist-key
    persist-tun
    """
    match = _detect("client.conf", content)
    assert match is not None
    assert match.variant == "openvpn-conf"


def test_text_plugin_ignores_assignment_heavy_files():
    content = """
    key1=value1
    key2=value2
    key3=value3
    key4=value4
    """
    match = _detect("assignments.conf", content)
    assert match is None
