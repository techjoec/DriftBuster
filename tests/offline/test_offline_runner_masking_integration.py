import base64
import json
from pathlib import Path

from driftbuster import offline_runner


def _build_keyset(path: Path, *, aes_key: bytes, hmac_key: bytes) -> None:
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


def test_execute_config_masks_secret_samples(tmp_path: Path) -> None:
    fixtures_root = Path(__file__).resolve().parents[2] / "fixtures" / "secret_samples"
    keyset_path = tmp_path / "keyset.json"
    _build_keyset(keyset_path, aes_key=b"A" * 32, hmac_key=b"B" * 32)

    output_dir = tmp_path / "output"

    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "fixtures-secret-validation",
            "description": "integration validation for secret masking",
            "sources": [
                {
                    "path": str(fixtures_root / "auth_secrets.txt"),
                    "alias": "secret-fixtures",
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
        "metadata": {
            "audit": "secret-masking",
        },
    }

    config = offline_runner.OfflineRunnerConfig.from_dict(config_payload)
    result = offline_runner.execute_config(
        config,
        base_dir=tmp_path,
        timestamp="20251025T070000Z",
    )

    assert result.package_path is not None
    assert result.package_path.suffix == ".enc"
    assert result.encrypted_package_path == result.package_path
    assert result.unencrypted_package_path is not None
    assert not result.unencrypted_package_path.exists()

    assert result.manifest_path is not None and result.manifest_path.exists()
    manifest_payload = json.loads(result.manifest_path.read_text(encoding="utf-8"))

    encryption_details = manifest_payload["package"]["encryption"]
    assert encryption_details["enabled"] is True
    assert encryption_details["schema"] == offline_runner.ENCRYPTED_PACKAGE_SCHEMA

    findings = manifest_payload["secrets"]["findings"]
    rules = {finding["rule"] for finding in findings}
    assert {"PasswordAssignment", "GenericApiToken", "AwsAccessKeyId"}.issubset(rules)

    assert any(
        finding["path"].endswith("auth_secrets.txt") for finding in findings
    ), "findings should reference the secret fixture"

    collected = {entry.relative_path.as_posix(): entry for entry in result.files}
    secret_entry = None
    for relative, entry in collected.items():
        if relative.endswith("auth_secrets.txt"):
            secret_entry = entry
            break
    assert secret_entry is not None, "expected collected secret fixture"

    sanitized_text = secret_entry.destination.read_text(encoding="utf-8")
    assert "[SECRET]" in sanitized_text
    assert "SuperSecret1234" not in sanitized_text
    assert "ABCDEF1234567890ABCD" not in sanitized_text
    assert "AKIA1234567890ABCDEF" not in sanitized_text

    manifest_snippets = {finding["snippet"] for finding in findings}
    assert "[SECRET]" in manifest_snippets
    assert all("SuperSecret1234" not in snippet for snippet in manifest_snippets)
