"""Static HTML renderer with embedded redaction warnings."""

from __future__ import annotations

from datetime import datetime, timezone
from html import escape
from os import PathLike
from pathlib import Path
from typing import Iterable, Mapping, Sequence, TextIO

from ..core.types import DetectionMatch
from ..hunt import HuntHit
from ._metadata import iter_detection_payloads
from .diff import DiffResult
from .redaction import RedactionFilter, redact_data, resolve_redactor

__all__ = ["render_html_report", "write_html_report"]


_HTML_HEADER = """<!doctype html>
<html lang=\"en\">
<head>
  <meta charset=\"utf-8\" />
  <title>{title}</title>
  <style>
    body {{ font-family: Arial, sans-serif; margin: 2rem; background: #111; color: #eee; }}
    h1, h2 {{ color: #f6c744; }}
    .meta {{ font-size: 0.9rem; color: #ccc; margin-bottom: 1rem; }}
    .warning {{ border: 1px solid #d9534f; padding: 1rem; margin-bottom: 1.5rem; background: #2a0000; }}
    .badge {{
      display: inline-block;
      padding: 0.1rem 0.4rem;
      border-radius: 0.25rem;
      font-size: 0.75rem;
      margin-left: 0.5rem;
      background: #f6c744;
      color: #111;
    }}
    .match,
    .diff-block,
    .profile-summary,
    .hunt-section {{
      border: 1px solid #333;
      padding: 1rem;
      margin-bottom: 1rem;
      background: #1a1a1a;
    }}
    .match h3 {{ margin-top: 0; }}
    table {{ width: 100%; border-collapse: collapse; margin-top: 0.5rem; }}
    th, td {{ border: 1px solid #333; padding: 0.5rem; text-align: left; }}
    .summary-table {{ margin-bottom: 1rem; }}
    .diff-block pre {{ background: #000; color: #f6f6f6; padding: 1rem; overflow-x: auto; border: 1px solid #333; }}
    .diff-stats {{ list-style: none; padding: 0; margin: 0.5rem 0 0.5rem 0; display: flex; gap: 1.5rem; }}
    .diff-stats li {{ font-size: 0.85rem; color: #ccc; }}
    .hunt-section ul {{ margin: 0.5rem 0 0 1rem; }}
    .redaction-summary {{ margin-top: 1.5rem; padding: 1rem; background: #1f1f1f; border: 1px solid #444; }}
  </style>
</head>
<body>
"""


def _format_metadata(metadata: Mapping[str, object]) -> str:
    rows = []
    for key, value in sorted(metadata.items()):
        rows.append(f"<tr><th>{escape(str(key))}</th><td>{escape(str(value))}</td></tr>")
    return "\n".join(rows)


def _render_match(match: Mapping[str, object], index: int) -> str:
    metadata = match.get("metadata")
    metadata_table = ""
    if isinstance(metadata, Mapping):
        metadata_table = f"<table>{_format_metadata(metadata)}</table>"
    reasons_list = "".join(f"<li>{escape(str(reason))}</li>" for reason in match.get("reasons", []))
    return (
        "<section class=\"match\">"
        f"<h3>Match {index}: {escape(str(match.get('format')))}</h3>"
        f"<p><strong>Plugin:</strong> {escape(str(match.get('plugin')))} | "
        f"<strong>Variant:</strong> {escape(str(match.get('variant')) or '—')}</p>"
        f"<p><strong>Confidence:</strong> {escape(str(match.get('confidence')))}</p>"
        f"<h4>Reasons</h4><ul>{reasons_list or '<li>None provided</li>'}</ul>"
        f"<h4>Metadata</h4>{metadata_table}"
        "</section>"
    )


def _render_detection_summary(matches: Sequence[Mapping[str, object]]) -> str:
    if not matches:
        return ""
    aggregates: dict[tuple[str, str], dict[str, object]] = {}
    for record in matches:
        format_name = str(record.get("format") or "unknown")
        variant_name = str(record.get("variant") or "—")
        key = (format_name, variant_name)
        bucket = aggregates.setdefault(key, {"count": 0, "max_confidence": 0.0})
        bucket["count"] = int(bucket["count"]) + 1
        try:
            confidence = float(record.get("confidence", 0.0) or 0.0)
        except (TypeError, ValueError):
            confidence = 0.0
        bucket["max_confidence"] = max(float(bucket["max_confidence"]), confidence)

    rows = []
    for (format_name, variant_name), info in sorted(aggregates.items()):
        rows.append(
            "<tr>"
            f"<td>{escape(format_name)}</td>"
            f"<td>{escape(variant_name)}</td>"
            f"<td>{info['count']}</td>"
            f"<td>{info['max_confidence']:.2f}</td>"
            "</tr>"
        )
    return (
        "<section class=\"match summary\">"
        "<h2>Detection Summary</h2>"
        "<table class=\"summary-table\">"
        "<thead><tr><th>Format</th><th>Variant</th><th>Matches</th><th>Peak confidence</th></tr></thead>"
        f"<tbody>{''.join(rows)}</tbody>"
        "</table>"
        "</section>"
    )


def _serialise_diff(diff: DiffResult | Mapping[str, object]) -> Mapping[str, object]:
    if isinstance(diff, DiffResult):
        return {
            "label": diff.label or "Diff",
            "diff": diff.diff,
            "stats": dict(diff.stats),
        }
    return dict(diff)


def _render_diff_section(diffs: Sequence[Mapping[str, object]]) -> str:
    if not diffs:
        return ""
    sections = ["<section class=\"diffs\">"]
    sections.append("<h2>Configuration Diffs</h2>")
    for entry in diffs:
        label = escape(str(entry.get("label") or "Diff"))
        stats = entry.get("stats")
        diff_text = escape(str(entry.get("diff") or ""))
        sections.append("<article class=\"diff-block\">")
        sections.append(f"<h3>{label}</h3>")
        if isinstance(stats, Mapping) and stats:
            stat_lines = []
            for key, value in stats.items():
                stat_lines.append(f"<li>{escape(str(key))}: {escape(str(value))}</li>")
            sections.append("<ul class=\"diff-stats\">" + "".join(stat_lines) + "</ul>")
        sections.append(f"<pre>{diff_text}</pre>")
        sections.append("</article>")
    sections.append("</section>")
    return "".join(sections)


def _serialise_hunt_hit(hit: HuntHit | Mapping[str, object]) -> Mapping[str, object]:
    if isinstance(hit, Mapping):
        return dict(hit)
    rule = hit.rule
    return {
        "rule": {
            "name": rule.name,
            "description": rule.description,
            "token_name": rule.token_name,
            "keywords": rule.keywords,
            "patterns": tuple(getattr(pattern, "pattern", pattern) for pattern in rule.patterns),
        },
        "path": str(hit.path),
        "line_number": hit.line_number,
        "excerpt": hit.excerpt,
    }


def _render_hunt_section(hits: Sequence[Mapping[str, object]]) -> str:
    if not hits:
        return ""
    items = []
    for entry in hits:
        rule = entry.get("rule", {})
        token_name = rule.get("token_name") if isinstance(rule, Mapping) else None
        token_badge = f"<span class=\"badge\">token: {escape(str(token_name))}</span>" if token_name else ""
        description = rule.get("description") if isinstance(rule, Mapping) else ""
        items.append(
            "<li>"
            f"<strong>{escape(str(entry.get('path')))}</strong> — line {escape(str(entry.get('line_number')))}"
            f"<br/><em>{escape(str(description))}</em> {token_badge}"
            f"<br/><code>{escape(str(entry.get('excerpt')))}</code>"
            "</li>"
        )
    return "<section class=\"hunt-section\"><h2>Hunt Highlights</h2><ul>" + "".join(items) + "</ul></section>"


def _render_profile_summary(summary: Mapping[str, object]) -> str:
    if not summary:
        return ""
    totals = []
    for key in ("total_profiles", "total_configs", "total_tags"):
        if key in summary:
            totals.append(f"<li>{escape(key.replace('_', ' ').title())}: {escape(str(summary[key]))}</li>")
    profile_rows = []
    for profile in summary.get("profiles", []):
        if not isinstance(profile, Mapping):
            continue
        config_ids = ", ".join(str(config_id) for config_id in profile.get("config_ids", [])) or "—"
        profile_rows.append(
            "<tr>"
            f"<td>{escape(str(profile.get('name')))}</td>"
            f"<td>{profile.get('config_count', 0)}</td>"
            f"<td>{escape(config_ids)}</td>"
            "</tr>"
        )
    parts = ["<section class=\"profile-summary\"><h2>Profile Summary</h2>"]
    if totals:
        parts.append("<ul>" + "".join(totals) + "</ul>")
    if profile_rows:
        parts.append(
            "<table><thead><tr><th>Name</th><th>Configs</th><th>Config IDs</th></tr></thead>"
            f"<tbody>{''.join(profile_rows)}</tbody></table>"
        )
    parts.append("</section>")
    return "".join(parts)


def render_html_report(
    matches: Iterable[DetectionMatch],
    *,
    title: str = "DriftBuster Report",
    diffs: Sequence[DiffResult | Mapping[str, object]] | None = None,
    profile_summary: Mapping[str, object] | None = None,
    hunt_hits: Iterable[HuntHit | Mapping[str, object]] | None = None,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
    extra_metadata: Mapping[str, object] | None = None,
    warnings: Sequence[str] | None = None,
    legal_notice: str | None = None,
) -> str:
    """Render ``matches`` into an HTML report with redaction summaries."""

    active_redactor = resolve_redactor(redactor=redactor, mask_tokens=mask_tokens, placeholder=placeholder)
    prepared_matches: list[Mapping[str, object]] = []
    for record in iter_detection_payloads(matches, extra_metadata=extra_metadata):
        if active_redactor:
            record = redact_data(record, active_redactor)
        prepared_matches.append(record)

    prepared_diffs: list[Mapping[str, object]] = []
    for diff in diffs or ():
        entry = dict(_serialise_diff(diff))
        if active_redactor:
            entry = redact_data(entry, active_redactor)
        prepared_diffs.append(entry)

    prepared_hunts: list[Mapping[str, object]] = []
    if hunt_hits:
        for hit in hunt_hits:
            entry = dict(_serialise_hunt_hit(hit))
            if extra_metadata:
                entry.setdefault("run_metadata", {}).update(extra_metadata)
            if active_redactor:
                entry = redact_data(entry, active_redactor)
            prepared_hunts.append(entry)

    prepared_summary: Mapping[str, object] | None = None
    if profile_summary:
        summary_payload = dict(profile_summary)
        if extra_metadata:
            summary_payload.setdefault("run_metadata", {}).update(extra_metadata)
        if active_redactor:
            summary_payload = redact_data(summary_payload, active_redactor)
        prepared_summary = summary_payload

    generated_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    parts = [_HTML_HEADER.format(title=escape(title))]
    parts.append(f"<div class=\"meta\">Generated at {escape(generated_at)}</div>")

    warning_messages = list(warnings or [])
    warning_messages.append("Derived data only. Do not redistribute without legal approval.")
    if active_redactor:
        warning_messages.append(
            f"Tokens replaced with {escape(active_redactor.placeholder)}. Original values are not stored in this report."
        )
        warning_messages.append("<span class=\"badge\">Redaction active</span>")
    if warning_messages:
        formatted = []
        for message in warning_messages:
            if message.startswith("<span"):
                formatted.append(message)
            else:
                formatted.append(escape(message))
        parts.append("<div class=\"warning\">" + "<br/>".join(formatted) + "</div>")

    summary_block = _render_detection_summary(prepared_matches)
    if summary_block:
        parts.append(summary_block)

    for index, record in enumerate(prepared_matches, start=1):
        parts.append(_render_match(record, index))

    if prepared_summary:
        parts.append(_render_profile_summary(prepared_summary))

    if prepared_diffs:
        parts.append(_render_diff_section(prepared_diffs))

    if prepared_hunts:
        parts.append(_render_hunt_section(prepared_hunts))

    summary_lines = ["<div class=\"redaction-summary\">", "<h2>Redaction Summary</h2>"]
    if active_redactor and active_redactor.has_hits:
        summary_lines.append("<ul>")
        for token, count in sorted(active_redactor.stats().items()):
            summary_lines.append(
                f"<li>{escape(token)} → {escape(active_redactor.placeholder)} (occurrences: {count})</li>"
            )
        summary_lines.append("</ul>")
    else:
        summary_lines.append(
            "<p>No configured tokens were encountered in this report. Manually inspect before external sharing.</p>"
        )
    if legal_notice:
        summary_lines.append(f"<p>{escape(legal_notice)}</p>")
    parts.append("\n".join(summary_lines) + "</div>")

    parts.append("</body></html>")
    return "\n".join(parts)


def write_html_report(
    matches: Iterable[DetectionMatch],
    destination: TextIO | str | PathLike[str],
    **kwargs: object,
) -> None:
    """Write the rendered HTML report to ``destination``.

    The ``destination`` may be a writable text stream or a filesystem path.  Any
    keyword arguments are forwarded to :func:`render_html_report`.
    """

    html = render_html_report(matches, **kwargs)
    if isinstance(destination, (str, PathLike, Path)):
        Path(destination).write_text(html, encoding="utf-8")
        return
    destination.write(html)
