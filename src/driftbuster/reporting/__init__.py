"""Reporting adapters and capture helpers for DriftBuster.

The reporting package currently focuses on the compliance-sensitive pieces
required by CLOUDTASKS area A11. It exposes light-weight helpers that wrap the
core detector data structures while making it trivial to plug in token
redaction before serialising outputs.
"""

from .redaction import RedactionFilter, redact_data, resolve_redactor
from .json import iter_json_records, render_json_lines, write_json_lines
from .html import render_html_report, write_html_report
from .diff import (
    DiffResult,
    DiffResultSummary,
    build_unified_diff,
    canonicalise_text,
    canonicalise_xml,
    diff_summary_to_payload,
    render_unified_diff,
    summarise_diff_result,
    summarise_diff_results,
)
from .snapshot import build_snapshot_manifest, write_snapshot
from .summary import summarise_detections

__all__ = [
    "RedactionFilter",
    "redact_data",
    "resolve_redactor",
    "iter_json_records",
    "render_json_lines",
    "write_json_lines",
    "render_html_report",
    "write_html_report",
    "canonicalise_text",
    "canonicalise_xml",
    "DiffResult",
    "DiffResultSummary",
    "build_unified_diff",
    "summarise_diff_result",
    "summarise_diff_results",
    "diff_summary_to_payload",
    "render_unified_diff",
    "build_snapshot_manifest",
    "write_snapshot",
    "summarise_detections",
]
