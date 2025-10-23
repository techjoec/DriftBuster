"""Utilities for building anonymised SQL snapshots."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import base64
import hashlib
import json
import sqlite3
from pathlib import Path
from typing import Any, Iterable, Mapping, MutableMapping, Sequence


def _normalise_column_map(values: Mapping[str, Sequence[str]] | None) -> Mapping[str, tuple[str, ...]]:
    if not values:
        return {}
    normalised: dict[str, tuple[str, ...]] = {}
    for table, columns in values.items():
        if not table:
            continue
        if isinstance(columns, Sequence):
            normalised[table] = tuple(str(column) for column in columns if str(column).strip())
        else:
            normalised[table] = (str(columns),)
    return normalised


def _parse_column_list(values: Sequence[str] | None) -> Mapping[str, tuple[str, ...]]:
    if not values:
        return {}
    grouped: dict[str, list[str]] = {}
    for entry in values:
        if not entry:
            continue
        text = entry.strip()
        if not text or "." not in text:
            continue
        table, column = text.split(".", 1)
        table = table.strip()
        column = column.strip()
        if not table or not column:
            continue
        grouped.setdefault(table, []).append(column)
    return {table: tuple(columns) for table, columns in grouped.items()}


def parse_column_map(
    values: Mapping[str, Sequence[str]] | Sequence[str] | None,
) -> Mapping[str, tuple[str, ...]]:
    if isinstance(values, Mapping):
        return _normalise_column_map(values)
    return _parse_column_list(values)


def _normalise_value(value: Any) -> Any:
    if isinstance(value, memoryview):
        value = value.tobytes()
    if isinstance(value, bytes):
        return {
            "type": "base64",
            "value": base64.b64encode(value).decode("ascii"),
        }
    if isinstance(value, datetime):
        return value.astimezone(timezone.utc).isoformat()
    if isinstance(value, (list, tuple)):
        return [_normalise_value(item) for item in value]
    if isinstance(value, dict):
        return {key: _normalise_value(val) for key, val in value.items()}
    return value


def _hash_text(value: Any, *, salt: str) -> str:
    text = json.dumps(value, sort_keys=True, default=str)
    digest = hashlib.sha256()
    digest.update(salt.encode("utf-8"))
    digest.update(text.encode("utf-8"))
    return f"sha256:{digest.hexdigest()}"


@dataclass(frozen=True)
class SnapshotTable:
    """Representation of a single exported SQL table."""

    name: str
    schema: str | None
    columns: tuple[str, ...]
    row_count: int
    rows: tuple[Mapping[str, Any], ...]
    masked_columns: tuple[str, ...]
    hashed_columns: tuple[str, ...]

    def to_dict(self) -> Mapping[str, Any]:
        return {
            "name": self.name,
            "schema": self.schema,
            "columns": list(self.columns),
            "row_count": self.row_count,
            "rows": [dict(row) for row in self.rows],
            "masked_columns": list(self.masked_columns),
            "hashed_columns": list(self.hashed_columns),
        }


@dataclass(frozen=True)
class SqlSnapshot:
    """Container describing a full database snapshot."""

    database: str
    dialect: str
    captured_at: str
    path: str
    tables: tuple[SnapshotTable, ...]

    def to_dict(self) -> Mapping[str, Any]:
        return {
            "database": self.database,
            "dialect": self.dialect,
            "captured_at": self.captured_at,
            "path": self.path,
            "tables": [table.to_dict() for table in self.tables],
        }


def _iter_tables(conn: sqlite3.Connection) -> Iterable[tuple[str, str | None]]:
    query = "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name"
    cursor = conn.execute(query)
    for name, schema in cursor.fetchall():
        if name.startswith("sqlite_"):
            continue
        yield name, schema


def build_sqlite_snapshot(
    path: Path,
    *,
    tables: Sequence[str] | None = None,
    exclude_tables: Sequence[str] | None = None,
    mask_columns: Mapping[str, Sequence[str]] | Sequence[str] | None = None,
    hash_columns: Mapping[str, Sequence[str]] | Sequence[str] | None = None,
    limit: int | None = None,
    placeholder: str = "[REDACTED]",
    hash_salt: str = "",
) -> SqlSnapshot:
    if limit is not None and limit <= 0:
        raise ValueError("limit must be positive when provided")

    resolved = Path(path)
    if not resolved.exists():
        raise FileNotFoundError(f"Database not found: {resolved}")

    include_tables = {name for name in tables or () if name}
    excluded = {name for name in exclude_tables or () if name}
    mask_map = parse_column_map(mask_columns)
    hash_map = parse_column_map(hash_columns)

    conn = sqlite3.connect(str(resolved))
    conn.row_factory = sqlite3.Row
    try:
        snapshots: list[SnapshotTable] = []
        for table_name, schema in _iter_tables(conn):
            if include_tables and table_name not in include_tables:
                continue
            if table_name in excluded:
                continue

            info_cursor = conn.execute(f"PRAGMA table_info({table_name!s})")
            columns = tuple(row[1] for row in info_cursor.fetchall())

            limit_clause = f" LIMIT {int(limit)}" if limit is not None else ""
            rows_cursor = conn.execute(f"SELECT * FROM {table_name}{limit_clause}")
            rows: list[MutableMapping[str, Any]] = []
            for fetched in rows_cursor.fetchall():
                row_payload: MutableMapping[str, Any] = {}
                for column in columns:
                    value = fetched[column]
                    if column in mask_map.get(table_name, ()):  # Mask has precedence
                        row_payload[column] = placeholder
                    elif column in hash_map.get(table_name, ()):  # Deterministic hash
                        row_payload[column] = _hash_text(
                            value,
                            salt=f"{table_name}.{column}:{hash_salt}",
                        )
                    else:
                        row_payload[column] = _normalise_value(value)
                rows.append(row_payload)

            count_cursor = conn.execute(f"SELECT COUNT(*) FROM {table_name}")
            total_rows = int(count_cursor.fetchone()[0])

            snapshots.append(
                SnapshotTable(
                    name=table_name,
                    schema=schema,
                    columns=columns,
                    row_count=total_rows,
                    rows=tuple(rows),
                    masked_columns=tuple(mask_map.get(table_name, ())),
                    hashed_columns=tuple(hash_map.get(table_name, ())),
                )
            )
    finally:
        conn.close()

    captured_at = datetime.now(timezone.utc).isoformat()
    return SqlSnapshot(
        database=resolved.name,
        dialect="sqlite",
        captured_at=captured_at,
        path=str(resolved),
        tables=tuple(snapshots),
    )


def write_sqlite_snapshot(
    path: Path,
    destination: Path,
    **kwargs: Any,
) -> SqlSnapshot:
    snapshot = build_sqlite_snapshot(path, **kwargs)
    payload = json.dumps(snapshot.to_dict(), indent=2, sort_keys=True)
    destination.write_text(payload, encoding="utf-8")
    return snapshot

