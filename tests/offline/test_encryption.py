import base64
import io
import json
import zipfile
import hmac
from hashlib import sha256

import pytest
from cryptography.hazmat.backends import default_backend
from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

from driftbuster import offline_runner


def _build_keyset(path, *, aes_key: bytes, hmac_key: bytes) -> None:
    payload = {
        "schema": offline_runner.ENCRYPTION_KEYSET_SCHEMA,
        "aes_key": {
            "encoding": "base64",
            "data": base64.b64encode(aes_key).decode("ascii"),
        },
        "hmac_key": {
            "encoding": "base64",
            "data": base64.b64encode(hmac_key).decode("ascii"),
        },
    }
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def test_execute_config_encrypts_package_with_dpapi_aes_keyset(tmp_path):
    source_dir = tmp_path / "source"
    source_dir.mkdir()
    (source_dir / "secrets.txt").write_text("token-123", encoding="utf-8")

    keyset_path = tmp_path / "keyset.json"
    aes_key = b"A" * 32
    hmac_key = b"B" * 32
    _build_keyset(keyset_path, aes_key=aes_key, hmac_key=hmac_key)

    output_dir = tmp_path / "output"

    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "encrypt-demo",
            "description": "demo",
            "sources": [
                {
                    "path": str(source_dir / "secrets.txt"),
                    "alias": "secret",
                }
            ],
            "options": {},
            "secret_scanner": {},
        },
        "runner": {
            "output_directory": str(output_dir),
            "compress": True,
            "cleanup_staging": False,
            "encryption": {
                "enabled": True,
                "mode": "dpapi-aes",
                "keyset_path": str(keyset_path),
                "output_extension": ".enc",
                "remove_plaintext": True,
            },
        },
        "metadata": {},
    }

    config = offline_runner.OfflineRunnerConfig.from_dict(config_payload)
    result = offline_runner.execute_config(config, base_dir=tmp_path, timestamp="20240101T000000Z")

    assert result.package_path is not None
    assert result.package_path.suffix == ".enc"
    assert result.encrypted_package_path == result.package_path
    assert result.package_path.exists(), "expected encrypted package"

    assert result.unencrypted_package_path is not None
    assert not result.unencrypted_package_path.exists(), "plaintext package should be removed"

    encrypted_payload = json.loads(result.package_path.read_text())
    assert encrypted_payload["schema"] == offline_runner.ENCRYPTED_PACKAGE_SCHEMA
    assert encrypted_payload["algorithm"] == "aes-256-cbc+hmac-sha256"

    iv = base64.b64decode(encrypted_payload["iv"])
    ciphertext = base64.b64decode(encrypted_payload["ciphertext"])
    mac = base64.b64decode(encrypted_payload["mac"])

    expected_mac = hmac.new(hmac_key, iv + ciphertext, sha256).digest()
    assert mac == expected_mac

    cipher = Cipher(algorithms.AES(aes_key), modes.CBC(iv), backend=default_backend())
    decryptor = cipher.decryptor()
    padded = decryptor.update(ciphertext) + decryptor.finalize()
    unpadder = padding.PKCS7(algorithms.AES.block_size).unpadder()
    plaintext = unpadder.update(padded) + unpadder.finalize()

    with zipfile.ZipFile(io.BytesIO(plaintext)) as archive:
        names = archive.namelist()
        assert any(name.startswith("data/") for name in names)
        assert "manifest.json" in names

        if result.manifest_path and result.manifest_path.exists():
            manifest_payload = json.loads(result.manifest_path.read_text())
        else:
            manifest_payload = json.loads(archive.read("manifest.json"))
    package_info = manifest_payload["package"]
    encryption_info = package_info["encryption"]
    assert encryption_info["enabled"] is True
    assert encryption_info["output_name"].endswith(".enc")
    assert encryption_info["remove_plaintext"] is True

    expected_sha256 = sha256(result.package_path.read_bytes()).hexdigest()
    assert encryption_info["sha256"] == expected_sha256


def test_execute_config_requires_compress_for_encryption(tmp_path):
    keyset_path = tmp_path / "keyset.json"
    _build_keyset(keyset_path, aes_key=b"A" * 32, hmac_key=b"B" * 32)

    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "no-compress",
            "sources": [
                {
                    "path": str(keyset_path),
                    "alias": "key",
                }
            ],
            "options": {},
            "secret_scanner": {},
        },
        "runner": {
            "output_directory": str(tmp_path / "out"),
            "compress": False,
            "cleanup_staging": False,
            "encryption": {
                "enabled": True,
                "keyset_path": str(keyset_path),
            },
        },
        "metadata": {},
    }

    config = offline_runner.OfflineRunnerConfig.from_dict(config_payload)

    with pytest.raises(ValueError):
        offline_runner.execute_config(config, base_dir=tmp_path, timestamp="20240101T000000Z")


def test_execute_config_path_supports_relative_paths(tmp_path):
    config_dir = tmp_path / "bundle"
    config_dir.mkdir()

    source_file = config_dir / "secrets.txt"
    source_file.write_text("token-456", encoding="utf-8")

    keyset_path = config_dir / "keyset.json"
    aes_key = b"C" * 32
    hmac_key = b"D" * 32
    _build_keyset(keyset_path, aes_key=aes_key, hmac_key=hmac_key)

    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "relative-paths",
            "sources": [
                {
                    "path": "secrets.txt",
                }
            ],
            "options": {},
            "secret_scanner": {},
        },
        "runner": {
            "compress": True,
            "cleanup_staging": True,
            "encryption": {
                "enabled": True,
                "mode": "dpapi-aes",
                "keyset_path": "keyset.json",
                "output_extension": ".enc",
                "remove_plaintext": True,
            },
        },
        "metadata": {},
    }

    config_path = config_dir / "config.json"
    config_path.write_text(json.dumps(config_payload, indent=2), encoding="utf-8")

    result = offline_runner.execute_config_path(config_path, timestamp="20240202T120000Z")

    assert result.package_path is not None
    assert result.package_path.parent == config_dir
    assert result.package_path.suffix == ".enc"
    assert result.encrypted_package_path == result.package_path

    assert result.unencrypted_package_path is not None
    assert not result.unencrypted_package_path.exists()

    payload = result.encryption_payload
    assert payload is not None
    assert payload["package"]["original_name"].endswith(".zip")

    iv = base64.b64decode(payload["iv"])
    ciphertext = base64.b64decode(payload["ciphertext"])

    cipher = Cipher(algorithms.AES(aes_key), modes.CBC(iv), backend=default_backend())
    decryptor = cipher.decryptor()
    padded = decryptor.update(ciphertext) + decryptor.finalize()
    unpadder = padding.PKCS7(algorithms.AES.block_size).unpadder()
    plaintext = unpadder.update(padded) + unpadder.finalize()

    with zipfile.ZipFile(io.BytesIO(plaintext)) as archive:
        names = archive.namelist()
        assert any(name.endswith("secrets.txt") for name in names)
        assert "manifest.json" in names
