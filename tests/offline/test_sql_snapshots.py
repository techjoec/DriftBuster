from __future__ import annotations

import argparse
import json
import sqlite3
from pathlib import Path

import pytest

from driftbuster import offline_runner
from driftbuster.sql import build_sqlite_snapshot, write_sqlite_snapshot
from scripts import capture as capture_script


def _create_sample_database(path: Path) -> Path:
    connection = sqlite3.connect(path)
    try:
        cursor = connection.cursor()
        cursor.execute(
            "CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL)"
        )
        cursor.execute(
            "INSERT INTO accounts (email, secret, balance) VALUES (?, ?, ?)",
            ("alice@example.com", "token-1", 42.5),
        )
        cursor.execute(
            "INSERT INTO accounts (email, secret, balance) VALUES (?, ?, ?)",
            ("bob@example.com", "token-2", 13.75),
        )
        connection.commit()
    finally:
        connection.close()
    return path


def test_build_sqlite_snapshot_masks_and_hashes(tmp_path: Path) -> None:
    db_path = _create_sample_database(tmp_path / "sample.sqlite")

    snapshot = build_sqlite_snapshot(
        db_path,
        mask_columns={"accounts": ("secret",)},
        hash_columns={"accounts": ("email",)},
        placeholder="[MASK]",
        hash_salt="pepper",
    )

    payload = snapshot.to_dict()
    assert payload["database"] == "sample.sqlite"
    assert payload["dialect"] == "sqlite"
    assert payload["tables"], "expected exported tables"

    accounts = payload["tables"][0]
    assert accounts["name"] == "accounts"
    assert accounts["row_count"] == 2
    assert accounts["masked_columns"] == ["secret"]
    assert accounts["hashed_columns"] == ["email"]

    rows = accounts["rows"]
    assert rows[0]["secret"] == "[MASK]"
    assert rows[0]["email"].startswith("sha256:")
    assert pytest.approx(float(rows[0]["balance"])) == 42.5


def test_write_sqlite_snapshot_with_limits_and_sequences(tmp_path: Path) -> None:
    db_path = _create_sample_database(tmp_path / "limited.sqlite")
    connection = sqlite3.connect(db_path)
    try:
        cursor = connection.cursor()
        cursor.execute("CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB)")
        cursor.execute("INSERT INTO audit (payload) VALUES (?)", (sqlite3.Binary(b"audit"),))
        connection.commit()
    finally:
        connection.close()

    destination = tmp_path / "out.json"
    snapshot = write_sqlite_snapshot(
        db_path,
        destination,
        tables=("accounts",),
        exclude_tables=("nonexistent",),
        mask_columns=("accounts.secret",),
        hash_columns=("accounts.email",),
        limit=1,
    )

    payload = json.loads(destination.read_text())
    assert payload["tables"][0]["row_count"] == 2
    assert len(payload["tables"][0]["rows"]) == 1
    assert payload["tables"][0]["rows"][0]["secret"] == "[REDACTED]"

    audit_snapshot = build_sqlite_snapshot(db_path, tables=("audit",))
    audit_payload = audit_snapshot.to_dict()["tables"][0]["rows"][0]["payload"]
    assert audit_payload["type"] == "base64"

    with pytest.raises(ValueError):
        build_sqlite_snapshot(db_path, limit=0)


def test_capture_export_sql_subcommand_writes_manifest(tmp_path: Path) -> None:
    db_path = _create_sample_database(tmp_path / "capture.sqlite")
    output_dir = tmp_path / "exports"

    args = argparse.Namespace(
        database=[str(db_path)],
        output_dir=str(output_dir),
        table=[],
        exclude_table=[],
        mask_column=["accounts.secret"],
        hash_column=["accounts.email"],
        placeholder="[MASK]",
        hash_salt="pepper",
        limit=None,
        prefix="demo",
    )

    result = capture_script.run_sql_export(args)
    assert result == 0

    manifest_path = output_dir / "sql-manifest.json"
    assert manifest_path.exists()
    manifest = json.loads(manifest_path.read_text())
    assert manifest["exports"]
    export_entry = manifest["exports"][0]
    assert export_entry["tables"] == ["accounts"]

    snapshot_path = output_dir / "demo-sql-snapshot.json"
    assert snapshot_path.exists()
    snapshot_payload = json.loads(snapshot_path.read_text())
    assert snapshot_payload["tables"][0]["masked_columns"] == ["secret"]


def test_offline_runner_sql_snapshot_source(tmp_path: Path) -> None:
    db_path = _create_sample_database(tmp_path / "runner.sqlite")
    output_dir = tmp_path / "runner-output"

    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "sql-demo",
            "description": "demo",
            "sources": [
                {
                    "sql_snapshot": {
                        "path": str(db_path),
                        "mask_columns": {"accounts": ["secret"]},
                        "hash_columns": {"accounts": ["email"]},
                        "placeholder": "[MASK]",
                        "hash_salt": "pepper",
                    },
                    "alias": "accounts-db",
                }
            ],
            "tags": ["demo"],
            "options": {},
            "secret_scanner": {},
        },
        "runner": {
            "output_directory": str(output_dir),
            "compress": False,
            "cleanup_staging": False,
        },
        "metadata": {},
    }

    config = offline_runner.OfflineRunnerConfig.from_dict(config_payload)
    result = offline_runner.execute_config(config, base_dir=tmp_path, timestamp="20230101T000000Z")

    assert result.package_path is None
    assert result.files, "expected collected files"
    exported = result.files[0]
    export_payload = json.loads(exported.destination.read_text())
    account_table = export_payload["tables"][0]
    assert account_table["masked_columns"] == ["secret"]
    assert account_table["rows"][0]["email"].startswith("sha256:")

    assert result.manifest_path is not None
    manifest = json.loads(result.manifest_path.read_text())
    summary = next(entry for entry in manifest["sources"] if entry["type"] == "sql_snapshot")
    assert summary["alias"] == "accounts-db"
    assert summary["tables"] == ["accounts"]


def test_offline_runner_sql_snapshot_optional(tmp_path: Path) -> None:
    output_dir = tmp_path / "optional-output"
    missing = tmp_path / "missing.sqlite"

    config_payload = {
        "schema": offline_runner.CONFIG_SCHEMA,
        "profile": {
            "name": "sql-optional",
            "description": "optional",
            "sources": [
                {
                    "sql_snapshot": {
                        "path": str(missing),
                        "optional": True,
                    }
                }
            ],
            "tags": [],
            "options": {},
            "secret_scanner": {},
        },
        "runner": {
            "output_directory": str(output_dir),
            "compress": False,
            "cleanup_staging": False,
        },
        "metadata": {},
    }

    config = offline_runner.OfflineRunnerConfig.from_dict(config_payload)
    result = offline_runner.execute_config(config, base_dir=tmp_path, timestamp="20230102T000000Z")

    assert result.files == ()
    assert result.manifest_path is not None
    manifest = json.loads(result.manifest_path.read_text())
    summary = next(entry for entry in manifest["sources"] if entry["type"] == "sql_snapshot")
    assert summary["skipped"] is True
    assert summary["reason"] == "missing"
