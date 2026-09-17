"""Generate the CPython oracle data the C# reporting tests compare against.

Temporary; deleted together with the Python package. Re-run after editing a case table:
    python tools/parity/gen_report_cases.py

Writes gui/DriftBuster.Backend.Tests/Reporting/Data/{escape,format,summary,html,json_lines,snapshot}_cases.json. Every string
is written with ``ensure_ascii=True`` so unpaired surrogates survive as ``\\uXXXX`` escapes (the C# tests read the files with
``PythonJson``). Each case holds the inputs as JSON (matches, diffs, hunt hits, profile summaries, redaction settings) and the
exact text Python produced; timestamps come from a fixed ``datetime.now`` stand-in.
"""

from __future__ import annotations

import html as html_lib
import io
import json
import tempfile
from datetime import UTC, datetime
from pathlib import Path

from driftbuster.catalog import DETECTION_CATALOG
from driftbuster.core.types import DetectionMatch, validate_detection_metadata
from driftbuster.hunt import HuntHit, HuntRule
from driftbuster.reporting import html as html_module
from driftbuster.reporting import snapshot as snapshot_module
from driftbuster.reporting.diff import build_unified_diff
from driftbuster.reporting.json_lines import render_json_lines, write_json_lines
from driftbuster.reporting.redaction import RedactionFilter
from driftbuster.reporting.summary import summarise_detections

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[1] / "gui" / "DriftBuster.Backend.Tests" / "Reporting" / "Data"

NOW = datetime(2026, 9, 15, 18, 22, 5, 123456, tzinfo=UTC)
NOW_WHOLE = datetime(2026, 1, 2, 3, 4, 5, tzinfo=UTC)


def _fixed_datetime(moment: datetime) -> type:
    class _Fixed(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return moment

    return _Fixed


def escape_cases() -> list[dict[str, object]]:
    inputs = [chr(code) for code in range(128)]
    inputs.append("".join(chr(code) for code in range(128)))
    inputs += [
        "\ud800",
        "\udfff",
        "a\udc80b<\ud83d",
        "\U0001f600<&>\U0001d518",
        "caf\u00e9 \u2014 \"quoted\" 'single' &amp;",
        "\u2028\u0085\x00",
        "",
    ]
    return [{"input": text, "escaped": html_lib.escape(text)} for text in inputs]


FIXED2_VALUES = [
    0.0, -0.0, 0.125, 0.375, 0.625, 0.005, 0.015, 0.025, 0.995, 2.675, 1.005, -0.001, -0.005, 0.5, 1.5, 2.5,
    123456789.125, 1e22, 1e300, 5e-324, 1e-7, 0.9999999, 0.994999, 1 / 3, 2 / 3, float("inf"), float("-inf"),
    float("nan"), 4503599627370497.5, 9007199254740993.0,
]


def format_cases() -> list[dict[str, object]]:
    return [{"value": value, "fixed2": f"{value:.2f}"} for value in FIXED2_VALUES]


def _match(spec: dict) -> DetectionMatch:
    match = DetectionMatch(
        plugin_name=spec["plugin"],
        format_name=spec["format"],
        variant=spec.get("variant"),
        confidence=spec["confidence"],
        reasons=list(spec.get("reasons", [])),
        metadata=spec.get("metadata"),
    )
    if spec.get("validate"):
        match.metadata = validate_detection_metadata(match, DETECTION_CATALOG)
    return match


def _redaction(spec: dict) -> dict[str, object]:
    kwargs: dict[str, object] = {}
    if "redactor" in spec:
        kwargs["redactor"] = RedactionFilter(tokens=tuple(spec["redactor"]["tokens"]), placeholder=spec["redactor"]["placeholder"])
    if "mask_tokens" in spec:
        kwargs["mask_tokens"] = tuple(spec["mask_tokens"])
    if "placeholder" in spec:
        kwargs["placeholder"] = spec["placeholder"]
    return kwargs


def _hunt_hits(spec: dict) -> list[object]:
    hits: list[object] = []
    for entry in spec.get("hunt_hits", []):
        if entry["kind"] == "mapping":
            hits.append(entry["value"])
            continue
        rule_spec = entry["rule"]
        rule = HuntRule(
            name=rule_spec["name"],
            description=rule_spec["description"],
            token_name=rule_spec.get("token_name"),
            keywords=tuple(rule_spec.get("keywords", ())),
            patterns=tuple(rule_spec.get("patterns", ())),
        )
        hits.append(HuntHit(rule=rule, path=Path(entry["path"]), line_number=entry["line_number"], excerpt=entry["excerpt"]))
    return hits


def _diffs(spec: dict) -> list[object]:
    diffs: list[object] = []
    for entry in spec.get("diffs", []):
        if entry["kind"] == "mapping":
            diffs.append(entry["value"])
            continue
        diffs.append(
            build_unified_diff(
                entry["before"],
                entry["after"],
                content_type=entry.get("content_type", "text"),
                from_label=entry.get("from_label", "before"),
                to_label=entry.get("to_label", "after"),
                label=entry.get("label"),
                mask_tokens=tuple(entry["mask_tokens"]) if "mask_tokens" in entry else None,
            )
        )
    return diffs


SECRET_META = {
    "zeta": "<script>alert('x')</script>",
    "Alpha": True,
    "alpha": None,
    "\u00e9t\u00e9": [1, 2.5, "SECRET", {"inner": "SECRET & <b>"}],
    "\U0001f600": {"nested": {"deeper": ["a", "b"]}, "flag": False},
    "\uffff": 2**70,
    "nan": float("nan"),
    "inf": float("-inf"),
    "empty": {},
    "list_empty": [],
    "quote\"key": "it's \"quoted\"",
}

MATCHES_RICH = [
    {"plugin": "json", "format": "json", "variant": "generic", "confidence": 0.125, "reasons": ["a <reason>", "SECRET in reason"],
     "metadata": SECRET_META},
    {"plugin": "json", "format": "json", "variant": "generic", "confidence": 0.375, "reasons": []},
    {"plugin": "xml", "format": "xml", "variant": "", "confidence": float("nan"), "reasons": ["empty variant"], "metadata": {}},
    {"plugin": "text", "format": "", "variant": None, "confidence": float("inf"), "reasons": ["\ud800 lone"],
     "metadata": {"b": "\U0001d518", "a": "\u00e9"}},
    {"plugin": "ini", "format": "ini", "variant": "\u2014", "confidence": 1e-7, "reasons": ["dash variant"]},
    {"plugin": "ini", "format": "ini", "variant": "Zed", "confidence": 1e22, "reasons": ["z"]},
    {"plugin": "yaml", "format": "yaml", "variant": "secret-variant", "confidence": 0.995, "reasons": []},
]

LARGE_BEFORE = "\n".join(f"line {index}" for index in range(700))
LARGE_AFTER = "\n".join(f"changed {index}" for index in range(700))


def html_cases() -> list[dict[str, object]]:
    return [
        {"name": "minimal", "matches": [MATCHES_RICH[1]]},
        {"name": "no-matches", "matches": [], "title": ""},
        {
            "name": "rich-redactor",
            "title": "Report <\"&'> \U0001f600",
            "matches": MATCHES_RICH,
            "redactor": {"tokens": ["SECRET", "secret", "Diff", "run"], "placeholder": "<&>"},
            "extra_metadata": {"run_id": "run-SECRET", "count": 3, "nested": {"k": ["SECRET"]}},
            "warnings": ["Check <manually>", "<span class=\"raw\">kept raw</span>", "<spanish>", ""],
            "legal_notice": "Handle & <care>",
            "profile_summary": {
                "total_profiles": 2,
                "total_tags": "<tags>",
                "profiles": [
                    {"name": "default", "config_count": 1, "config_ids": ["cfg1", "SECRET-cfg"]},
                    {"name": None, "config_count": "<b>raw</b>", "config_ids": "abc"},
                    {"config_ids": []},
                    "invalid",
                    7,
                ],
                "run_metadata": {"existing": "value"},
            },
            "diffs": [
                {"kind": "mapping", "value": {"label": "Direct", "diff": ""}},
                {"kind": "mapping", "value": {"label": 0, "diff": None, "stats": {}, "safety_limits": "ignored"}},
                {"kind": "mapping", "value": {"label": "Stats <x>", "diff": "-a <b>\n+SECRET", "stats": {"added_lines": 1, "odd<key>": "v&"}}},
                {"kind": "result", "before": "a\nSECRET\nc", "after": "a\nb\nc", "label": None},
                {"kind": "result", "before": LARGE_BEFORE, "after": LARGE_AFTER, "label": "Large", "from_label": "old", "to_label": "new"},
                {"kind": "mapping", "value": {
                    "label": "Notice",
                    "diff": "x",
                    "stats": "not a mapping",
                    "safety_limits": {
                        "canonical": {
                            "before": {"truncated_bytes": 12, "digest": "sha256:aa"},
                            "after": {"truncated_bytes": 0, "digest": "sha256:bb"},
                            "odd": "not a mapping",
                            "third": {"truncated_bytes": 5, "digest": ""},
                        },
                        "diff": {"truncated_lines": 0, "truncated_bytes": 0, "digest": None},
                        "thresholds": {"canonical_bytes": 0, "diff_bytes": 20, "diff_lines": 2},
                    },
                }},
                {"kind": "mapping", "value": {
                    "label": "Bytes only",
                    "diff": "y",
                    "safety_limits": {"diff": {"truncated_bytes": 16, "digest": "sha256:cc"}, "thresholds": {}},
                }},
                {"kind": "mapping", "value": {"label": "No segments", "diff": "z", "safety_limits": {"canonical": {}, "thresholds": {"diff_lines": 3}}}},
            ],
            "hunt_hits": [
                {"kind": "hit", "rule": {"name": "rule", "description": "desc <SECRET>", "token_name": "  token  ",
                                         "keywords": ["SECRET", "Host"], "patterns": [r"server\s+(\S+)"]},
                 "path": "/tmp/file.txt", "line_number": 3, "excerpt": "SECRET value"},
                {"kind": "hit", "rule": {"name": "blank", "description": "", "token_name": "   "},
                 "path": "relative/p\u00e9.cfg", "line_number": 10, "excerpt": "<tag attr='1'>"},
                {"kind": "mapping", "value": {"rule": {"name": "token", "description": "desc", "token_name": "secret"},
                                              "path": "sample", "line_number": 1, "excerpt": "value"}},
                {"kind": "mapping", "value": {"rule": None, "path": None, "excerpt": 5}},
                {"kind": "mapping", "value": {"rule": "text rule", "line_number": [1, 2]}},
                {"kind": "mapping", "value": {"rule": {"token_name": 0}, "run_metadata": {"prior": 1}}},
                {"kind": "mapping", "value": {}},
            ],
        },
        {
            "name": "mask-tokens-no-hits",
            "matches": [MATCHES_RICH[4]],
            "mask_tokens": ["absent"],
            "placeholder": "##",
            "warnings": [],
        },
        {
            "name": "mask-tokens-hits-summary-only",
            "matches": [MATCHES_RICH[5]],
            "mask_tokens": ["Zed", "z", "ini"],
            "extra_metadata": {},
            "profile_summary": {},
            "hunt_hits": [],
            "diffs": [],
        },
        {
            "name": "extra-metadata-only",
            "matches": [MATCHES_RICH[6], MATCHES_RICH[0]],
            "extra_metadata": {"run_id": "abc", "zz": [True, None]},
            "profile_summary": {"profiles": [{"name": "p", "config_count": 0, "config_ids": [1, 2.5, None]}]},
            "hunt_hits": [{"kind": "mapping", "value": {"rule": {"name": "r"}, "path": "p", "line_number": 1, "excerpt": "e"}}],
        },
    ]


def _render_html(case: dict, moment: datetime) -> str:
    kwargs: dict[str, object] = {**_redaction(case)}
    for key in ("title", "profile_summary", "extra_metadata", "warnings", "legal_notice"):
        if key in case:
            kwargs[key] = json.loads(json.dumps(case[key]))
    if "diffs" in case:
        kwargs["diffs"] = _diffs(json.loads(json.dumps(case)))
    if "hunt_hits" in case:
        kwargs["hunt_hits"] = _hunt_hits(json.loads(json.dumps(case)))
    original = html_module.datetime
    html_module.datetime = _fixed_datetime(moment)  # type: ignore[misc]
    try:
        return html_module.render_html_report([_match(spec) for spec in case["matches"]], **kwargs)
    finally:
        html_module.datetime = original  # type: ignore[misc]


def html_outputs() -> list[dict[str, object]]:
    results = []
    for index, case in enumerate(html_cases()):
        moment = NOW if index % 2 == 0 else NOW_WHOLE
        results.append({"case": case, "now": moment.isoformat(), "html": _render_html(case, moment)})
    return results


def json_lines_cases() -> list[dict[str, object]]:
    return [
        {"name": "plain", "matches": [MATCHES_RICH[1]]},
        {
            "name": "non-ascii-nan-nested",
            "matches": MATCHES_RICH,
            "extra_metadata": {"run": "r\u00e9-\U0001f600", "nan": float("nan"), "deep": {"z": [{"y": float("inf")}], "a": None}},
            "profile_summary": {"total": 1, "zeta": "\u2028", "alpha": [1, {"b": 2, "a": 1}]},
            "hunt_hits": [
                {"kind": "hit", "rule": {"name": "token", "description": "", "keywords": ["Key"], "patterns": ["a+", "b\\d"]},
                 "path": "/tmp/secret.txt", "line_number": 1, "excerpt": "SECRET \ud800"},
                {"kind": "mapping", "value": {"rule": {"name": "mapping"}, "path": "file", "line_number": 2, "excerpt": "value",
                                              "run_metadata": {"kept": True}}},
            ],
        },
        {
            "name": "redactor-hits-type-and-keys",
            "matches": [MATCHES_RICH[0], MATCHES_RICH[6]],
            "redactor": {"tokens": ["SECRET", "detect", "hunt", "zeta", "secret"], "placeholder": "\u2588"},
            "profile_summary": {"zeta": "zeta value"},
            "hunt_hits": [{"kind": "mapping", "value": {"rule": {"name": "hunt rule"}, "excerpt": "SECRET"}}],
            "extra_metadata": {"run_id": "SECRET-run"},
        },
        {"name": "mask-tokens", "matches": [MATCHES_RICH[3]], "mask_tokens": ["\u00e9", "\U0001d518"], "placeholder": "[M]"},
        {"name": "empty", "matches": []},
    ]


def json_lines_outputs() -> list[dict[str, object]]:
    results = []
    for case in json_lines_cases():
        outputs: dict[str, object] = {}
        for sort_keys in (True, False):
            spec = json.loads(json.dumps(case))
            kwargs = {
                **_redaction(spec),
                "profile_summary": spec.get("profile_summary"),
                "hunt_hits": _hunt_hits(spec) or None,
                "extra_metadata": spec.get("extra_metadata"),
            }
            rendered = render_json_lines([_match(m) for m in spec["matches"]], sort_keys=sort_keys, **kwargs)
            spec = json.loads(json.dumps(case))
            kwargs = {
                **_redaction(spec),
                "profile_summary": spec.get("profile_summary"),
                "hunt_hits": _hunt_hits(spec) or None,
                "extra_metadata": spec.get("extra_metadata"),
            }
            stream = io.StringIO()
            write_json_lines([_match(m) for m in spec["matches"]], stream, sort_keys=sort_keys, **kwargs)
            suffix = "sorted" if sort_keys else "unsorted"
            outputs[f"render_{suffix}"] = rendered
            outputs[f"write_{suffix}"] = stream.getvalue()
        results.append({"case": case, **outputs})
    return results


def snapshot_cases() -> list[dict[str, object]]:
    return [
        {"name": "defaults", "matches": [MATCHES_RICH[1]], "indent": 2},
        {
            "name": "operator-output-legal",
            "matches": [MATCHES_RICH[0], MATCHES_RICH[2], MATCHES_RICH[4], MATCHES_RICH[6]],
            "operator": "analyst \u00e9",
            "output_name": "report.json",
            "redactor": {"tokens": ["SECRET", "generic", "internal"], "placeholder": "[X]"},
            "legal_metadata": {"retention_days": 10, "classification": "restricted", "extra": [1, None]},
            "extra_metadata": {"scan_id": "abc-123", "operator": "preset"},
            "indent": 4,
        },
        {"name": "mask-tokens-indent-zero", "matches": [MATCHES_RICH[0]], "mask_tokens": ["SECRET"], "placeholder": "##", "indent": 0},
        {"name": "empty-strings", "matches": [], "operator": "", "output_name": "", "extra_metadata": {}, "indent": 1},
    ]


def snapshot_outputs() -> list[dict[str, object]]:
    results = []
    original = snapshot_module.datetime
    snapshot_module.datetime = _fixed_datetime(NOW)  # type: ignore[misc]
    try:
        with tempfile.TemporaryDirectory() as scratch:
            for case in snapshot_cases():
                spec = json.loads(json.dumps(case))
                destination = Path(scratch) / "nested" / f"{spec['name']}.json"
                kwargs = {key: spec[key] for key in ("operator", "output_name", "legal_metadata", "extra_metadata") if key in spec}
                snapshot_module.write_snapshot(
                    [_match(m) for m in spec["matches"]], destination, indent=spec["indent"], **_redaction(spec), **kwargs
                )
                results.append({"case": case, "text": destination.read_text(encoding="utf-8")})
    finally:
        snapshot_module.datetime = original  # type: ignore[misc]
    return results


def summary_cases() -> list[dict[str, object]]:
    remediations = [
        {"id": "b-id", "category": None, "summary": "B summary", "documentation": None},
        {"id": "", "category": "cat", "summary": "b-id", "documentation": "doc"},
        {"id": None, "summary": "summary-only", "category": "first"},
        {"id": 0, "summary": "", "category": "skipped"},
        {"id": 7, "summary": 3, "documentation": "numeric"},
        {"id": "b-id", "category": "late-category", "documentation": "late-doc"},
        {"summary": "summary-only", "category": "second", "documentation": "d"},
        "not a mapping",
        {"id": "a-id"},
        {"id": [1], "summary": {"k": "v"}},
        {"id": None, "summary": "shared", "documentation": ""},
        {"id": "shared", "category": "filled", "documentation": "filled-doc"},
    ]
    return [
        {"name": "catalog", "matches": [
            {"plugin": "ini", "format": "ini", "variant": "dotenv", "confidence": 0.8, "reasons": ["synthetic"], "validate": True},
            {"plugin": "conf", "format": "unix-conf", "variant": "generic-directive-text", "confidence": 0.6, "reasons": ["synthetic"],
             "validate": True},
            {"plugin": "json", "format": "json", "variant": None, "confidence": 0.5, "reasons": ["synthetic"], "validate": True},
            {"plugin": "xml", "format": "xml", "variant": "msbuild-project", "confidence": 0.9, "reasons": ["synthetic"], "validate": True},
        ]},
        {"name": "adversarial", "matches": [
            {"plugin": "p", "format": "fmt", "variant": "v2", "confidence": 0.3, "reasons": ["z", "a", "\u00e9", "a"],
             "metadata": {"catalog_severity": "high", "catalog_severity_hint": "hint-1", "catalog_remediations": remediations,
                          "\U0001f600": 1, "Z": 2}},
            {"plugin": "p", "format": "fmt", "variant": "v2", "confidence": float("nan"), "reasons": ["b"],
             "metadata": {"catalog_severity": "low", "catalog_severity_hint": "hint-2", "catalog_remediations": "string"}},
            {"plugin": "p", "format": "fmt", "variant": "\u2014", "confidence": 0.2, "reasons": [],
             "metadata": {"catalog_severity": "", "catalog_severity_hint": 5, "catalog_remediations": [{"id": "dash"}]}},
            {"plugin": "p", "format": "fmt", "variant": "", "confidence": float("inf"), "reasons": [],
             "metadata": {"catalog_severity": 3, "catalog_remediations": {"id": "mapping-not-sequence"}}},
            {"plugin": "p", "format": "", "variant": "Zed", "confidence": 0.0, "reasons": [],
             "metadata": {"catalog_severity": "high", "catalog_remediations": [{"id": "b-id", "summary": "other"}]}},
            {"plugin": "p", "format": "\u00e9", "variant": "a", "confidence": -1.0, "reasons": [], "metadata": None},
        ]},
        {"name": "empty", "matches": []},
    ]


def summary_outputs() -> list[dict[str, object]]:
    results = []
    for case in summary_cases():
        spec = json.loads(json.dumps(case))
        summary = summarise_detections([_match(m) for m in spec["matches"]])
        results.append({"case": case, "json": json.dumps(summary, ensure_ascii=True)})
    return results


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    tables = {
        "escape_cases.json": escape_cases(),
        "format_cases.json": format_cases(),
        "summary_cases.json": summary_outputs(),
        "html_cases.json": html_outputs(),
        "json_lines_cases.json": json_lines_outputs(),
        "snapshot_cases.json": snapshot_outputs(),
    }
    for name, data in tables.items():
        (OUT / name).write_text(json.dumps(data, ensure_ascii=True, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
