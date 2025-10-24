"""Compatibility facade for newline-delimited JSON reporting helpers.

Historically the JSON helpers lived in :mod:`driftbuster.reporting.json`.
Area ``A11`` in ``CLOUDTASKS`` now tracks a dedicated JSON lines emitter,
so the implementation resides in :mod:`driftbuster.reporting.json_lines`.
The old module is kept as a thin re-export to avoid breaking imports in
downstream automation while letting new code depend on the clearer module
name.
"""

from __future__ import annotations

from .json_lines import iter_json_records, render_json_lines, write_json_lines

__all__ = ["iter_json_records", "render_json_lines", "write_json_lines"]

