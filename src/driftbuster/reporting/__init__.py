"""Reporting adapters and capture helpers for DriftBuster.

The reporting package currently focuses on the compliance-sensitive pieces
required by CLOUDTASKS area A11. It exposes light-weight helpers that wrap the
core detector data structures while making it trivial to plug in token
redaction before serialising outputs.
"""

from .diff import (
    DiffResult,
    DiffResultSummary,
    build_unified_diff,
    canonicalise_json,
    canonicalise_text,
    canonicalise_xml,
    diff_summary_to_payload,
    render_unified_diff,
    summarise_diff_result,
    summarise_diff_results,
)
from .html import render_html_report, write_html_report
from .json import iter_json_records, render_json_lines, write_json_lines
from .redaction import RedactionFilter, redact_data, resolve_redactor
from .snapshot import build_snapshot_manifest, write_snapshot
from .summary import summarise_detections

__all__ = [
    "DiffResult",
    "DiffResultSummary",
    "RedactionFilter",
    "build_snapshot_manifest",
    "build_unified_diff",
    "canonicalise_json",
    "canonicalise_text",
    "canonicalise_xml",
    "diff_summary_to_payload",
    "iter_json_records",
    "redact_data",
    "render_html_report",
    "render_json_lines",
    "render_unified_diff",
    "resolve_redactor",
    "summarise_detections",
    "summarise_diff_result",
    "summarise_diff_results",
    "write_html_report",
    "write_json_lines",
    "write_snapshot",
]
