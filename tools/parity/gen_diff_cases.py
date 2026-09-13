"""Generate the CPython oracle data the C# diff tests compare against.

Temporary; deleted together with the Python package. Re-run after editing a case table:
    python tools/parity/gen_diff_cases.py

Writes gui/DriftBuster.Backend.Tests/Diff/Data/{difflib,canonicalise,build}_cases.json. Every string is written with
``ensure_ascii=True`` so unpaired surrogates survive as ``\\uXXXX`` escapes (the C# tests read the files with
``PythonJson``).
"""

from __future__ import annotations

import difflib
import json
import random
from pathlib import Path

from driftbuster.reporting import diff as diff_module
from driftbuster.reporting.redaction import RedactionFilter

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[1] / "gui" / "DriftBuster.Backend.Tests" / "Diff" / "Data"


def _lines(seed: int, count: int, alphabet: int, prefix: str = "line") -> list[str]:
    rng = random.Random(seed)
    return [f"{prefix}{rng.randrange(alphabet)}" for _ in range(count)]


def _mutate(seed: int, lines: list[str], edits: int, alphabet: int) -> list[str]:
    rng = random.Random(seed)
    result = list(lines)
    for _ in range(edits):
        op = rng.randrange(3)
        position = rng.randrange(len(result) + 1)
        if op == 0 and result:
            del result[min(position, len(result) - 1)]
        elif op == 1:
            result.insert(position, f"new{rng.randrange(alphabet)}")
        elif result:
            result[min(position, len(result) - 1)] = f"swap{rng.randrange(alphabet)}"
    return result


def _config_file(seed: int, sections: int) -> list[str]:
    rng = random.Random(seed)
    lines: list[str] = []
    for section in range(sections):
        lines.append("{")
        lines.append(f'  "name": "section{section}",')
        for key in range(rng.randrange(2, 6)):
            lines.append(f'  "key{key}": {rng.randrange(4)},')
        lines.append("}")
        lines.append("")
    return lines


# Context sizes for the grouped opcodes and unified diffs: the usual ones plus the edges where n + n, i1 + n and i2 - n leave
# the 32-bit range (Python integers never overflow).
CONTEXTS = (-(2**31), -(2**30) - 1, -(2**30), -1, 0, 1, 3, 2**30 - 1, 2**30, 2**31 - 1)


def difflib_cases() -> list[dict[str, object]]:
    raw: list[tuple[str, list[str], list[str]]] = [
        ("both-empty", [], []),
        ("a-empty", [], ["x", "y"]),
        ("b-empty", ["x", "y"], []),
        ("identical", ["a", "b", "c"], ["a", "b", "c"]),
        ("single-replace", ["a", "b", "c"], ["a", "B", "c"]),
        ("insert-start", ["b", "c"], ["a", "b", "c"]),
        ("insert-end", ["a", "b"], ["a", "b", "c"]),
        ("delete-middle", ["a", "b", "c"], ["a", "c"]),
        ("tie-two-candidates", ["x", "y", "x", "y"], ["y", "x"]),
        ("tie-reversed", ["a", "b"], ["b", "a"]),
        ("repeated-small", ["a"] * 5 + ["b"] + ["a"] * 5, ["a"] * 4 + ["c"] + ["a"] * 6),
        ("empty-strings", ["", "", "x", ""], ["", "x", "", ""]),
        ("whitespace-and-case", ["Key = 1", "key = 1 ", "\tkey"], ["key = 1", "Key = 1", " key"]),
        ("astral-and-surrogate-order", ["\U0001F600", "\ue000", "a"], ["\ue000", "\U0001F600", "a"]),
        ("adjacent-blocks-collapse", ["a", "b", "c", "d", "e"], ["a", "b", "X", "c", "d", "e"]),
        ("all-different", ["a", "b", "c"], ["d", "e", "f"]),
        ("199-lines-repeated", ["}"] * 60 + _lines(1, 139, 40), ["}"] * 61 + _lines(1, 138, 40)),
        ("200-lines-popular", ["}"] * 60 + _lines(2, 140, 40), ["}"] * 60 + _lines(3, 140, 40)),
        ("201-lines-popular", _lines(4, 201, 3), _mutate(5, _lines(4, 201, 3), 12, 3)),
        ("ntest-boundary-3-not-popular", ["p"] * 3 + [f"u{i}" for i in range(197)], [f"u{i}" for i in range(197)] + ["p"] * 3),
        ("ntest-boundary-4-popular", ["p"] * 4 + [f"u{i}" for i in range(196)], [f"u{i}" for i in range(196)] + ["p"] * 4),
        ("popular-only-match", ["x"] * 3 + ["}"] * 10, ["}"] * 150 + [f"v{i}" for i in range(60)]),
        ("popular-extension", ["a", "}", "}", "b"], ["q"] * 10 + ["a", "}", "}", "b"] + ["}"] * 190),
        ("config-300-lines", _config_file(6, 45), _mutate(7, _config_file(6, 45), 20, 5)),
        ("random-alphabet-5-250", _lines(8, 250, 5), _mutate(9, _lines(8, 250, 5), 30, 5)),
        ("random-alphabet-50-400", _lines(10, 400, 50), _mutate(11, _lines(10, 400, 50), 40, 50)),
        ("random-alphabet-500-300", _lines(12, 300, 500), _mutate(13, _lines(12, 300, 500), 25, 500)),
        ("random-small-30", _lines(14, 30, 4), _mutate(15, _lines(14, 30, 4), 8, 4)),
        ("a-long-b-short-popular", _lines(16, 400, 2), _lines(17, 20, 2)),
        ("a-short-b-long-popular", _lines(18, 20, 2), _lines(19, 400, 2)),
        ("blank-line-runs", ([""] * 50 + ["x"]) * 5, ([""] * 49 + ["x", "y"]) * 5),
        ("far-apart-changes", [f"l{i}" for i in range(40)], [f"l{i}" if i not in (2, 30) else f"c{i}" for i in range(40)]),
    ]
    cases = []
    for name, a, b in raw:
        matcher = difflib.SequenceMatcher(None, a, b)
        cases.append(
            {
                "name": name,
                "a": a,
                "b": b,
                "popular": sorted(matcher.bpopular),
                "matching_blocks": [list(block) for block in matcher.get_matching_blocks()],
                "opcodes": [list(code) for code in difflib.SequenceMatcher(None, a, b).get_opcodes()],
                "grouped": {
                    str(n): [[list(code) for code in group] for group in difflib.SequenceMatcher(None, a, b).get_grouped_opcodes(n)]
                    for n in CONTEXTS
                },
                "unified": {
                    str(n): "\n".join(difflib.unified_diff(a, b, fromfile="left.cfg", tofile="right.cfg", lineterm="", n=n))
                    for n in (*CONTEXTS, 5)
                },
                "unified_default": "".join(
                    difflib.unified_diff(a, b, fromfile="a", tofile="b", fromfiledate="2026-01-01", tofiledate="")
                ),
            }
        )
    return cases


CANONICAL_INPUTS: list[tuple[str, str]] = [
    ("empty", ""),
    ("spaces-only", "   "),
    ("newlines-only", "\n\n"),
    ("bom-run", "\ufeff\ufeff  text \n"),
    ("bom-after-space", " \ufeffa"),
    ("crlf-cr-lf", "a\r\nb\rc\n\r\n"),
    ("unicode-newlines", "a\u2028b\u2029c\u0085d"),
    ("other-splitlines-boundaries", "a\x0bb\x0cc\x1cd\x1de\x1ef"),
    ("trailing-python-space", "a\x1f\nb\xa0\u3000\nc\u200b"),
    ("json-sorted", '{"b": 1, "a": {"z": [1, 2.5, -0.0, 1e16, 1e-5], "y": null}, "\ue000": true, "\U0001F600": false}'),
    ("json-escapes", '["\\u0001\\b\\f\\n\\r\\t\\"\\\\/", "\\u2028\\u007f", "\\ud83d\\ude00", "caf\\u00e9"]'),
    ("json-lone-surrogate", '{"k": "\\ud800"}'),
    ("json-empty-containers", '{"a": {}, "b": [], "c": [[]], "d": [{}]}'),
    ("json-big-numbers", '[12345678901234567890, 1.7976931348623157e308, 5e-324, 1E400, -1E400, NaN, Infinity, -Infinity]'),
    ("json-duplicate-keys", '{"a": 1, "b": 2, "a": 3}'),
    ("json-scalar", '  "just a string"  '),
    ("json-whitespace-wrapped", "\u3000\n{\"a\": 1}\x1f"),
    ("json-bom", "\ufeff{\"a\": 1}"),
    ("json-invalid", "{not-json\r\n  trailing  "),
    ("json-trailing-comma", '{"a": 1,}'),
    ("json-nested-depth-50", "[" * 50 + "]" * 50),
    ("xml-basic", "<root b='2' a='1'>\n  <child>value</child>\n</root>\n"),
    ("xml-declaration-doctype", '<?xml version="1.0"?>\n<!DOCTYPE note [<!ELEMENT note ANY>]>\n<note x=" y "> text </note>'),
    ("xml-comments-pi-cdata", "<!-- top --><a>\r\n <!--  --> x <?pi data?> y <![CDATA[ c\r\nd ]]>&#13;&amp;&#x1F600;<b/>  tail  <!-- c -->\n</a><!-- after -->"),
    ("xml-attribute-escapes", "<a t='a\tb\nc' q='\"' e=' ' r='&#13;&#10;&#9;' lt='&lt;&gt;&amp;'>x > y</a>"),
    ("xml-upper-declaration", "  <?XML version='1.0' ?>  <a/>"),
    ("xml-stylesheet-pi-as-declaration", "<?xml-stylesheet href='x'?><a/>"),
    ("xml-declaration-without-question", "<?xml version='1.0'><a/>"),
    ("xml-doctype-lowercase", "<!doctype html><html><body/></html>"),
    ("xml-doctype-quoted-bracket", '<!DOCTYPE a [<!ELEMENT a ANY><!ATTLIST a v CDATA "]">]><a/>'),
    ("xml-doctype-unclosed", "<!DOCTYPE broken ["),
    ("xml-doctype-after-comment-attlist", "<!-- c --><!DOCTYPE a [<!ATTLIST a d CDATA 'def'>]><a/>"),
    ("xml-whitespace-char-refs", "<a>&#x85;&#x2028;</a>"),
    ("xml-nbsp-text", "<a>\xa0<b>\u3000</b>\x1c</a>"),
    ("xml-mixed-content", "<a>one<b>two</b>three<c/>four</a>"),
    ("xml-unclosed", "<root><unclosed></root>"),
    ("xml-truncated", "<root>   \r\n  <child>value</child>   \r\n</root"),
    ("xml-bom", "\ufeff<?xml version='1.0'?><root> value </root>"),
    ("xml-trailing-garbage", "<a/>junk"),
    ("xml-undefined-entity", "<a>&nbsp;</a>"),
    ("xml-deep-200", "<a>" * 200 + "</a>" * 200),
    ("xml-attribute-order-code-point", "<a \u00e9='1' z='2' Z='3' _='4' \u4e2d='5'/>"),
    ("xml-xml-lang", "<a xml:lang='en' b='1'/>"),
    ("text-lone-surrogate", "a\ud800b"),
]

# canonicalise_xml outputs the port intentionally does not reproduce (plan fix d, entity expansion refused).
XML_DIVERGENT = {
    "xml-namespaces": "<p:a xmlns:p='urn:p' xmlns='urn:d' xmlns:u='urn:unused'><b p:x='1' y='2'/><c xmlns=''/></p:a>",
    "xml-entity-after-comment": "<!-- c --><!DOCTYPE a [<!ENTITY x 'yy'>]><a>&x;</a>",
    "xml-entity-quoted-bracket": '<!DOCTYPE a [<!ENTITY x "[">]><a>&x;</a>',
}


def _outcome(function, payload: str) -> dict[str, object]:
    try:
        return {"value": function(payload)}
    except Exception as exc:  # noqa: BLE001 - the oracle records whatever Python raises
        return {"error": type(exc).__name__}


def canonicalise_cases() -> list[dict[str, object]]:
    cases = []
    for name, payload in CANONICAL_INPUTS:
        cases.append(
            {
                "name": name,
                "payload": payload,
                "text": _outcome(diff_module.canonicalise_text, payload),
                "json": _outcome(diff_module.canonicalise_json, payload),
                "xml": _outcome(diff_module.canonicalise_xml, payload),
            }
        )
    for name, payload in XML_DIVERGENT.items():
        cases.append({"name": name, "payload": payload, "python_xml": _outcome(diff_module.canonicalise_xml, payload)})
    return cases


def _long_lines(count: int, width: int) -> str:
    return "\n".join(f"{index:05d} " + "\u00e9" * width for index in range(count))


BUILD_CASES: list[dict[str, object]] = [
    {"name": "text-identical", "before": "same\n", "after": "same\n"},
    {"name": "text-change", "before": "alpha\nbeta\ngamma\n", "after": "alpha\nBETA\ngamma\ndelta\n", "from_label": "a.txt", "to_label": "b.txt"},
    {"name": "json-reorder", "before": '{"b": 1, "a": 2}', "after": '{"a": 2, "b": 3}', "content_type": "json", "label": "cfg"},
    {"name": "xml-change", "before": "<a x='1'><b/></a>", "after": "<a x='2'><b/><c/></a>", "content_type": "xml", "context_lines": 0},
    {"name": "mask-tokens", "before": "token = SECRET\npw=hunter2\n", "after": "token = SECRET\npw=hunter3\n", "mask_tokens": ["SECRET", "hunter2", "", "SECRET"], "placeholder": "***"},
    {"name": "mask-tokens-empty-list", "before": "a", "after": "b", "mask_tokens": []},
    {"name": "mask-tokens-only-empty", "before": "a", "after": "b", "mask_tokens": [""]},
    {"name": "redactor-longest-first", "before": "tokenised token\n", "after": "token\n", "redactor": ["token", "tokenised", "ok"]},
    {"name": "context-10", "before": "\n".join(f"l{i}" for i in range(40)), "after": "\n".join(f"l{i}" if i != 20 else "x" for i in range(40)), "context_lines": 10},
    {"name": "clamp-canonical", "before": "alpha\n" + "x" * 64, "after": "beta\n" + "y" * 64, "limits": [32, 48, 2]},
    {"name": "clamp-lines-only", "before": "\n".join(f"a{i}" for i in range(30)), "after": "\n".join(f"b{i}" for i in range(30)), "limits": [1 << 20, 1 << 20, 10]},
    {"name": "clamp-bytes-multibyte-split", "before": _long_lines(20, 30), "after": _long_lines(20, 31), "limits": [1 << 20, 1001, 600]},
    {"name": "clamp-canonical-multibyte-split", "before": "\u00e9" * 40, "after": "\U0001F600" * 20, "limits": [33, 1 << 20, 600]},
    {"name": "clamp-after-only", "before": "short", "after": "long " * 20, "limits": [40, 1 << 20, 600]},
    {"name": "clamp-lines-and-bytes", "before": _long_lines(50, 10), "after": _long_lines(50, 11), "limits": [1 << 20, 500, 30]},
    {"name": "clamp-diff-splitlines-boundaries", "before": "a\x1cb\x1cc\x1cd", "after": "a\x1cB\x1cc\x1cD", "limits": [1 << 20, 30, 600]},
    # The joined lines (CRLF from the placeholder becomes LF) fit the byte limit while the original diff does not.
    {"name": "clamp-diff-only-original-too-big", "before": "aXb\ncXd\nq", "after": "aXb\ncYd\nq", "mask_tokens": ["X"], "placeholder": "\r\n", "limits": [1 << 20, 55, 600]},
    {"name": "clamp-defaults-large", "before": _long_lines(700, 1), "after": _long_lines(700, 2)},
]


def _dict(value):
    return None if value is None else dict(value)


def build_cases() -> list[dict[str, object]]:
    out = []
    defaults = (
        diff_module._SAFE_DIFF_MAX_CANONICAL_BYTES,
        diff_module._SAFE_DIFF_MAX_DIFF_BYTES,
        diff_module._SAFE_DIFF_MAX_DIFF_LINES,
    )
    for case in BUILD_CASES:
        limits = case.get("limits", defaults)
        (
            diff_module._SAFE_DIFF_MAX_CANONICAL_BYTES,
            diff_module._SAFE_DIFF_MAX_DIFF_BYTES,
            diff_module._SAFE_DIFF_MAX_DIFF_LINES,
        ) = limits
        try:
            redactor = RedactionFilter(tokens=tuple(case["redactor"])) if "redactor" in case else None
            result = diff_module.build_unified_diff(
                case["before"],
                case["after"],
                content_type=case.get("content_type", "text"),
                from_label=case.get("from_label", "before"),
                to_label=case.get("to_label", "after"),
                label=case.get("label"),
                redactor=redactor,
                mask_tokens=tuple(case["mask_tokens"]) if "mask_tokens" in case else None,
                placeholder=case.get("placeholder", "[REDACTED]"),
                context_lines=case.get("context_lines", 3),
            )
            payload = dict(diff_module.diff_summary_to_payload(diff_module.summarise_diff_result(result, versions=("v1", "v2"))))
            payload.pop("generated_at")
        finally:
            (
                diff_module._SAFE_DIFF_MAX_CANONICAL_BYTES,
                diff_module._SAFE_DIFF_MAX_DIFF_BYTES,
                diff_module._SAFE_DIFF_MAX_DIFF_LINES,
            ) = defaults
        out.append(
            {
                "case": {key: value for key, value in case.items()},
                "limits": list(limits),
                "result": {
                    "canonical_before": result.canonical_before,
                    "canonical_after": result.canonical_after,
                    "diff": result.diff,
                    "stats": dict(result.stats),
                    "mask_tokens": None if result.mask_tokens is None else list(result.mask_tokens),
                    "placeholder": result.placeholder,
                    "redaction_counts": _dict(result.redaction_counts),
                    "safety_limits": _dict(result.safety_limits),
                },
                "payload": payload,
            }
        )
    return out


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, data in (
        ("difflib_cases.json", difflib_cases()),
        ("canonicalise_cases.json", canonicalise_cases()),
        ("build_cases.json", build_cases()),
    ):
        (OUT / name).write_text(json.dumps(data, ensure_ascii=True, indent=1) + "\n", encoding="utf-8")
        print(f"wrote {OUT / name}")


if __name__ == "__main__":
    main()
