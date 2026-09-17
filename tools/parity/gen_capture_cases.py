"""Generate the CPython oracle data the C# capture runner tests compare against.

Temporary; deleted together with the Python package. Re-run from the repository root after editing a case table:
    python tools/parity/gen_capture_cases.py

Writes gui/DriftBuster.Backend.Tests/Remote/Data/capture_cases.json with ``ensure_ascii=True``. Every case runs in a fresh temporary
directory whose resolved path is written as ``{dir}``, in the options as in the recorded output. ``tree`` holds the files created first
(text, or ``{"b64": ...}`` for bytes; a ``{"sql": [steps]}`` entry is a SQLite database built one ``Connection.execute`` per step in
autocommit mode, parameters bound as ``sqlite3`` binds them). Each run patches exactly what the script reads from its environment:
``datetime.now`` (one queue shared by ``scripts.capture`` and ``driftbuster.sql.snapshots``, ``now``), ``time.monotonic`` (``monotonic``),
``socket.gethostname`` (``host``) and ``os.getenv`` (``env``). Hunts use ``default_rules()`` with fix e applied, as the port ships them.
The result is the exit code (or the escaped exception as ``{"type", "message"}``), stdout, stderr and every file under ``{dir}/out`` as
text. ``divergence`` names an approved difference the C# test normalises (``json-decode-text``: the ``JSONDecodeError`` reason, which the
port reports as ``invalid JSON document``).

Sections: ``run`` (``run_capture``), ``export_sql`` (``run_sql_export``), ``compare`` (``compare_snapshots``), ``registry_summaries``
(``_summarise_registry_scan`` over one file) and ``manifest`` (``_build_manifest_payload`` over explicit values, as JSON text).
"""

from __future__ import annotations

import argparse
import base64
import contextlib
import io
import json
import shutil
import sqlite3
import sys
import tempfile
from datetime import datetime
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT))

from gen_hunt_secret_cases import _default_rules_with_fix_e  # noqa: E402

from driftbuster.sql import snapshots  # noqa: E402
from scripts import capture  # noqa: E402

OUT = ROOT / "gui" / "DriftBuster.Backend.Tests" / "Remote" / "Data" / "capture_cases.json"

THUMBPRINT = "0123456789abcdef0123456789abcdef01234567"

TREE: dict[str, object] = {
    "tree/app/appsettings.json": (
        '{\n  "ConnectionStrings": {"Default": "Server=sql.corp.local;Password=hunter2"},\n'
        '  "Version": "1.2.3",\n  "Name": "na\u00efve \u2603"\n}\n'
    ),
    "tree/app/web.config": (
        '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <appSettings>\n'
        '    <add key="ServiceEndpoint" value="https://api.example.com/v1/" />\n'
        '    <add key="FeatureFlag:NewDashboard" value="true" />\n  </appSettings>\n</configuration>\n'
    ),
    "tree/app/settings.ini": f"[server]\nhost = db01.internal\ncertificate thumbprint = {THUMBPRINT}\n",
    "tree/notes/readme.txt": "install path C:\\Program Files\\Vendor\nversion 2.0.1\nINSTALL_PATH=/opt/vendor-app/bin\n",
    "tree/bin/blob.dat": {"b64": base64.b64encode(bytes(range(256)) * 4).decode("ascii")},
}

PROFILES = json.dumps(
    {
        "profiles": [
            {
                "name": "app",
                "description": "Application configs",
                "tags": ["prod"],
                "metadata": {"owner": "ops", "hunter2": "key-not-redacted"},
                "configs": [
                    {"id": "appsettings", "path": "app/appsettings.json", "expected_format": "json", "tags": ["b", "a"]},
                    {"id": "web", "path_glob": "app/*.config", "application": "portal", "metadata": {"ignore_review_flags": True}},
                ],
            },
            {"name": "other", "tags": ["dev"], "configs": [{"id": "ini", "path": "app/settings.ini", "version": "7"}]},
            {"name": "untagged", "configs": [{"id": "notes", "path_glob": "notes/*.txt"}, {"id": "any-json", "path_glob": "**/*.json"}]},
        ]
    }
)

REGISTRY_A = json.dumps(
    {
        "token": "VendorA",
        "roots": [
            {"hive": "HKLM", "path": "Software\\VendorA"},
            {"hive": " HKCU ", "path": "  Software\\VendorA\\Sub ", "view": "32"},
            {"hive": "", "path": "Software\\Skipped"},
            {"hive": "HKLM", "path": "   "},
            "not-a-mapping",
            {"hive": 5, "path": ["a", 1], "view": 0},
            {"hive": "HKLM", "path": "Software\\Float", "view": 2.5},
        ],
        "requested_roots": [{"hive": "HKLM", "path": "Software\\VendorA", "view": "64"}, {"path": "x"}],
        "hits": [{"hive": "HKLM"}, {"hive": "HKCU"}],
    }
)

REGISTRY_B = json.dumps({"token": {"nested": [1, None]}, "roots": None, "requested_roots": "abc", "hits": "hits"})

BASE_RUN = {
    "root": "{dir}/tree",
    "profiles": None,
    "profile_tags": [],
    "glob": "**/*",
    "hunt_glob": "**/*",
    "hunt_exclude": [],
    "skip_hunt": False,
    "sample_size": 128 * 1024,
    "output_dir": "{dir}/out",
    "capture_id": "cap-1",
    "operator": "alice",
    "environment": "lab",
    "reason": "baseline",
    "mask_tokens": ["hunter2", "corp"],
    "placeholder": "[REDACTED]",
    "allow_unmasked": False,
    "registry_scan": [],
}

NOW = ["2025-03-12T01:02:03.456789+00:00", "2025-03-12T01:02:04+00:00", "2025-03-12T01:02:05.000001+00:00"]
MONOTONIC = [100.0, 100.0005, 101.2345, 101.2345, 102.0015, 102.6665]


def _run_case(
    name: str, *, tree: dict[str, object] | None = None, env: dict[str, str] | None = None, **options: object
) -> dict[str, object]:
    merged = dict(BASE_RUN)
    merged.update(options)
    return {
        "name": name,
        "tree": TREE if tree is None else tree,
        "options": merged,
        "env": env or {},
        "host": "capture-host.example",
        "now": NOW,
        "monotonic": MONOTONIC,
    }


RUN_CASES = [
    _run_case("masked-capture"),
    _run_case(
        "env-operator-default-id-unmasked",
        capture_id=None,
        operator=None,
        mask_tokens=[],
        allow_unmasked=True,
        env={"DRIFTBUSTER_CAPTURE_OPERATOR": "  ops  ", "USER": "ignored"},
    ),
    _run_case("user-name-fallback", capture_id="", operator="   ", env={"USER": "", "USERNAME": " winuser "}),
    _run_case("user-fallback", operator=None, env={"USER": "posix-user"}),
    _run_case(
        "profiles-with-tag",
        tree={**TREE, "profiles.json": PROFILES},
        profiles="{dir}/profiles.json",
        profile_tags=["prod"],
        mask_tokens=["example", "ops"],
    ),
    _run_case("profiles-without-tags", tree={**TREE, "profiles.json": PROFILES}, profiles="{dir}/profiles.json"),
    _run_case("skip-hunt-glob", skip_hunt=True, glob="app/*"),
    _run_case("hunt-glob-and-exclude", hunt_glob="**/*.*", hunt_exclude=["*.ini", "notes/*"]),
    _run_case(
        "registry-scans",
        tree={**TREE, "scans/registry_scan.json": REGISTRY_A, "scans/b/registry_scan.json": REGISTRY_B},
        registry_scan=["{dir}/scans/registry_scan.json", "{dir}/scans/b/../b/registry_scan.json"],
    ),
    _run_case("registry-scan-missing", registry_scan=["{dir}/scans/missing.json"]),
    _run_case(
        "registry-scan-invalid-json",
        tree={**TREE, "scan.json": "{not json"},
        registry_scan=["{dir}/scan.json"],
    ),
    _run_case("registry-scan-list", tree={**TREE, "scan.json": "[1, 2]"}, registry_scan=["{dir}/scan.json"]),
    _run_case("registry-scan-int-hits", tree={**TREE, "scan.json": '{"hits": 5}'}, registry_scan=["{dir}/scan.json"]),
    _run_case("registry-scan-directory", tree={**TREE, "scans/dir/keep.txt": "x"}, registry_scan=["{dir}/scans/dir"]),
    _run_case("registry-scan-not-utf8", tree={**TREE, "scan.json": {"b64": base64.b64encode(b'{"token": "\xff"}').decode()}},
              registry_scan=["{dir}/scan.json"]),
    _run_case("root-missing", root="{dir}/nope"),
    _run_case("no-mask-tokens", mask_tokens=[]),
    _run_case("no-operator", operator=None, env={"USER": "  "}),
    _run_case("blank-environment", environment="  "),
    _run_case("missing-reason", reason=None),
    _run_case("profiles-missing", profiles="{dir}/missing.json"),
    _run_case(
        "profiles-lenient-fallback",
        tree={**TREE, "profiles.json": json.dumps({"profiles": [5, {"name": "p", "configs": ["x", {"id": 7, "path": "app/web.config"}]}]})},
        profiles="{dir}/profiles.json",
    ),
    _run_case(
        "profiles-fallback-key-error",
        tree={**TREE, "profiles.json": json.dumps({"profiles": [{"name": "p", "configs": [{"path": "x"}]}]})},
        profiles="{dir}/profiles.json",
    ),
    _run_case("sample-size-clamped", sample_size=600000),
    _run_case("small-sample", sample_size=64),
    _run_case("unmatched-mask-token", mask_tokens=["absent-token"]),
    _run_case("placeholder-redacted", mask_tokens=["RED", "format"], placeholder="[REDACTED]"),
    _run_case("empty-mask-token", mask_tokens=[""]),
    _run_case("file-root", root="{dir}/tree/app/appsettings.json"),
    _run_case("capture-id-subdirectory", capture_id="nested/cap"),
    _run_case("non-ascii-tokens", mask_tokens=["\u2603", "na\u00efve"], placeholder="\u2588"),
    _run_case("relative-parts-root", root="{dir}/tree/app/../notes/."),
]


def _write_tree(base: Path, tree: dict[str, object]) -> None:
    for relative, content in tree.items():
        path = base / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, str):
            path.write_text(content, encoding="utf-8")
        elif isinstance(content, dict) and "b64" in content:
            path.write_bytes(base64.b64decode(content["b64"]))
        elif isinstance(content, dict) and "sql" in content:
            connection = sqlite3.connect(path, isolation_level=None)
            try:
                for step in content["sql"]:
                    if isinstance(step, str):
                        connection.execute(step)
                    else:
                        connection.execute(step[0], step[1])
            finally:
                connection.close()
        else:
            raise TypeError(f"unsupported tree entry {relative}")


def _substitute(value: object, directory: str) -> object:
    if isinstance(value, str):
        return value.replace("{dir}", directory)
    if isinstance(value, list):
        return [_substitute(item, directory) for item in value]
    return value


def _collect(out: Path, directory: str) -> dict[str, str]:
    files: dict[str, str] = {}
    if out.is_dir():
        for path in sorted(out.rglob("*")):
            if path.is_file():
                files[path.relative_to(out).as_posix()] = path.read_bytes().decode("utf-8").replace(directory, "{dir}")
    return files


class _Queue:
    def __init__(self, values: list[object]) -> None:
        self.values = list(values)

    def pop(self) -> object:
        return self.values.pop(0)


def _patched(case: dict[str, object]) -> contextlib.ExitStack:
    stack = contextlib.ExitStack()
    now = _Queue(list(case.get("now", [])))  # type: ignore[arg-type]
    clock = _Queue(list(case.get("monotonic", [])))  # type: ignore[arg-type]
    env = dict(case.get("env", {}))  # type: ignore[arg-type]

    class FakeDateTime(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return datetime.fromisoformat(str(now.pop()))

    saved = (capture.datetime, snapshots.datetime, capture.time, capture.socket, capture.os, capture.default_rules)
    capture.datetime = FakeDateTime  # type: ignore[misc]
    snapshots.datetime = FakeDateTime  # type: ignore[misc]
    capture.time = SimpleNamespace(monotonic=clock.pop)  # type: ignore[assignment]
    capture.socket = SimpleNamespace(gethostname=lambda: case.get("host", "host"))  # type: ignore[assignment]
    capture.os = SimpleNamespace(getenv=lambda key, default=None: env.get(key, default))  # type: ignore[assignment]
    capture.default_rules = _default_rules_with_fix_e  # type: ignore[assignment]

    def restore() -> None:
        (capture.datetime, snapshots.datetime, capture.time, capture.socket, capture.os, capture.default_rules) = saved

    stack.callback(restore)
    return stack


def _execute(case: dict[str, object], handler, build_args) -> dict[str, object]:  # type: ignore[no-untyped-def]
    temp = tempfile.mkdtemp(prefix="capture-oracle-")
    base = Path(temp).resolve()
    directory = str(base)
    try:
        _write_tree(base, case.get("tree", {}))  # type: ignore[arg-type]
        args = build_args(directory)
        stdout, stderr = io.StringIO(), io.StringIO()
        result: dict[str, object] = {}
        with _patched(case), contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            try:
                result["exit_code"] = handler(args)
            except Exception as exc:
                result["error"] = {"type": type(exc).__name__, "message": str(exc).replace(directory, "{dir}")}
        result["stdout"] = stdout.getvalue().replace(directory, "{dir}")
        result["stderr"] = stderr.getvalue().replace(directory, "{dir}")
        result["files"] = _collect(base / "out", directory)
        return result
    finally:
        shutil.rmtree(base)


def run_cases() -> list[dict[str, object]]:
    cases = []
    for case in RUN_CASES:
        options = case["options"]

        def build(directory: str, options: dict[str, object] = options) -> argparse.Namespace:  # type: ignore[assignment]
            return argparse.Namespace(**{key: _substitute(value, directory) for key, value in options.items()})

        entry = dict(case)
        entry["result"] = _execute(case, capture.run_capture, build)
        if str(case["name"]).endswith("invalid-json"):
            entry["divergence"] = "json-decode-text"
        cases.append(entry)
    return cases


ACCOUNTS = [
    "CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL)",
    ["INSERT INTO accounts (email, secret, balance) VALUES (?, ?, ?)", ["alice@example.com", "token-1", 42.5]],
    ["INSERT INTO accounts (email, secret, balance) VALUES (?, ?, ?)", ["bob@example.com", "token-2", 13.75]],
]
AUDIT = [*ACCOUNTS, "CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB)", ["INSERT INTO audit (payload) VALUES (?)", [b"audit"]]]

BASE_SQL = {
    "database": ["{dir}/capture.sqlite"],
    "output_dir": "{dir}/out",
    "table": [],
    "exclude_table": [],
    "mask_column": ["accounts.secret"],
    "hash_column": ["accounts.email"],
    "placeholder": "[MASK]",
    "hash_salt": "pepper",
    "limit": None,
    "prefix": "demo",
}


def _sql_case(name: str, *, tree: dict[str, object] | None = None, **options: object) -> dict[str, object]:
    merged = dict(BASE_SQL)
    merged.update(options)
    return {
        "name": name,
        "tree": {"capture.sqlite": {"sql": ACCOUNTS}} if tree is None else tree,
        "options": merged,
        "now": [f"2025-03-12T02:00:0{index}.{index}00000+00:00" for index in range(10)],
    }


def _json_sql(steps: list[object]) -> list[object]:
    return [
        step if isinstance(step, str) else [step[0], [{"$bytes": v.hex()} if isinstance(v, bytes) else v for v in step[1]]]
        for step in steps
    ]


SQL_CASES = [
    _sql_case("subcommand-writes-manifest"),
    _sql_case(
        "two-databases-with-prefix",
        tree={"capture.sqlite": {"sql": ACCOUNTS}, "audit.db": {"sql": AUDIT}},
        database=["{dir}/capture.sqlite", "{dir}/audit.db"],
        hash_salt=None,
    ),
    _sql_case(
        "two-databases-without-prefix",
        tree={"capture.sqlite": {"sql": ACCOUNTS}, "audit.db": {"sql": AUDIT}},
        database=["{dir}/capture.sqlite", "{dir}/audit.db"],
        prefix="",
        limit=1,
    ),
    _sql_case("same-database-twice", database=["{dir}/capture.sqlite", "{dir}/./capture.sqlite"]),
    _sql_case(
        "existing-outputs-are-kept",
        tree={"capture.sqlite": {"sql": ACCOUNTS}, "out/demo-sql-snapshot.json": "old", "out/demo-sql-snapshot-1.json": "old"},
    ),
    _sql_case("missing-database", database=["{dir}/missing.sqlite", "{dir}/capture.sqlite"]),
    _sql_case("limit-zero", limit=0),
    _sql_case(
        "tables-and-column-arguments",
        tree={"capture.sqlite": {"sql": AUDIT}},
        table=["audit", "accounts", "ghost"],
        exclude_table=["audit"],
        mask_column=["", "nodot", ".secret", "accounts.", "  accounts . secret  ", "audit.payload", "a.b.c"],
        hash_column=["accounts.email", "accounts.email"],
        placeholder="",
    ),
    _sql_case("not-a-database", tree={"capture.sqlite": "plain text, not sqlite"}),
    _sql_case(
        "blob-schema-escapes",
        tree={
            "capture.sqlite": {"sql": ACCOUNTS},
            "blob.sqlite": {
                "sql": [
                    "CREATE TABLE t (v)",
                    "PRAGMA writable_schema=ON",
                    "UPDATE sqlite_master SET sql = CAST(sql AS BLOB) WHERE name = 't'",
                ]
            },
        },
        database=["{dir}/capture.sqlite", "{dir}/blob.sqlite"],
    ),
]


def export_sql_cases() -> list[dict[str, object]]:
    cases = []
    for case in SQL_CASES:
        options = case["options"]

        def build(directory: str, options: dict[str, object] = options) -> argparse.Namespace:  # type: ignore[assignment]
            return argparse.Namespace(**{key: _substitute(value, directory) for key, value in options.items()})

        entry = dict(case)
        entry["tree"] = {
            key: ({"sql": _json_sql(value["sql"])} if isinstance(value, dict) and "sql" in value else value)
            for key, value in case["tree"].items()  # type: ignore[union-attr]
        }
        entry["result"] = _execute(case, capture.run_sql_export, build)
        cases.append(entry)
    return cases


def _detection(path: str, fmt: object, variant: object, **extra: object) -> dict[str, object]:
    detection = {"plugin": "json", "format": fmt, "variant": variant, "confidence": 0.9, "reasons": [], "metadata": {}}
    detection.update(extra)
    return {"path": "/abs/" + str(path), "relative_path": path, "detection": detection}


def _summary(*profiles: tuple[object, list[object]], **totals: object) -> dict[str, object]:
    payload: dict[str, object] = dict(totals)
    payload["profiles"] = [{"name": name, "config_ids": ids, "config_count": len(ids)} for name, ids in profiles]
    return payload


def _hits(*tokens: object) -> list[object]:
    return [{"rule": {"token_name": token}} for token in tokens]


BASELINE = {
    "detections": [
        _detection("a.json", "json", "generic"),
        _detection("b.ini", "ini", None),
        _detection("c.xml", "xml", "app-config"),
    ],
    "profile_summary": _summary(("app", ["x", "y"]), ("gone", ["z"])),
    "hunt_hits": _hits("server_name", "server_name", None, "", "version"),
}
CURRENT = {
    "detections": [
        _detection("a.json", "json", "generic"),
        _detection("b.ini", "ini", None, confidence=0.8),
        _detection("d.yaml", "yaml", None),
        {"path": "/abs/e.txt", "detection": {"format": "text", "variant": "plain"}},
    ],
    "profile_summary": _summary(("app", ["y", "w"]), ("new", ["n"]), ("same", ["s"])),
    "hunt_hits": _hits("server_name", "certificate_thumbprint", None),
}


def _compare_case(name: str, baseline: object, current: object, **extra: object) -> dict[str, object]:
    tree: dict[str, object] = {}
    for key, value in (("base.json", baseline), ("current.json", current)):
        if value is None:
            continue
        tree[key] = value if isinstance(value, str) or (isinstance(value, dict) and "b64" in value) else json.dumps(value)
    case: dict[str, object] = {"name": name, "tree": tree, "options": {"baseline": "{dir}/base.json", "current": "{dir}/current.json"}}
    case.update(extra)
    return case


COMPARE_CASES = [
    _compare_case("added-removed-changed", BASELINE, CURRENT),
    _compare_case("identical", BASELINE, BASELINE),
    _compare_case(
        "missing-summaries", {"detections": [], "profile_summary": None}, {"detections": [], "profile_summary": {"profiles": []}}
    ),
    _compare_case("current-missing", BASELINE, None),
    _compare_case("baseline-missing", None, CURRENT),
    _compare_case("baseline-invalid-json", "{broken", CURRENT, divergence="json-decode-text"),
    _compare_case("current-not-utf8", BASELINE, {"b64": base64.b64encode(b"\xff{}").decode()}),
    _compare_case("baseline-directory", BASELINE, CURRENT, options={"baseline": "{dir}/basedir", "current": "{dir}/current.json"},
                  tree_extra={"basedir/keep.txt": "x"}),
    _compare_case("baseline-not-a-mapping", [1, 2], CURRENT),
    _compare_case("detection-not-a-mapping", {"detections": [{"path": "p", "detection": ["x"]}]}, CURRENT),
    _compare_case("unhashable-key", {"detections": [{"relative_path": ["a"], "detection": {}}]}, CURRENT),
    _compare_case(
        "numeric-key-equivalence",
        {"detections": [{"relative_path": 1, "detection": {"format": 1.0}}, {"relative_path": True, "detection": {"format": 1, "x": 2}}]},
        {"detections": [{"relative_path": 1.0, "detection": {"format": True}}, {"relative_path": 2, "detection": {"format": None}}]},
    ),
    _compare_case(
        "repr-escapes",
        {"detections": []},
        {"detections": [{"relative_path": "it's \"q\" \u2028\U0001f600\x07", "detection": {"format": "t\u00e9", "variant": 1e16}}]},
    ),
    _compare_case(
        "profile-name-not-str",
        {"detections": [], "profile_summary": _summary(("a", ["x"]))},
        {"detections": [], "profile_summary": _summary((5, ["x"]), ("a", ["x"]))},
    ),
    _compare_case(
        "token-types-unsortable",
        {"detections": [], "hunt_hits": []},
        {"detections": [], "hunt_hits": [*_hits("a"), *_hits(3)]},
    ),
    _compare_case(
        "numeric-tokens",
        {"detections": [], "hunt_hits": [*_hits(2, 2.0, True), {"rule": {}}, {}]},
        {"detections": [], "hunt_hits": [*_hits(1, 2.5)]},
    ),
    _compare_case("hits-not-mappings", {"detections": [], "hunt_hits": "ab"}, CURRENT),
    _compare_case(
        "nan-signature",
        {"detections": [_detection("a", "json", None, confidence=float("nan"))]},
        {"detections": [_detection("a", "json", None, confidence=float("nan"))]},
    ),
    _compare_case(
        "summary-totals-and-counts",
        {"detections": [], "profile_summary": {"total_profiles": 0, "profiles": [{"name": "p", "config_ids": ["a"], "config_count": "3"}]}},
        {"detections": [], "profile_summary": {"profiles": [{"name": "p", "config_ids": ["a"]}, {"name": "", "config_ids": []}]}},
    ),
]


def compare_cases() -> list[dict[str, object]]:
    cases = []
    for case in COMPARE_CASES:
        tree = dict(case["tree"])  # type: ignore[arg-type]
        tree.update(case.pop("tree_extra", {}))  # type: ignore[arg-type]
        case["tree"] = tree
        options = case["options"]

        def build(directory: str, options: dict[str, object] = options) -> argparse.Namespace:  # type: ignore[assignment]
            return argparse.Namespace(**{key: _substitute(value, directory) for key, value in options.items()})

        entry = dict(case)
        entry["result"] = _execute(case, capture.compare_snapshots, build)
        cases.append(entry)
    return cases


REGISTRY_CONTENTS = [
    REGISTRY_A,
    REGISTRY_B,
    "{}",
    '{"roots": [], "requested_roots": [], "hits": []}',
    '{"roots": 0, "requested_roots": false, "hits": 0, "token": 0}',
    '{"roots": 5}',
    '{"requested_roots": true}',
    '{"hits": true}',
    '{"hits": {"a": 1, "b": 2}}',
    r'{"hits": "\ud83d\ude00x"}',
    '{"roots": {"hive": "HKLM"}}',
    '{"roots": [{"hive": "HKLM", "path": "P", "view": "64"}, {"hive": null, "path": "P"}, {"hive": "H", "path": "P", "view": null}]}',
    '{"roots": [{"hive": "\\u3000HKLM\\u3000", "path": "P\\u2028", "view": [1]}, {"hive": true, "path": 1e16, "view": {"a": 1}}]}',
    '{"roots": [{"hive": "HKLM", "path": "P", "view": ""}, {"hive": "HKLM", "path": "P", "view": false},'
    ' {"hive": "HKLM", "path": "P", "view": 1.0}]}',
    "[]",
    '"text"',
    "null",
    "{bad",
    "\ufeff{}",
]


def registry_summary_cases() -> list[dict[str, object]]:
    cases = []
    for content in REGISTRY_CONTENTS:
        with tempfile.TemporaryDirectory(prefix="capture-oracle-") as temp:
            base = Path(temp).resolve()
            path = base / "registry_scan.json"
            path.write_text(content, encoding="utf-8")
            entry: dict[str, object] = {"content": content}
            try:
                summary = capture._summarise_registry_scan(path)
                entry["result"] = json.loads(json.dumps(summary).replace(str(base), "{dir}"))
            except Exception as exc:
                entry["error"] = {"type": type(exc).__name__, "message": str(exc).replace(str(base), "{dir}")}
            cases.append(entry)
    return cases


MANIFEST_DURATIONS = [
    (0.0, 0.0, 0.0),
    (0.0005, 1.0005, 2.675),
    (0.0015, 0.0025, 1e-9),
    (123456.78951, 0.1235, 1.2344999999999999),
    (-0.0004, -1.2345, -2.5),
    (float("nan"), float("inf"), float("-inf")),
    (1e20, 2.0**53, 5e-324),
]


def manifest_cases() -> list[dict[str, object]]:
    cases = []
    for detection, hunt, total in MANIFEST_DURATIONS:
        capture_block = {
            "id": "capture",
            "captured_at": "2025-03-12T00:00:00Z",
            "root": "/root",
            "operator": "tester",
            "environment": "lab",
            "reason": "validation",
            "host": "test-host",
        }
        manifest = capture._build_manifest_payload(
            capture=capture_block,
            snapshot_path=Path("/out/sub/capture-snapshot.json"),
            manifest_path=Path("/out/sub/capture-manifest.json"),
            detection_duration=detection,
            hunt_duration=hunt,
            total_duration=total,
            detection_count=3,
            profile_match_count=2,
            hunt_count=1,
            profile_summary={"total_profiles": 4, "total_configs": 9, "profiles": []} if detection else None,
            placeholder="[X]",
            mask_token_count=2,
            total_redactions=5,
            registry_scans=[{"file": "a.json", "token": None}] if hunt else None,
        )
        cases.append(
            {
                "durations": [repr(detection), repr(hunt), repr(total)],
                "with_summary": bool(detection),
                "with_scans": bool(hunt),
                "dump": json.dumps(manifest, indent=2, sort_keys=True),
            }
        )
    return cases


def main() -> None:
    payload = {
        "run": run_cases(),
        "export_sql": export_sql_cases(),
        "compare": compare_cases(),
        "registry_summaries": registry_summary_cases(),
        "manifest": manifest_cases(),
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(payload, ensure_ascii=True, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
