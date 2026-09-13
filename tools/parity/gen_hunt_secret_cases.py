"""Generate the CPython oracle data the C# hunt and secret-scanning parity tests compare against.

Temporary; deleted together with the Python package. Re-run after editing a case table:
    python tools/parity/gen_hunt_secret_cases.py

Writes gui/DriftBuster.Backend.Tests/Hunt/Data/hunt_cases.json and
gui/DriftBuster.Backend.Tests/Secrets/Data/secret_cases.json. File contents are base64 so every byte survives; strings are
written with ``ensure_ascii=True``. Paths under the temporary tree are written with the tree root replaced by ``<root>``.

Hunt runs use ``default_rules()`` with fix e applied (the install-path drive pattern single-escaped, as the port ships it),
so the only expected difference between the two sides is none.
"""

from __future__ import annotations

import base64
import json
import random
import re
import tempfile
from pathlib import Path

from driftbuster import hunt, secret_scanning

HERE = Path(__file__).resolve().parent
TESTS = HERE.parents[1] / "gui" / "DriftBuster.Backend.Tests"

FIXED_INSTALL_PATTERN = r"[A-Za-z]:\\[\w\-\.\s]+"


def _default_rules_with_fix_e() -> tuple[hunt.HuntRule, ...]:
    rules = []
    for rule in hunt.default_rules():
        if rule.name == "install-path":
            patterns = (FIXED_INSTALL_PATTERN, *(p.pattern for p in rule.compiled_patterns[1:]))
            rule = hunt.HuntRule(
                name=rule.name,
                description=rule.description,
                token_name=rule.token_name,
                keywords=rule.keywords,
                patterns=patterns,
            )
        rules.append(rule)
    return tuple(rules)


def _b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


BIG_PREFIX = b"server host: early.corp.local\n" + (b"x" * 80 + b"\n") * 1700
TREE: dict[str, bytes] = {
    "app.config": (
        b'<configuration>\n  <connectionStrings>\n    <add name="Primary" connectionString="Server=sql.example.local;Database=App;" />\n'
        b'  </connectionStrings>\n  <appSettings>\n    <add key="ServiceEndpoint" value="https://api.example.com/v1/" />\n'
        b'    <add key="FeatureFlag:NewDashboard" value="true" />\n  </appSettings>\n'
        b'  <client><endpoint address="net.tcp://svc.example.local:9000/Feed" /></client>\n</configuration>\n'
    ),
    "a/sub/y.txt": b"server host: y.lan\nversion 1.2.3 version 1.2.3\n",
    "a-b/x.txt": b"host=x.internal and server",
    "a.txt": b"install path C:\\Program Files\\Vendor\nINSTALL_PATH=/opt/vendor-app.v2/bin\n",
    "B.txt": b"Thumbprint certificate 0123456789ABCDEF0123456789ABCDEF01234567\r\nhost server\x1cserver z.com\x85host q.net",
    "emoji-\U0001f600.txt": ("server host \U0001f600.corp k\u212abe.local x.com\U0001f600 " + "padding " * 40).encode(),
    "unicode-host.txt": ("server host \u00e9t\u00e9.corp h\u00f4te.com _x.net a\u0301.lan \U0001d400.internal\n" + "padding " * 60).encode(),
    "~tilde.txt": b"server host: tilde.corp.local",
    "utf16.txt": "server host=utf16.corp.local\n".encode("utf-16"),
    "latin1.txt": b"server host caf\xe9.local\n",
    "binary.dat": b"\x00\x01server host bin.corp.local\x00" * 10,
    "empty.txt": b"",
    "big.txt": BIG_PREFIX + b"server host: late.corp.local\n",
    "logs/run.log": b"server host: log.corp.local\n",
    "dups.txt": b"server host a.local a.local b.local\n",
    "keys/feature.xml": b'<feature name="x" enabled="true"/>\n<add key="toggleThing" value="on"/>\n',
}

HUNT_RUNS: list[dict[str, object]] = [
    {"name": "tree-default", "root": "", "glob": "**/*", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "tree-exclude", "root": "", "glob": "**/*", "exclude": ["*.log", "sub/*", "a.txt"], "sample_size": 128 * 1024, "rules": "default"},
    {"name": "tree-glob-txt", "root": "", "glob": "*.txt", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "tree-glob-config", "root": "", "glob": "**/*.config", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "tree-small-sample", "root": "", "glob": "**/*", "exclude": None, "sample_size": 64, "rules": "default"},
    {"name": "file-root", "root": "a.txt", "glob": "**/*", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "file-root-excluded", "root": "a.txt", "glob": "**/*", "exclude": ["a.txt"], "sample_size": 128 * 1024, "rules": "default"},
    {"name": "subdir-root", "root": "a", "glob": "**/*", "exclude": ["y.txt"], "sample_size": 128 * 1024, "rules": "default"},
    {"name": "custom-rules", "root": "", "glob": "**/*", "exclude": None, "sample_size": 128 * 1024, "rules": "custom"},
    {"name": "glob-question-mark", "root": "", "glob": "?.txt", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "glob-character-class", "root": "", "glob": "[aB]*", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "glob-nested-wildcard", "root": "", "glob": "*/*.txt", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "glob-trailing-separator", "root": "", "glob": "a/", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "glob-recursive-directories", "root": "", "glob": "**/", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "glob-parent-part", "root": "a", "glob": "../*.txt", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "glob-double-separator", "root": "", "glob": "a//sub/y.txt", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "glob-recursive-twice", "root": "", "glob": "**/../**/*.log", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
    {"name": "sample-size-negative", "root": "", "glob": "big.txt", "exclude": None, "sample_size": -1, "rules": "default"},
    {"name": "missing-root", "root": "missing", "glob": "**/*", "exclude": None, "sample_size": 128 * 1024, "rules": "default"},
]


def _custom_rules() -> tuple[hunt.HuntRule, ...]:
    return (
        hunt.HuntRule(name="groups", description="nested groups", token_name="  grouped  ", patterns=(r"((host)[:=]\s*(\S+))",)),
        hunt.HuntRule(name="blank-token", description="blank token", token_name="   ", keywords=("SERVER",), patterns=(r"server",)),
        hunt.HuntRule(name="no-patterns", description="keywords only", token_name="line", keywords=("\u0130",)),
        hunt.HuntRule(name="empty-match", description="zero width", token_name="empty", keywords=("version",), patterns=(r"x*",)),
        hunt.HuntRule(name="lookahead", description="lookahead groups", token_name="ahead", patterns=(r"(?=(\w+)\.local)(\w)",)),
        hunt.HuntRule(
            name="engine",
            description="sre repeat semantics",
            token_name="engine",
            patterns=(r"(?:\d{1,2}){2}+", r"\N{LATIN SMALL LETTER E}(?:r?)+?v", r"(?i)(s)\1"),
        ),
    )


def hunt_cases() -> list[dict[str, object]]:
    out: list[dict[str, object]] = []
    with tempfile.TemporaryDirectory(prefix="driftbuster-hunt-oracle-") as tmp:
        base = Path(tmp) / "tree"
        for relative, content in TREE.items():
            target = base / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        (base / "i-dotted.txt").write_text("server \u0130stanbul.local\n", encoding="utf-8")
        for run in HUNT_RUNS:
            rules = _default_rules_with_fix_e() if run["rules"] == "default" else _custom_rules()
            root = base / str(run["root"]) if run["root"] else base
            payload = hunt.hunt_path(
                root,
                rules=rules,
                glob=str(run["glob"]),
                sample_size=int(run["sample_size"]),  # type: ignore[arg-type]
                exclude_patterns=run["exclude"],  # type: ignore[arg-type]
                return_json=True,
            )
            for entry in payload:
                entry["path"] = entry["path"].replace(str(base), "<root>", 1)
                entry["rule"]["keywords"] = list(entry["rule"]["keywords"])
                entry["rule"]["patterns"] = list(entry["rule"]["patterns"])
            out.append({**run, "result": payload})
        files = {relative: _b64(content) for relative, content in TREE.items()}
        files["i-dotted.txt"] = _b64("server \u0130stanbul.local\n".encode())
    return [{"files": files, "runs": out}]


SECRET_CASES: list[dict[str, object]] = [
    {"name": "password", "content": b"password = Hunter12345\n", "options": None, "scanner": None},
    {"name": "no-match-crlf", "content": b"nothing here\r\nstill nothing\r\n", "options": None, "scanner": None},
    {"name": "crlf-and-cr", "content": b"a\r\npassword: 'abcdefghij'\rtail\r\n", "options": None, "scanner": None},
    {"name": "several-on-a-line", "content": b"api_key=ABCDEFGHIJKLMNOPQRST password=abcdefgh1 AKIAABCDEFGHIJKLMNOP AKIAQQQQQQQQQQQQQQQQ\n", "options": None, "scanner": None},
    {"name": "invalid-utf8", "content": b"\xff\xfe password=\xed\xa0\x80secretvalue12\n", "options": None, "scanner": None},
    {"name": "invalid-utf8-then-secret", "content": b"\xff\xfe\xc3 password=abcdefghij \xe2\x82\n", "options": None, "scanner": None},
    {"name": "bom", "content": b"\xef\xbb\xbfpassword=abcdefghij\nplain\n", "options": None, "scanner": None},
    {"name": "long-line", "content": ("x" * 150 + " password=abcdefghij " + "y" * 150 + "\n").encode(), "options": None, "scanner": None},
    {"name": "exactly-120", "content": ("z" * 104 + "password=abcdefghij").encode(), "options": None, "scanner": None},
    {"name": "astral-prefix", "content": ("\U0001f600" * 130 + "password=abcdefghij\n").encode(), "options": None, "scanner": None},
    {"name": "ignore-rules-string", "content": b"password=abcdefghij token_key: ABCDEFGHIJKLMNOPQR\n", "options": {"secret_ignore_rules": "PasswordAssignment; GenericApiToken"}, "scanner": None},
    {"name": "ignore-rules-scanner", "content": b"password=abcdefghij AKIAABCDEFGHIJKLMNOP\n", "options": {}, "scanner": {"ignore_rules": ["AwsAccessKeyId"]}},
    {"name": "ignore-pattern-original-line", "content": b"password=abcdefghij ALLOW\npassword=abcdefghij\n", "options": {"secret_ignore_patterns": ["ALLOW"]}, "scanner": {"ignore_patterns": "[ ALLOW"}},
    {"name": "inline-ruleset-zero-width", "content": b"abc\nxxd\n", "options": None, "scanner": {"ruleset": {"version": "inline", "rules": [{"name": "Empty", "pattern": "x*"}]}}},
    {"name": "inline-ruleset-flags", "content": b"Hidden HIDDEN hidden\n", "options": None, "scanner": {"ruleset": {"version": 7, "rules": [{"name": "Upper", "pattern": "hidden", "flags": "I", "description": "d"}, {"name": "", "pattern": "x"}, {"name": "Bad", "pattern": "("}]}}},
    {"name": "inline-ruleset-unusable", "content": b"password=abcdefghij\n", "options": None, "scanner": {"ruleset": {"version": "x", "rules": [{"name": "Bad", "pattern": "["}]}}},
    {"name": "binary", "content": b"password=abcdefghij\x00\n", "options": None, "scanner": None},
    {"name": "no-trailing-newline", "content": b"first\npassword=abcdefghij", "options": None, "scanner": None},
    {"name": "unicode-separators", "content": "password=abcdefghij\u2028AKIAABCDEFGHIJKLMNOP\x85x\n".encode(), "options": None, "scanner": None},
    {"name": "cr-at-64k-boundary", "content": b"x" * 65535 + b"\r\npassword=abcdefghij\r" + b"y" * 65520 + b"\r\n", "options": None, "scanner": None},
    {"name": "utf8-split-at-64k", "content": b"a" * 65535 + "\u00e9 password=abcdefghij\n".encode() + b"b" * 65534 + b"\xe2\x82\n\xe2\x82\xac", "options": None, "scanner": None},
    {"name": "invalid-utf8-at-64k", "content": b"c" * 65534 + b"\xf0\x9f\x98 password=abcdefghij\r" + b"d" * 131070 + b"\xed\xa0\x80\r\n", "options": None, "scanner": None},
    {"name": "named-escape-rule", "content": b"pin 0000 here\n", "options": None, "scanner": {"ruleset": {"version": "n", "rules": [{"name": "Pin", "pattern": "\\N{DIGIT ZERO}{4}"}]}}},
    {"name": "possessive-counted-rule", "content": b"\xf4\x90\x80\x801111\t12\r\n34", "options": None, "scanner": {"ruleset": {"version": "p", "rules": [{"name": "Pairs", "pattern": "(?:\\d{1,2}){2}+"}]}}},
    {"name": "lazy-empty-body-rule", "content": b"ab-c\nbxx-\n", "options": None, "scanner": {"ruleset": {"version": "l", "rules": [{"name": "Lazy", "pattern": "b(?:x?)+?-"}]}}},
]

# Fix g: rulesets whose matches overlap, abut, straddle a placeholder or fall inside one without Python looping. The port's
# redaction guard must leave every one of these lines exactly as Python redacts it. The same inputs are written as the
# run_parity.sh cases under tools/parity/cases/secrets-guard (where the looping examples, which Python never finishes, also live).
GUARD_OVERLAP_RULESET: dict[str, object] = {
    "version": "guard-overlap",
    "rules": [
        {"name": "Pw", "pattern": r"pw=\S+"},
        {"name": "Adjacent", "pattern": "ab|bc"},
        {"name": "Digits", "pattern": r"\d{4,}"},
        {"name": "Hex", "pattern": "0x[0-9a-f]+"},
        {"name": "Straddle", "pattern": r"\]x+"},
        {"name": "Tail", "pattern": "c$"},
    ],
}
GUARD_INSIDE_RULESET: dict[str, object] = {
    "version": "guard-inside",
    "rules": [
        {"name": "Pw", "pattern": "pw"},
        {"name": "LookBehind", "pattern": r"(?<=x\[)S"},
        {"name": "LookAhead", "pattern": r"\[SE(?=CRET\]z)"},
        {"name": "ZeroWidth", "pattern": r"(?<=\[S)(?=E)"},
    ],
}
_GUARD_FRAGMENTS = ("pw=", "pw=pw=", "ab", "bc", "abc", "abcbc", "1234", "12345678", "0x", "0xbeef", "]", "]x", "xx", "[SECRET]", "[",
                    " ", "=", "c")


def guard_overlap_lines() -> bytes:
    rng = random.Random(20260913)
    lines = ["".join(rng.choice(_GUARD_FRAGMENTS) for _ in range(rng.randint(3, 9))) for _ in range(24)]
    return ("\n".join(lines) + "\n").encode()


GUARD_INSIDE_LINES = b"xpw\nxpw ypw\n[SECRET]z pw\npwz\nplain\n[pw]z\nx[pw\n"

SECRET_CASES += [
    {"name": "guard-overlap-adjacent-generated", "content": guard_overlap_lines(), "options": None, "scanner": {"ruleset": GUARD_OVERLAP_RULESET}},
    {"name": "guard-inside-placeholder-terminates", "content": GUARD_INSIDE_LINES, "options": None, "scanner": {"ruleset": GUARD_INSIDE_RULESET}},
]

GUARD_PARITY_CASES = TESTS.parents[1] / "tools" / "parity" / "cases" / "secrets-guard"


def write_guard_parity_cases() -> None:
    """tools/parity/cases/secrets-guard/<set>/{ruleset.json,input/*}: the two oracle rulesets plus the looping examples."""

    sets: dict[str, tuple[dict[str, object], dict[str, bytes]]] = {
        "overlap-adjacent": (GUARD_OVERLAP_RULESET, {"generated.txt": guard_overlap_lines()}),
        "inside-placeholder": (GUARD_INSIDE_RULESET, {"terminates.txt": GUARD_INSIDE_LINES}),
        "self-matching": (
            {"version": "loop", "rules": [{"name": "SelfMatching", "pattern": "secret", "flags": "i"}]},
            {"loop.txt": b"token secret\nplain\n", "no-loop.txt": b"nothing here\n"},
        ),
        "long-s": (
            {"version": "loop", "rules": [{"name": "LongS", "pattern": "\u017f+", "flags": "i"}]},
            {"loop.txt": "a\u017f\u017fb\n".encode()},
        ),
    }
    for name, (ruleset, files) in sets.items():
        folder = GUARD_PARITY_CASES / name
        (folder / "input").mkdir(parents=True, exist_ok=True)
        (folder / "ruleset.json").write_text(json.dumps(ruleset, ensure_ascii=True, indent=1) + "\n", encoding="utf-8")
        for file_name, content in files.items():
            (folder / "input" / file_name).write_bytes(content)


OPTION_VALUES: list[object] = [
    None, "", "a, b ; c", " \x1c x,\u2028;y ", ",,;;", ["x", None, " y ", 3, "", "  "], ("t",), {"k": "v"}, 0, 5, [],
]

COMPILE_PAYLOADS: list[object] = [
    None, {}, {"rules": "invalid"}, {"rules": None}, {"rules": []}, [1, 2],
    {"version": "custom", "rules": [{"name": "Valid", "pattern": "secret", "flags": "i"}, {"name": "Broken", "pattern": "["}]},
    {"version": None, "rules": [{"name": "  Spaced  ", "pattern": 123, "description": 0}, "not-a-mapping", {"name": "NoPattern"}]},
    {"rules": [{"name": "Lookbehind", "pattern": "(?<=a+)b"}, {"name": "Ok", "pattern": "b", "flags": ["i"]}]},
    {"version": "names", "rules": [{"name": "Named", "pattern": "\\N{em dash}"}, {"name": "Unknown", "pattern": "\\N{NOT A NAME}"}]},
]


def secret_cases() -> dict[str, object]:
    copies = []
    with tempfile.TemporaryDirectory(prefix="driftbuster-secret-oracle-") as tmp:
        for case in SECRET_CASES:
            source = Path(tmp) / f"{case['name']}.src"
            source.write_bytes(case["content"])  # type: ignore[arg-type]
            destination = Path(tmp) / "out" / f"{case['name']}.txt"
            secret_scanning.reset_secret_rule_cache()
            context = secret_scanning.build_context(case["options"], case["scanner"])  # type: ignore[arg-type]
            logs: list[str] = []
            size, digest = secret_scanning.copy_with_secret_filter(
                source, destination, display_path=f"dir/{case['name']}.txt", context=context, log=logs.append
            )
            copies.append(
                {
                    "name": case["name"],
                    "content": _b64(case["content"]),  # type: ignore[arg-type]
                    "options": case["options"],
                    "scanner": case["scanner"],
                    "context": {
                        "rules": [rule.name for rule in context.rules],
                        "version": context.version,
                        "ignore_rules": sorted(context.ignore_rules),
                        "ignore_pattern_text": list(context.ignore_pattern_text),
                        "ignore_patterns": [pattern.pattern for pattern in context.ignore_patterns],
                        "rules_loaded": context.rules_loaded,
                    },
                    "size": size,
                    "sha256": digest,
                    "output": _b64(destination.read_bytes()),
                    "findings": [[f.path, f.rule, f.line, f.snippet] for f in context.findings],
                    "logs": logs,
                }
            )
    option_values = [{"value": value, "result": list(secret_scanning.secret_option_values(value))} for value in OPTION_VALUES]
    compiled = []
    for payload in COMPILE_PAYLOADS:
        result = secret_scanning.compile_ruleset_from_mapping(payload)  # type: ignore[arg-type]
        compiled.append(
            {
                "payload": payload,
                "result": None
                if result is None
                else {"version": result[1], "rules": [[r.name, r.pattern.pattern, r.pattern.flags & re.IGNORECASE != 0, r.description] for r in result[0]]},
            }
        )
    secret_scanning.reset_secret_rule_cache()
    rules, version, loaded = secret_scanning.load_secret_rules()
    packaged = {"version": version, "loaded": loaded, "rules": [[r.name, r.pattern.pattern, r.description] for r in rules]}
    return {"copies": copies, "option_values": option_values, "compile": compiled, "packaged": packaged}


def main() -> None:
    write_guard_parity_cases()
    for folder, name, data in (
        (TESTS / "Hunt" / "Data", "hunt_cases.json", hunt_cases()),
        (TESTS / "Secrets" / "Data", "secret_cases.json", secret_cases()),
    ):
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / name
        path.write_text(json.dumps(data, ensure_ascii=True, indent=1) + "\n", encoding="utf-8")
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
