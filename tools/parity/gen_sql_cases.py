"""Generate the CPython oracle data the C# SQL snapshot tests compare against.

Temporary; deleted together with the Python package. Re-run from the repository root after editing a case table:
    python tools/parity/gen_sql_cases.py

Writes gui/DriftBuster.Backend.Tests/Sql/Data/sql_cases.json with ``ensure_ascii=True``. Sections:

* ``parse_column_map``: ``sql.snapshots.parse_column_map`` over JSON-shaped inputs; each result is a list of ``[table, [columns]]`` pairs
  in dict order, or the raised exception.
* ``normalise_value`` / ``hash_text``: ``_normalise_value`` and ``_hash_text`` over the values ``sqlite3`` hands back (int, float, str,
  bytes, None). A float travels as ``{"$float": repr}``, bytes as ``{"$bytes": hex}``.
* ``databases``: ``build_sqlite_snapshot`` over a database built by ``setup`` (each step one ``Connection.execute`` in autocommit mode, with
  parameters bound as ``sqlite3`` binds them), then byte ``patches`` (``find`` must occur once) applied to the closed file. A case may
  instead name a ``kind``: ``missing``, ``directory`` or ``bytes`` (the file holds ``content`` hex). ``path`` is the text passed, relative
  to the case directory (``{dir}`` in messages and the recorded ``path``). The result is ``to_dict()`` without ``captured_at``, and
  ``dump`` is ``json.dumps(to_dict(), indent=2, sort_keys=True)`` with ``captured_at`` set to ``<captured_at>``, as
  ``write_sqlite_snapshot`` writes it, or ``dump_error`` when ``json.dumps`` raises (a BLOB in ``sqlite_master.sql``).
* ``fixture``: the committed fixtures/sql/sample.sqlite (written by the C# test helper) exported with the fixtures/sql/README.md recipe,
  as ``dump`` with ``captured_at`` set to ``<captured_at>`` and the repository root in ``path`` written as ``{root}``.
"""

from __future__ import annotations

import json
import math
import sqlite3
import tempfile
from pathlib import Path

from driftbuster.sql import snapshots

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
OUT = ROOT / "gui" / "DriftBuster.Backend.Tests" / "Sql" / "Data" / "sql_cases.json"
FIXTURE = ROOT / "fixtures" / "sql" / "sample.sqlite"

ACCOUNTS = [
    "CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL)",
    ["INSERT INTO accounts (email, secret, balance) VALUES (?, ?, ?)", ["alice@example.com", "token-1", 42.5]],
    ["INSERT INTO accounts (email, secret, balance) VALUES (?, ?, ?)", ["bob@example.com", "token-2", 13.75]],
]


def _encode(value: object) -> object:
    if isinstance(value, float):
        return {"$float": repr(value)}
    if isinstance(value, bytes):
        return {"$bytes": value.hex()}
    if isinstance(value, list):
        return [_encode(item) for item in value]
    if isinstance(value, dict):
        return {key: _encode(item) for key, item in value.items()}
    return value


def _decode(value: object) -> object:
    if isinstance(value, dict) and set(value) == {"$float"}:
        return float(value["$float"])
    if isinstance(value, dict) and set(value) == {"$bytes"}:
        return bytes.fromhex(value["$bytes"])
    if isinstance(value, list):
        return [_decode(item) for item in value]
    return value


def _error(exc: BaseException, directory: str | None = None) -> dict[str, str]:
    message = str(exc)
    if directory is not None:
        message = message.replace(directory, "{dir}")
    return {"type": type(exc).__name__, "message": message}


def parse_column_map_cases() -> list[dict[str, object]]:
    inputs: list[object] = [
        None,
        {},
        [],
        "",
        0,
        False,
        {"accounts": ["secret", "token"]},
        {"accounts": ("secret",), "": ["skipped"], "users": []},
        {"accounts": "secret"},
        {"accounts": "a b\u3000c"},
        {"accounts": 5},
        {"accounts": 2.5},
        {"accounts": True},
        {"accounts": None},
        {"accounts": {"nested": [1]}},
        {"accounts": ["", "  ", "\u2028", " spaced ", 7, 1e16, None, False, ["x", 1], {"k": "v"}]},
        {"t\u00e9": ["\U0001f600", "a.b"]},
        ["accounts.secret", "accounts.email", "users.token"],
        ["  accounts . secret  ", "accounts.", ".secret", "nodot", "", None, 0, [], {}],
        ["a.b.c", "x..y", "\u3000t\u3000.\u3000c\u3000", "t.\U0001f600", "\u00e9.\u00e8"],
        ["users.token", "accounts.secret", "users.name"],
        "accounts.secret",
        ".",
        ["a.b", 5],
        ["a.b", True],
        ["a.b", 2.5],
        ["a.b", ["c.d"]],
        ["a.b", {"c": "d"}],
        5,
        2.5,
        True,
        [None, False, 0, 0.0, "", [], {}],
    ]
    cases = []
    for value in inputs:
        entry: dict[str, object] = {"input": value}
        try:
            result = snapshots.parse_column_map(value)  # type: ignore[arg-type]
            entry["result"] = [[table, list(columns)] for table, columns in result.items()]
        except Exception as exc:  # noqa: BLE001 - every raise is oracle data
            entry["error"] = _error(exc)
        cases.append(entry)
    return cases


VALUES: list[object] = [
    None,
    0,
    1,
    -1,
    2**63 - 1,
    -(2**63),
    0.0,
    -0.0,
    42.5,
    13.75,
    0.1,
    1e16,
    1e-7,
    1.5e300,
    5e-324,
    123456789.123456789,
    math.inf,
    -math.inf,
    "",
    "alice@example.com",
    "quote ' and \" and \\",
    "controls \x00\x01\x1f\x7f \t\n\r\b\f",
    "unicode caf\u00e9 \u2028 \ufeff",
    "astral \U0001f600",
    b"",
    b"audit",
    b"\x00\x01\x7f\x80\xff",
    b"it's",
    b'say "hi"',
    b"both ' and \"",
    b"back\\slash\t\n\r",
]

SALTS = ["", "pepper", "accounts.email:pepper", "s\u00e5lt \U0001f600:x"]


def normalise_value_cases() -> list[dict[str, object]]:
    return [{"value": _encode(value), "result": _encode(snapshots._normalise_value(value))} for value in VALUES]


def hash_text_cases() -> list[dict[str, object]]:
    return [
        {"value": _encode(value), "salt": salt, "result": snapshots._hash_text(value, salt=salt)}
        for value in VALUES
        for salt in SALTS
    ]


def _db(name: str, setup: list[object], **kwargs: object) -> dict[str, object]:
    return {"name": name, "setup": setup, "kwargs": kwargs}


NAN_PATCH = {"find": "3ff8000000000000", "replace": "7ff8000000000000"}

DATABASES: list[dict[str, object]] = [
    _db(
        "accounts-mask-hash-mapping",
        ACCOUNTS,
        mask_columns={"accounts": ["secret"]},
        hash_columns={"accounts": ["email"]},
        placeholder="[MASK]",
        hash_salt="pepper",
    ),
    _db(
        "accounts-list-forms-limit",
        [*ACCOUNTS, "CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB)", ["INSERT INTO audit (payload) VALUES (?)", [b"audit"]]],
        tables=["accounts"],
        exclude_tables=["nonexistent"],
        mask_columns=["accounts.secret"],
        hash_columns=["accounts.email"],
        limit=1,
    ),
    _db("accounts-defaults", [*ACCOUNTS, "CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB)", ["INSERT INTO audit (payload) VALUES (?)", [b"audit"]]]),
    _db(
        "mask-wins-over-hash-and-unknown-columns",
        ACCOUNTS,
        mask_columns={"accounts": ["email", "missing"], "ghost": ["x"]},
        hash_columns=["accounts.email", "accounts.balance", "accounts.id", "accounts.secret"],
        placeholder="",
        hash_salt="s\u00e5lt \U0001f600",
    ),
    _db("placeholder-none", ACCOUNTS, mask_columns=["accounts.secret"], placeholder=None),
    _db(
        "tables-and-exclude",
        [*ACCOUNTS, "CREATE TABLE b (v)", "INSERT INTO b VALUES (1)", "CREATE TABLE c (v)", "INSERT INTO c VALUES (2)"],
        tables=["b", "c", "", "sqlite_sequence", "zzz"],
        exclude_tables=["c", ""],
    ),
    _db("exclude-only", [*ACCOUNTS, "CREATE TABLE b (v)"], exclude_tables=["accounts"]),
    _db("empty-tables-filter", [*ACCOUNTS], tables=[], exclude_tables=[]),
    _db("limit-larger-than-rows", ACCOUNTS, limit=10),
    _db("limit-int64-overflow", ACCOUNTS, limit=2**63),
    _db("limit-huge", ACCOUNTS, limit=10**30),
    _db("limit-zero", ACCOUNTS, limit=0),
    _db("limit-negative", ACCOUNTS, limit=-3),
    _db(
        "value-storage-classes",
        [
            "CREATE TABLE v (anything, i INTEGER, r REAL, n NUMERIC, t TEXT, b BLOB)",
            ["INSERT INTO v VALUES (?, ?, ?, ?, ?, ?)", [2**63 - 1, "12", 3, "1e3", 45, b"\x00\xff"]],
            ["INSERT INTO v VALUES (?, ?, ?, ?, ?, ?)", [-(2**63), "abc", "4.0", "0x10", 1.5, "text in blob"]],
            ["INSERT INTO v VALUES (?, ?, ?, ?, ?, ?)", [-0.0, -0.0, -0.0, -0.0, -0.0, -0.0]],
            ["INSERT INTO v VALUES (?, ?, ?, ?, ?, ?)", [math.nan, math.nan, math.nan, None, b"", ""]],
            "INSERT INTO v VALUES (9e999, -9e999, 9e999, 1e16, 'x', x'')",
            ["INSERT INTO v VALUES (?, ?, ?, ?, ?, ?)", [0.1, 1e-7, 5e-324, 123456789.123456789, "\U0001f600 caf\u00e9", b"it's"]],
            "INSERT INTO v VALUES (CAST(x'410042' AS TEXT), '\u2028', 'quote '' \" \\', char(1, 31, 127), x'22275c', NULL)",
        ],
    ),
    _db(
        "value-storage-classes-hashed",
        [
            "CREATE TABLE v (anything, i INTEGER, r REAL, t TEXT, b BLOB)",
            ["INSERT INTO v VALUES (?, ?, ?, ?, ?)", [2**63 - 1, "12", 3, "\U0001f600", b"\x00\xff'\""]],
            ["INSERT INTO v VALUES (?, ?, ?, ?, ?)", [None, -0.0, 1.5, "tab\there", b"back\\slash"]],
            "INSERT INTO v VALUES (9e999, -9e999, 1e300, CAST(x'410042' AS TEXT), x'')",
        ],
        hash_columns={"v": ["anything", "i", "r", "t", "b"]},
        hash_salt="pepper",
    ),
    _db("nan-bits-in-real", ["CREATE TABLE r (x REAL, y)", ["INSERT INTO r VALUES (?, ?)", [1.5, "keep"]]], _patches=[NAN_PATCH]),
    _db("invalid-utf8-text", ["CREATE TABLE t (a, v)", "INSERT INTO t VALUES (1, CAST(x'41ff42' AS TEXT))"]),
    _db("surrogate-utf8-text", ["CREATE TABLE t (v)", "INSERT INTO t VALUES (CAST(x'eda080' AS TEXT))"]),
    _db("invalid-utf8-long-text", ["CREATE TABLE t (v)", "INSERT INTO t VALUES (CAST(x'" + "41" * 300 + "ff' AS TEXT))"]),
    _db("invalid-utf8-after-nul", ["CREATE TABLE t (v)", "INSERT INTO t VALUES (CAST(x'410042ff' AS TEXT))"]),
    _db("invalid-utf8-masked-still-decoded", ["CREATE TABLE t (v)", "INSERT INTO t VALUES (CAST(x'c3' AS TEXT))"], mask_columns=["t.v"]),
    _db("invalid-utf8-column-name-escape", ["CREATE TABLE \"t\u00e9\" (\"c\u00f6l\")", "INSERT INTO \"t\u00e9\" VALUES (CAST(x'e282' AS TEXT))"]),
    _db(
        "invalid-utf8-table-name",
        [
            "CREATE TABLE t (v)",
            "INSERT INTO t VALUES (1)",
            "PRAGMA writable_schema=ON",
            "UPDATE sqlite_master SET name = CAST(x'74ff' AS TEXT), tbl_name = CAST(x'74ff' AS TEXT), "
            "sql = CAST(x'435245415445205441424c45202274ff22287629' AS TEXT) WHERE name = 't'",
        ],
    ),
    _db("blob-table-name", ["CREATE TABLE t (v)", "PRAGMA writable_schema=ON", "UPDATE sqlite_master SET name = x'74' WHERE name = 't'"]),
    _db("integer-table-name", ["CREATE TABLE t (v)", "PRAGMA writable_schema=ON", "UPDATE sqlite_master SET name = 5 WHERE name = 't'"]),
    _db(
        "blob-table-schema",
        ["CREATE TABLE t (v)", "PRAGMA writable_schema=ON", "UPDATE sqlite_master SET sql = CAST(sql AS BLOB) WHERE name = 't'"],
    ),
    _db(
        "internal-and-non-table-objects",
        [
            "CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, v)",
            "INSERT INTO t (v) VALUES ('a')",
            "INSERT INTO t (v) VALUES ('b')",
            "CREATE VIEW vw AS SELECT * FROM t",
            "CREATE INDEX ix ON t (v)",
            "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END",
            "ANALYZE",
        ],
    ),
    _db(
        "table-name-ordering",
        [
            "CREATE TABLE \"b\" (v)",
            "CREATE TABLE \"C\" (v)",
            "CREATE TABLE \"a\" (v)",
            "CREATE TABLE \"\u00e9\" (v)",
            "CREATE TABLE \"z\" (v)",
            "CREATE TABLE \"\U0001f600\" (v)",
            "CREATE TABLE \"\uff21\" (v)",
            "CREATE TABLE \"_x\" (v)",
        ],
    ),
    _db("name-with-space", ["CREATE TABLE \"t t\" (v)", "INSERT INTO \"t t\" VALUES (1)"]),
    _db("name-with-single-quote", ["CREATE TABLE \"t'x\" (v)"]),
    _db("name-with-double-quotes", ["CREATE TABLE \"\"\"q\"\"\" (v)"]),
    _db("name-bracket-quoted", ["CREATE TABLE [t] (v)", "INSERT INTO [t] VALUES (1)"]),
    _db("name-unicode", ["CREATE TABLE \"t\u00ebst \U0001f600\" (v)", "INSERT INTO \"t\u00ebst \U0001f600\" VALUES (1)"]),
    _db("name-unicode-no-space", ["CREATE TABLE \"t\u00ebst\U0001f600\" (\"c\u00f6l\")", "INSERT INTO \"t\u00ebst\U0001f600\" VALUES (1)"]),
    _db("name-hyphen", ["CREATE TABLE \"t-1\" (v)"]),
    _db("name-leading-digit", ["CREATE TABLE \"1t\" (v)"]),
    _db("name-keyword", ["CREATE TABLE \"select\" (v)"]),
    _db("name-semicolon", ["CREATE TABLE \"t;x\" (v)"]),
    _db("name-line-comment", ["CREATE TABLE \"t--\" (v)"]),
    _db("name-second-statement", ["CREATE TABLE t (v)", "CREATE TABLE \"t); SELECT (1\" (v)"]),
    _db("name-trailing-comment", ["CREATE TABLE t (v)", "CREATE TABLE \"t) --\" (v)"]),
    _db(
        "name-block-comment-reads-other-table",
        [
            "CREATE TABLE t (a, b)",
            "INSERT INTO t VALUES (1, 'one')",
            "INSERT INTO t VALUES (2, 'two')",
            "CREATE TABLE \"t/**/\" (z)",
        ],
        hash_columns={"t/**/": ["b"]},
    ),
    _db("name-with-limit-words", ["CREATE TABLE \"t LIMIT 0\" (v)"]),
    _db("name-nul-in-sql", ["CREATE TABLE t (v)", "PRAGMA writable_schema=ON", "UPDATE sqlite_master SET name = 't' || char(0) || 'x' WHERE name = 't'"]),
    _db(
        "odd-column-names",
        [
            "CREATE TABLE t (\"a b\", \"it's\", \"\"\"q\"\"\", \"\u00c9\", \"\u00e9\", \"Ab\", \"\U0001f600\", \"[x]\", \"sp \")",
            "INSERT INTO t VALUES (1, 2, 3, 4, 5, 6, 7, 8, 9)",
        ],
        mask_columns={"t": ["it's"]},
        hash_columns={"t": ["\u00e9", "\U0001f600"]},
    ),
    _db(
        "generated-columns",
        ["CREATE TABLE g (a INTEGER, b AS (a * 2), c AS (a || 'x') STORED)", "INSERT INTO g (a) VALUES (1)", "INSERT INTO g (a) VALUES (2)"],
    ),
    _db(
        "without-rowid-and-rowid-order",
        [
            "CREATE TABLE w (k TEXT PRIMARY KEY, v) WITHOUT ROWID",
            "INSERT INTO w VALUES ('b', 1)",
            "INSERT INTO w VALUES ('a', 2)",
            "CREATE TABLE r (v)",
            "INSERT INTO r (rowid, v) VALUES (5, 'five')",
            "INSERT INTO r (rowid, v) VALUES (2, 'two')",
            "INSERT INTO r (rowid, v) VALUES (9, 'nine')",
            "DELETE FROM r WHERE rowid = 5",
        ],
        limit=1,
    ),
    _db("strict-table", ["CREATE TABLE s (i INTEGER, t TEXT, a ANY) STRICT", "INSERT INTO s VALUES (1, 'x', 2.5)"]),
    _db("rtree-virtual-table", ["CREATE VIRTUAL TABLE rt USING rtree(id, x0, x1)", "INSERT INTO rt VALUES (1, 0.5, 1.5)"]),
    _db("fts5-virtual-table", ["CREATE VIRTUAL TABLE f USING fts5(x)", "INSERT INTO f VALUES ('hello world')"]),
    _db("utf16-database", ["PRAGMA encoding = 'UTF-16le'", "CREATE TABLE u (v)", ["INSERT INTO u VALUES (?)", ["caf\u00e9 \U0001f600"]]]),
    _db(
        "utf16-database-lone-surrogate",
        ["PRAGMA encoding = 'UTF-16le'", "CREATE TABLE u (v)", "INSERT INTO u VALUES (CAST(x'3d00d8' AS TEXT))"],
    ),
    _db("empty-database", []),
    _db("empty-table", ["CREATE TABLE e (a, b)"], hash_columns=["e.a"]),
    _db("dotted-path", ACCOUNTS, _path="./sub/../case.sqlite"),
    _db("trailing-slash-path", ACCOUNTS, _path="case.sqlite/"),
    {"name": "missing-file", "kind": "missing", "kwargs": {}},
    {"name": "missing-file-limit-zero", "kind": "missing", "kwargs": {"limit": 0}},
    {"name": "directory", "kind": "directory", "kwargs": {}},
    {"name": "not-a-database", "kind": "bytes", "content": ("hello world" * 100).encode().hex(), "kwargs": {}},
    {"name": "zero-bytes", "kind": "bytes", "content": "", "kwargs": {}},
    {"name": "header-only", "kind": "bytes", "content": b"SQLite format 3\x00".hex(), "kwargs": {}},
    {"name": "bad-mask-spec", "setup": ACCOUNTS, "kwargs": {"mask_columns": ["accounts.secret", 5]}},
    {"name": "bad-hash-spec", "setup": ACCOUNTS, "kwargs": {"hash_columns": 7}},
    {"name": "missing-file-bad-spec", "kind": "missing", "kwargs": {"mask_columns": 7}},
]


def _execute(connection: sqlite3.Connection, step: object) -> None:
    if isinstance(step, str):
        connection.execute(step)
    else:
        sql, params = step  # type: ignore[misc]
        connection.execute(sql, _decode(params))


def _build(case: dict[str, object], directory: Path) -> str:
    kind = case.get("kind", "setup")
    target = directory / "case.sqlite"
    if kind == "missing":
        return str(target)
    if kind == "directory":
        target.mkdir()
        return str(target)
    if kind == "bytes":
        target.write_bytes(bytes.fromhex(case["content"]))  # type: ignore[arg-type]
        return str(target)
    (directory / "sub").mkdir()
    connection = sqlite3.connect(target, isolation_level=None)
    try:
        for step in case["setup"]:  # type: ignore[union-attr]
            _execute(connection, step)
    finally:
        connection.close()
    patches = case.get("patches", [])
    if patches:
        data = target.read_bytes()
        for patch in patches:  # type: ignore[union-attr]
            find = bytes.fromhex(patch["find"])
            if data.count(find) != 1:
                raise SystemExit(f"{case['name']}: patch {patch['find']} occurs {data.count(find)} times")
            data = data.replace(find, bytes.fromhex(patch["replace"]))
        target.write_bytes(data)
    return str(directory / case.get("path", "case.sqlite"))  # type: ignore[operator]


def database_cases() -> list[dict[str, object]]:
    cases = []
    for raw in DATABASES:
        case = dict(raw)
        kwargs = dict(case["kwargs"])  # type: ignore[call-overload]
        if "_patches" in kwargs:
            case["patches"] = kwargs.pop("_patches")
        if "_path" in kwargs:
            case["path"] = kwargs.pop("_path")
        case["kwargs"] = kwargs
        with tempfile.TemporaryDirectory(prefix="driftbuster-sql-oracle-") as tmp:
            directory = Path(tmp)
            path = _build(case, directory)
            entry: dict[str, object] = dict(case)
            if "setup" in entry:
                entry["setup"] = _encode(entry["setup"])
            try:
                snapshot = snapshots.build_sqlite_snapshot(Path(path), **kwargs)  # type: ignore[arg-type]
            except Exception as exc:  # noqa: BLE001 - every raise is oracle data
                entry["error"] = _error(exc, tmp)
            else:
                payload = dict(snapshot.to_dict())
                payload.pop("captured_at")
                payload["path"] = payload["path"].replace(tmp, "{dir}")
                entry["result"] = _encode(payload)
                dumped = dict(snapshot.to_dict())
                dumped["captured_at"] = "<captured_at>"
                dumped["path"] = payload["path"]
                try:
                    entry["dump"] = json.dumps(dumped, indent=2, sort_keys=True)
                except TypeError as exc:
                    entry["dump_error"] = _error(exc)
        cases.append(entry)
    return cases


def fixture_case() -> dict[str, object]:
    kwargs = {"mask_columns": ["accounts.secret"], "hash_columns": ["accounts.email"], "placeholder": "[MASK]", "hash_salt": "pepper"}
    snapshot = snapshots.build_sqlite_snapshot(FIXTURE, **kwargs)  # type: ignore[arg-type]
    payload = dict(snapshot.to_dict())
    payload["captured_at"] = "<captured_at>"
    payload["path"] = payload["path"].replace(str(ROOT), "{root}")
    return {"kwargs": kwargs, "dump": json.dumps(payload, indent=2, sort_keys=True)}


def main() -> None:
    payload = {
        "sqlite_version": sqlite3.sqlite_version,
        "parse_column_map": parse_column_map_cases(),
        "normalise_value": normalise_value_cases(),
        "hash_text": hash_text_cases(),
        "databases": database_cases(),
        "fixture": fixture_case(),
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(payload, ensure_ascii=True, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
