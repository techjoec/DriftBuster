"""Generate the CPython oracle data the C# registry tests compare against.

Temporary; deleted together with the Python package. Re-run after editing a case table:
    python tools/parity/gen_registry_cases.py

Writes gui/DriftBuster.Backend.Tests/Registry/Data/registry_cases.json with ``ensure_ascii=True``. A ``bytes`` value is written as
``{"$bytes": "<hex>"}``. Registry trees are fake backends: each key is ``{"hive", "path", "view", "subkeys", "values"}`` where a
``view`` of ``"*"`` answers every view, and a lookup that finds no key lists nothing. Errors are ``{"type", "message"}``.
"""

from __future__ import annotations

import contextlib
import dataclasses
import io
import json
import re
import tempfile
from pathlib import Path

import driftbuster.registry as registry_package
import driftbuster.registry_cli as registry_cli
from driftbuster import offline_runner
from driftbuster.offline_runner import OfflineRegistryScanSource, RemoteRegistryTarget
from driftbuster.registry import _format_timestamp, parse_registry_root_descriptor, scan
from driftbuster.registry.scan import RegistryApp, RegistryHit, SearchSpec

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[1] / "gui" / "DriftBuster.Backend.Tests" / "Registry" / "Data" / "registry_cases.json"

UNINSTALL = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
UNINSTALL_WOW = r"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall"


def encode(value):
    if isinstance(value, bytes):
        return {"$bytes": value.hex()}
    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        return {field.name: encode(getattr(value, field.name)) for field in dataclasses.fields(value)}
    if isinstance(value, (list, tuple)):
        return [encode(item) for item in value]
    if isinstance(value, dict):
        return {key: encode(item) for key, item in value.items()}
    return value


def decode(value):
    if isinstance(value, dict):
        if set(value) == {"$bytes"}:
            return bytes.fromhex(value["$bytes"])
        return {key: decode(item) for key, item in value.items()}
    if isinstance(value, list):
        return [decode(item) for item in value]
    return value


def outcome(func):
    try:
        return {"result": encode(func())}
    except BaseException as exc:  # the oracle records every failure, SystemExit included
        return {"error": {"type": type(exc).__name__, "message": str(exc)}}


class FakeBackend(scan._Backend):
    def __init__(self, keys) -> None:
        self.keys = keys

    def _find(self, hive, path, view):
        for key in self.keys:
            if key["hive"] == hive and key["path"] == path and key.get("view", "*") in {"*", view}:
                return key
        return None

    def enum_subkeys(self, hive, path, view):
        key = self._find(hive, path, view)
        return list(key.get("subkeys", [])) if key else []

    def enum_values(self, hive, path, view):
        key = self._find(hive, path, view)
        return [(name, decode(data)) for name, data in key.get("values", [])] if key else []


ROOT_DESCRIPTORS = [
    "", "   ", ",", " , ,", "HKLM\\Software\\Vendor", "hklm/Software/Vendor", "HKCU\\Software\\Tool,view=auto",
    "HKLM\\Software\\V,view=32,view=64", "HKLM\\Software\\V, VIEW = Auto ", "HKCR\\Software", "HKLM\\", "HKLM",
    "HKLM\\  x  ,view=64", "HKLM\\Software,view", "HKLM\\Software,mode=32", "HKLM\\Software,view=", "HKLM\\Software,view=16",
    "HKLM\\Software,,view=32,", "HK\u212aLM\\x", "hk\u212alm\\x", "HKLM\\a\nb", "HKLM\\Software,view=a=b",
    "HKLM\\Soft ware\\Key,view= 64", "HKLM\\Software,view=64,view=auto", "HKCU\\\u00dfoftware\\\U0001f600,view=32",
    "\u2003HKLM\\x\u2003", "HKLM\\x,view=\uff13\uff12", "HKLM\\x,Vi\u0130ew=32",
]

REMOTE_TARGET_ARGS = [
    "", " , ", "host=x", "srv,port=5986", "srv, Port = 0x1 ", "srv,port=abc", "srv,use-ssl=YES", "srv,use_ssl=maybe",
    "srv,USER=bob", "srv,user =", "srv,password-env=ENV,credential-profile=prof,transport=WinRM,alias=a", "srv,unknown=1",
    "srv,novalue", "srv,port=1_000", "srv,,alias=b,", "srv,port= -5", "srv,use-ssl=0,use-ssl=on", "srv,alias=a=b",
    "srv,Credential_Profile=p", "srv,port=\uff11\uff12",
]

REMOTE_TARGETS = [
    "  h  ", "", 5, None, {"hostname": "h2"}, {"host": ""}, {"host": "  "}, {"host": "h", "password": None},
    {"host": "h", "password_env": ""}, {"host": "h", "password-env": " E "}, {"host": "h", "password_env": None, "password-env": "X"},
    {"host": "h", "user": "u", "username": ""}, {"host": "h", "transport": " SMB "}, {"host": "h", "transport": ""},
    {"host": "h", "transport": None}, {"host": "h", "port": "0"}, {"host": "h", "port": "12"}, {"host": "h", "port": 1.9},
    {"host": "h", "port": None}, {"host": "h", "port": "x"}, {"host": "h", "use-ssl": "Off"}, {"host": "h", "use_ssl": 1},
    {"host": "h", "use_ssl": "maybe"}, {"host": "h", "use_ssl": None}, {"host": "h", "alias": 0},
    {"host": 7, "alias": " a ", "credential-profile": " "}, {"host": "h", "port": [1]}, {"host": "", "hostname": "h3"},
    {"host": "h", "use_ssl": 2.0}, {"host": "h", "password_env": 0}, [], {"host": "h", "port": True},
]

SCAN_SOURCES = [
    {}, {"registry_scan": []}, {"registry_scan": {}}, {"registry_scan": {"token": "  "}},
    {"registry_scan": {"token": " T ", "keywords": "a, b;;c  d", "patterns": ["x", " ", 3, None]}, "alias": "  "},
    {"registry_scan": {"token": "T", "keywords": {"a": 1}, "patterns": 5}, "alias": 0},
    {"registry_scan": {"token": "T", "remote_batch": [], "batch": "b1"}},
    {"registry_scan": {"token": "T", "remote_batch": "", "remote_targets": ["r1", {"host": "r2"}]}},
    {"registry_scan": {"token": "T", "remoteTargets": 0}},
    {"registry_scan": {"token": "T", "batch": ""}},
    {"registry_scan": {"token": "T", "batch": 5}},
    {"registry_scan": {"token": "T", "remote": ""}},
    {"registry_scan": {"token": "T", "remote": {"host": "g", "port": 5986}, "remote_batch": {"host": "m"}}},
    {"registry_scan": {"token": "T", "max_depth": "3", "max_hits": 2.7, "time_budget_s": "1.5"}},
    {"registry_scan": {"token": "T", "max_depth": None}},
    {"registry_scan": {"token": "T", "max_depth": 123456789012345678901234567890}},
    {"registry_scan": {"token": "T", "roots": "hkcu\\Software\\X,view=32"}},
    {"registry_scan": {"token": "T", "roots": {"hive": "hklm", "path": " P ", "view": "Auto"}}},
    {"registry_scan": {"token": "T", "roots": [{"hive": None, "path": "P"}, {"hive": "x", "path": "P", "view": 64}]}},
    {"registry_scan": {"token": "T", "roots": [{"hive": "HKLM"}]}},
    {"registry_scan": {"token": "T", "roots": [{"hive": "HKLM", "path": "p", "view": "16"}]}},
    {"registry_scan": {"token": "T", "roots": [5]}},
    {"registry_scan": {"token": "T", "roots": True}},
    {"registry_scan": {"token": "T", "roots": []}},
    {"registry_scan": {"token": "T", "roots": ["HKLM\\A", "bad"]}},
    {"registry_scan": {"token": "T", "roots": [{"hive": "HKLM", "path": "p", "view": " "}]}, "alias": "my alias!"},
    {"registry_scan": {"token": 12}},
    {"registry_scan": {"token": "Vendor \u00c9t\u00e9/App"}, "alias": True},
]

VALUE_TEXTS = [
    "text", "", {"$bytes": "616263"}, {"$bytes": "ff"}, {"$bytes": "eda080"}, {"$bytes": "e282"}, {"$bytes": "61f0808080"},
    {"$bytes": "f4908080"}, {"$bytes": "c0af"}, {"$bytes": "e080af"}, {"$bytes": "f09f98"}, {"$bytes": ""}, 42, 0, 4294967295,
    18446744073709551615, True, False, 1.5, 1e22, 1e-7, ["a", "b"], [], ["a", {"$bytes": "7827"}], [1, None, 2.5, True],
    [["x"], {"k": "v"}], None, {"a": 1}, "x" * 130, "\U0001f600" * 125, "A\u0130\u03a3",
]

MATCH_CASES = [
    {"name": "Server", "value": "api.internal.local", "keywords": ["server", "api"], "patterns": []},
    {"name": "Server", "value": "something", "keywords": ["SERVER"], "patterns": []},
    {"name": "Endpoint", "value": "https://svc", "keywords": [], "patterns": [r"https://"]},
    {"name": "https_name", "value": "plain", "keywords": [], "patterns": [r"https"]},
    {"name": "N", "value": "v", "keywords": ["n v"], "patterns": []},
    {"name": "N", "value": "v", "keywords": ["missing"], "patterns": []},
    {"name": "N", "value": "v", "keywords": [], "patterns": ["zzz", "v$"]},
    {"name": "Stra\u00dfe", "value": "x", "keywords": ["STRASSE"], "patterns": []},
    {"name": "K", "value": "\u0130stanbul", "keywords": ["i\u0307stanbul"], "patterns": []},
    {"name": "K", "value": 1234, "keywords": ["23"], "patterns": [r"^\d+$"]},
    {"name": "K", "value": {"$bytes": "414243"}, "keywords": ["abc"], "patterns": []},
    {"name": "K", "value": ["One", "Two"], "keywords": ["one, two"], "patterns": []},
    {"name": "K", "value": "abc", "keywords": [], "patterns": ["(?i)ABC"]},
]

VENDOR_PAIRS = ["VendorA AppA", "a_b-c  d", "Single", "", " lead", "x\u2003y", "__", "Contoso Tool Suite 2024", "trail- "]

INSTALLED = [
    {"display_name": "VendorA AppA", "key_path": UNINSTALL + r"\AppA", "hive": "HKLM", "publisher": "VendorA", "view": "64"},
    {"display_name": "VendorB AppB", "key_path": UNINSTALL_WOW + r"\AppB", "hive": "HKLM", "publisher": "VendorB", "view": "32"},
    {"display_name": "TinyTool", "key_path": UNINSTALL + r"\UserApp", "hive": "HKCU", "view": "auto"},
    {"display_name": "Single", "key_path": UNINSTALL + r"\S", "hive": "HKLM", "publisher": "Acme Corp", "view": "auto"},
]

FIND_ROOTS = [
    {"token": "AppA", "installed": INSTALLED},
    {"token": "  vendor  ", "installed": INSTALLED},
    {"token": "acme", "installed": INSTALLED},
    {"token": "Acme Product", "installed": []},
    {"token": "   ", "installed": INSTALLED},
    {"token": "", "installed": INSTALLED},
    {"token": "tiny", "installed": INSTALLED + INSTALLED},
]

APP_TREE = [
    {"hive": "HKLM", "path": UNINSTALL, "view": "64", "subkeys": ["b", "a", "dup", "dup", "blank", "binary", "nopub"]},
    {"hive": "HKLM", "path": UNINSTALL + r"\a", "view": "64", "values": [["DisplayName", " zeta "], ["Publisher", "P"],
     ["DisplayVersion", "1.0"], ["UninstallString", "u.exe"], ["InstallLocation", "C:\\z"]]},
    {"hive": "HKLM", "path": UNINSTALL + r"\b", "view": "64", "values": [["DisplayName", "Alpha"], ["DisplayName", "alpha"]]},
    {"hive": "HKLM", "path": UNINSTALL + r"\dup", "view": "64", "values": [["DisplayName", "Dup"]]},
    {"hive": "HKLM", "path": UNINSTALL + r"\blank", "view": "64", "values": [["DisplayName", "   "]]},
    {"hive": "HKLM", "path": UNINSTALL + r"\binary", "view": "64", "values": [["DisplayName", {"$bytes": "6869"}], ["Publisher", 0],
     ["DisplayVersion", 7], ["InstallLocation", ["x", "y"]]]},
    {"hive": "HKLM", "path": UNINSTALL + r"\nopub", "view": "64", "values": [["DisplayName", "\u00c9clair"], ["Publisher", ""]]},
    {"hive": "HKLM", "path": UNINSTALL_WOW, "view": "32", "subkeys": ["w"]},
    {"hive": "HKLM", "path": UNINSTALL_WOW + r"\w", "view": "32", "values": [["DisplayName", "alpha"]]},
    {"hive": "HKCU", "path": UNINSTALL, "view": "*", "subkeys": ["c"]},
    {"hive": "HKCU", "path": UNINSTALL + r"\c", "view": "*", "values": [["DisplayName", "Alpha"]]},
]

SEARCH_TREE = [
    {"hive": "HKLM", "path": "Software\\V", "view": "*", "subkeys": ["B", "A"], "values": [["Root", "hit-root"]]},
    {"hive": "HKLM", "path": "Software\\V\\A", "view": "*", "subkeys": ["Deep"], "values": [["A1", "hit-a1"], ["A2", "miss"]]},
    {"hive": "HKLM", "path": "Software\\V\\B", "view": "*", "values": [["B1", "hit-b1"], ["B2", {"$bytes": "6869742d6279746573"}]]},
    {"hive": "HKLM", "path": "Software\\V\\A\\Deep", "view": "*", "subkeys": ["Deeper"], "values": [["D1", "hit-deep"]]},
    {"hive": "HKLM", "path": "Software\\V\\A\\Deep\\Deeper", "view": "*", "values": [["D2", "hit-deeper"]]},
    {"hive": "HKCU", "path": "Software\\V", "view": "32", "values": [["U", "hit-user32"]]},
]

SEARCHES = [
    {"roots": [["HKLM", "Software\\V", None]], "keywords": ["hit"], "patterns": [], "max_depth": 12, "max_hits": 200},
    {"roots": [["HKLM", "Software\\V", None]], "keywords": ["hit"], "patterns": [], "max_depth": 1, "max_hits": 200},
    {"roots": [["HKLM", "Software\\V", None]], "keywords": ["hit"], "patterns": [], "max_depth": -3, "max_hits": 200},
    {"roots": [["HKLM", "Software\\V", None]], "keywords": [], "patterns": ["hit"], "max_depth": 12, "max_hits": 3},
    {"roots": [["HKLM", "Software\\V", None]], "keywords": [], "patterns": ["hit"], "max_depth": 12, "max_hits": 0},
    {"roots": [["HKLM", "Software\\V", None], ["HKLM", "Software\\V", None], ["HKLM", "Software\\V\\A", None]],
     "keywords": [], "patterns": [], "max_depth": 12, "max_hits": 200},
    {"roots": [["HKLM", "Software\\V\\A", "64"], ["HKLM", "Software\\V\\A", None]], "keywords": [], "patterns": [], "max_depth": 0,
     "max_hits": 200},
    {"roots": [["HKCU", "Software\\V", "32"], ["HKCU", "Software\\V", "64"], ["HKX", "Nope", None]], "keywords": [], "patterns": [],
     "max_depth": 5, "max_hits": 200},
    {"roots": [], "keywords": [], "patterns": [], "max_depth": 5, "max_hits": 200},
]

TIMESTAMPS = [0.0, 1.5, -0.5, 1000000000.0000005, 1710000000.123456789, 1757894400.0000025, 1757894400.9999996, 1e-7, 0.0000005,
              0.0000015, -0.0000005, 253402300799.0, 1757894400.25]

EMIT_CONFIG = [
    ["emit-config", "VendorA", "--keyword", "server", "--root", "HKLM\\Software\\VendorA,view=64"],
    ["emit-config", "VendorA"],
    ["emit-config", "V\u00e9ndor", "--alias", "Alias\u2603", "--keyword", "", "--pattern", r"api\.x", "--pattern", "", "--max-depth", "3",
     "--max-hits", "5", "--time-budget", "2.5", "--root", "hkcu/Software/X", "--root", "HKLM\\Y,view=auto",
     "--remote-target", "gw,port=5986,use-ssl=true", "--remote-target", "b1,user=svc", "--remote-target", "b2"],
    ["emit-config", "T", "--alias", "", "--time-budget", "inf", "--remote-target", "gw"],
    ["emit-config", "T", "--root", "HKCR\\X"],
    ["emit-config", "T", "--remote-target", "k=v"],
]

FAKE_APPS = [
    RegistryApp(display_name="VendorA AppA", key_path=UNINSTALL + r"\AppA", hive="HKLM", version="1.2.3", view="64"),
    RegistryApp(display_name="TinyTool", key_path=UNINSTALL + r"\UserApp", hive="HKCU", version="", view="auto"),
]

FAKE_ROOTS = (("HKLM", r"Software\VendorA\AppA", "64"), ("HKCU", r"Software\TinyTool", None), ("HKLM", "Software\\X", "auto"))

FAKE_HITS = (
    RegistryHit(path=r"Software\VendorA\AppA", hive="HKLM", value_name="Server", data_preview="api.internal.local", reason="r"),
    RegistryHit(path="Software\\\u00c9", hive="HKCU", value_name="", data_preview="", reason="r"),
)

PATTERN_ERRORS = ["(", "ab\n(c", "a\nb\n[x", "\U0001f600(", "x{2,1}", "\n)", "\U0001f600\n\U0001f600)", "(?P<1>x)", "a)", "\\"]

SEARCH_COMMANDS = [
    ["search", "VendorA", "--keyword", "server", "--pattern", r"api\.internal", "--max-depth", "3", "--max-hits", "5"],
    ["search", "VendorA", "--root", "HKLM\\Software\\Explicit,view=32", "--root", "HKCU\\Other"],
    ["search", "VendorA", "--root", "nope"],
    ["search", "VendorA", "--root", ","],
    ["search", "VendorA", "--pattern", "("],
]


def root_descriptor_cases():
    return [
        {"input": text, **outcome(lambda text=text: list(parse_registry_root_descriptor(text).as_tuple()))} for text in ROOT_DESCRIPTORS
    ]


def remote_target_arg_cases():
    return [{"input": text, **outcome(lambda text=text: registry_cli._parse_remote_target_arg(text))} for text in REMOTE_TARGET_ARGS]


def remote_target_cases():
    return [{"input": payload, **outcome(lambda payload=payload: RemoteRegistryTarget.from_payload(payload))} for payload in REMOTE_TARGETS]


def scan_source_cases():
    cases = []
    for payload in SCAN_SOURCES:
        case = {"input": payload, **outcome(lambda payload=payload: OfflineRegistryScanSource.from_dict(payload))}
        if "result" in case:
            source = OfflineRegistryScanSource.from_dict(payload)
            case["destination_name"] = source.destination_name(fallback_index=3)
        cases.append(case)
    return cases


def search_hits(backend, roots, spec):
    return [dataclasses.asdict(hit) for hit in scan.search_registry(roots, spec, backend=backend)]


def value_text_cases():
    cases = []
    for value in VALUE_TEXTS:
        backend = FakeBackend([{"hive": "HKLM", "path": "K", "view": "*", "values": [["Name", value]]}])
        hits = search_hits(backend, (("HKLM", "K", None),), SearchSpec())
        cases.append({"value": value, "preview": hits[0]["data_preview"] if hits else None})
    return cases


def match_cases():
    cases = []
    for case in MATCH_CASES:
        backend = FakeBackend([{"hive": "HKLM", "path": "K", "view": "*", "values": [[case["name"], case["value"]]]}])
        spec = SearchSpec(keywords=tuple(case["keywords"]), patterns=tuple(re.compile(p) for p in case["patterns"]))
        hits = search_hits(backend, (("HKLM", "K", None),), spec)
        cases.append({**case, "preview": hits[0]["data_preview"] if hits else None})
    return cases


def installed_apps(entries):
    return tuple(RegistryApp(**entry) for entry in entries)


def search_cases():
    backend = FakeBackend(SEARCH_TREE)
    cases = []
    for case in SEARCHES:
        spec = SearchSpec(
            keywords=tuple(case["keywords"]),
            patterns=tuple(re.compile(p) for p in case["patterns"]),
            max_depth=case["max_depth"],
            max_hits=case["max_hits"],
        )
        roots = tuple(tuple(root) for root in case["roots"])
        cases.append({**case, **outcome(lambda roots=roots, spec=spec: search_hits(backend, roots, spec))})
    return cases


@contextlib.contextmanager
def patched(target, name, value):
    original = getattr(target, name)
    setattr(target, name, value)
    try:
        yield
    finally:
        setattr(target, name, original)


def run_cli(argv, apps=FAKE_APPS, roots=FAKE_ROOTS, hits=FAKE_HITS):
    calls = []
    stdout = io.StringIO()
    with contextlib.ExitStack() as stack:
        stack.enter_context(patched(registry_cli, "is_windows", lambda: True))
        stack.enter_context(patched(registry_cli, "enumerate_installed_apps", lambda: (calls.append(["enumerate"]), apps)[1]))
        stack.enter_context(patched(registry_cli, "find_app_registry_roots",
                                    lambda token, installed: (calls.append(["find", token]), roots)[1]))

        def fake_search(search_roots, spec):
            calls.append(["search", [list(root) for root in search_roots], list(spec.keywords), [p.pattern for p in spec.patterns],
                          spec.max_depth, spec.max_hits, spec.time_budget_s])
            return hits

        stack.enter_context(patched(registry_cli, "search_registry", fake_search))
        stack.enter_context(contextlib.redirect_stdout(stdout))
        result = outcome(lambda: registry_cli.main(argv))
    return {"argv": argv, **result, "stdout": stdout.getvalue(), "calls": calls}


def collector_cases():
    cases = []
    sources = [
        {"registry_scan": {"token": "VendorA", "keywords": ["server"], "patterns": [r"api\."],
                           "roots": [{"hive": "HKLM", "path": r"Software\VendorA", "view": "64"}, "HKCU\\Software\\\u00c9"]}},
        {"registry_scan": {"token": "Vendor\u00c9", "max_depth": 2, "max_hits": 9, "time_budget_s": 0.5}, "alias": "al"},
    ]
    for payload in sources:
        calls = []

        def fake_enumerate(calls=calls):
            calls.append(["enumerate"])
            return FAKE_APPS

        def fake_find(token, installed, calls=calls):
            calls.append(["find", token, len(installed)])
            return FAKE_ROOTS

        with tempfile.TemporaryDirectory() as tmp, contextlib.ExitStack() as stack:
            stack.enter_context(patched(registry_package, "is_windows", lambda: True))
            stack.enter_context(patched(registry_package, "enumerate_installed_apps", fake_enumerate))
            stack.enter_context(patched(registry_package, "find_app_registry_roots", fake_find))

            def fake_search(roots, spec, calls=calls):
                calls.append(["search", [list(root) for root in roots], list(spec.keywords), [p.pattern for p in spec.patterns],
                              spec.max_depth, spec.max_hits, spec.time_budget_s])
                return FAKE_HITS

            stack.enter_context(patched(registry_package, "search_registry", fake_search))
            config = offline_runner.OfflineRunnerConfig.from_dict({
                "schema": offline_runner.CONFIG_SCHEMA,
                "profile": {"name": "registry-only", "sources": [payload], "options": {}, "secret_scanner": {}},
                "runner": {"output_directory": str(Path(tmp) / "out"), "compress": False, "cleanup_staging": False},
                "metadata": {},
            })
            result = offline_runner.execute_config(config, base_dir=Path(tmp), timestamp="20250312T010101Z")
            manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
            summary = manifest["sources"][0]
            alias = config.profile.sources[0].destination_name(fallback_index=0)
            data_path = Path(result.staging_dir) / config.settings.data_directory_name / alias / "registry_scan.json"
            summary["output"] = Path(summary["output"]).relative_to(data_path.parent).as_posix()
            file_entry = next(entry for entry in manifest["files"] if entry["source"].startswith("registry:"))
            cases.append({
                "input": payload,
                "calls": calls,
                "summary": summary,
                "payload_text": data_path.read_bytes().decode("utf-8"),
                "size": file_entry.get("size"),
                "sha256": file_entry.get("sha256"),
            })
    return cases


def main() -> None:
    cases = {
        "root_descriptors": root_descriptor_cases(),
        "remote_target_args": remote_target_arg_cases(),
        "remote_targets": remote_target_cases(),
        "scan_sources": scan_source_cases(),
        "value_texts": value_text_cases(),
        "matches": match_cases(),
        "vendor_pairs": [
            {"input": name, "result": [list(pair) for pair in scan._candidate_vendor_app_pairs(name)]} for name in VENDOR_PAIRS
        ],
        "find_roots": [{**case, "result": [list(root) for root in scan.find_app_registry_roots(case["token"],
                                                                                           installed=installed_apps(case["installed"]))]}
                       for case in FIND_ROOTS],
        "enumerate_apps": {"tree": APP_TREE, "result": encode(scan.enumerate_installed_apps(backend=FakeBackend(APP_TREE)))},
        "search": {"tree": SEARCH_TREE, "cases": search_cases()},
        "pattern_errors": [
            {"input": pattern, **outcome(lambda pattern=pattern: re.compile(pattern).pattern)} for pattern in PATTERN_ERRORS
        ],
        "timestamps": [{"input": value, "result": _format_timestamp(value)} for value in TIMESTAMPS],
        "cli": {
            "apps": encode(FAKE_APPS),
            "roots": [list(root) for root in FAKE_ROOTS],
            "hits": encode(FAKE_HITS),
            "runs": [run_cli(["list-apps"]), run_cli(["suggest-roots", "AppA"])]
            + [run_cli(argv) for argv in SEARCH_COMMANDS]
            + [run_cli(argv) for argv in EMIT_CONFIG],
        },
        "collector": collector_cases(),
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(cases, indent=1, ensure_ascii=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
