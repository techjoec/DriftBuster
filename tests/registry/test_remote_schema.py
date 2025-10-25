import pytest

from driftbuster.offline_runner import OfflineRegistryScanSource, RemoteRegistryTarget
from driftbuster.registry_cli import _parse_remote_target_arg


def test_remote_schema_parses_single_target():
    payload = {
        "alias": "hq-remote",
        "registry_scan": {
            "token": "VendorA",
            "remote": {
                "host": "hq-gateway",
                "username": "DOMAIN\\collector",
                "password_env": "DRIFTBUSTER_REMOTE_PASS",
                "transport": "winrm",
                "port": 5986,
                "use_ssl": True,
                "credential_profile": "hq-collector",
            },
        },
    }

    source = OfflineRegistryScanSource.from_dict(payload)
    assert source.remote is not None
    assert source.remote.host == "hq-gateway"
    assert source.remote.username == "DOMAIN\\collector"
    assert source.remote.password_env == "DRIFTBUSTER_REMOTE_PASS"
    assert source.remote.transport == "winrm"
    assert source.remote.port == 5986
    assert source.remote.use_ssl is True
    assert source.remote.credential_profile == "hq-collector"


def test_remote_schema_supports_batch_targets():
    payload = {
        "registry_scan": {
            "token": "VendorA",
            "remote": "branch-gateway",
            "remote_batch": [
                {"host": "branch-01", "username": "svc-collector"},
                "branch-02",
                {"host": "branch-03", "use_ssl": False, "transport": "winrm", "port": 5985},
            ],
        },
    }

    source = OfflineRegistryScanSource.from_dict(payload)
    assert source.remote is not None
    assert source.remote.host == "branch-gateway"
    assert len(source.remote_batch) == 3
    assert [target.host for target in source.remote_batch] == [
        "branch-01",
        "branch-02",
        "branch-03",
    ]
    assert source.remote_batch[0].username == "svc-collector"
    assert source.remote_batch[2].use_ssl is False
    assert source.remote_batch[2].port == 5985


def test_remote_schema_rejects_inline_passwords():
    payload = {
        "registry_scan": {
            "token": "VendorA",
            "remote": {"host": "forbidden", "password": "super-secret"},
        }
    }

    with pytest.raises(ValueError):
        OfflineRegistryScanSource.from_dict(payload)


@pytest.mark.parametrize(
    "value,expected",
    [
        (
            "branch-01,username=domain\\\\collector,password-env=REMOTE_PASS,use-ssl=false,port=5985",
            {
                "host": "branch-01",
                "username": "domain\\\\collector",
                "password_env": "REMOTE_PASS",
                "use_ssl": False,
                "port": 5985,
            },
        ),
        (
            "branch-02,transport=smb,alias=branch02",
            {"host": "branch-02", "transport": "smb", "alias": "branch02"},
        ),
    ],
)
def test_parse_remote_target_arg_roundtrip(value, expected):
    result = _parse_remote_target_arg(value)
    for key, val in expected.items():
        assert result[key] == val


@pytest.mark.parametrize(
    "value",
    ["", "username=missing", "branch-01,password-env="],
)
def test_parse_remote_target_arg_errors(value):
    with pytest.raises(ValueError):
        _parse_remote_target_arg(value)


def test_remote_batch_allows_mapping_payload():
    payload = {
        "registry_scan": {
            "token": "VendorA",
            "remote_batch": {"host": "branch-unique", "credential_profile": "branch-profile"},
        }
    }

    source = OfflineRegistryScanSource.from_dict(payload)
    assert source.remote is None
    assert len(source.remote_batch) == 1
    assert source.remote_batch[0] == RemoteRegistryTarget(
        host="branch-unique",
        transport="winrm",
        port=None,
        use_ssl=None,
        username=None,
        password_env=None,
        credential_profile="branch-profile",
        alias=None,
    )
