# Python CLI Plan (Legacy)

> **Note:** The Windows-first pivot moves CLI work to the PowerShell module
> backed by the shared .NET backend. This document remains for historical
> context while the Python CLI stays on hold.

Purpose: Outline how a future Python entry point would wrap the detector once
core stabilization (A1–A3) finishes.

## Goals

- Provide a `driftbuster` command that can scan a path, emit JSON output, HTML
  summaries, and diff artefacts while optionally filtering by format/variant.
- Keep dependencies limited to the standard library (argparse, pathlib)
  unless a clear justification emerges.
- Support manual validation workflows (no automated test harness).

## Proposed Structure

- Entry point defined in `pyproject.toml` (`[project.scripts] driftbuster =
  "driftbuster.cli:main"`).
- New module `src/driftbuster/cli.py` with:
  - `parse_args(argv: Sequence[str]) -> Namespace`
  - `run_scan(path: Path, glob: str, json_output: bool, plugins: Optional[str])`
  - `main(argv: Sequence[str] | None = None) -> int`
- Default output: human-friendly table summarizing matches; `--json`
  switch dumps structured metadata using the reporting adapter helpers.
- `--html` writes the rendered HTML report to the provided path (or STDOUT when
  omitted) using `render_html_report` with hunt and profile context.
- `--diff BEFORE AFTER` renders canonicalised diffs via
  `build_unified_diff`/`render_unified_diff` with masking enabled.
- Detector defaults should surface `_DEFAULT_SAMPLE_SIZE` explicitly in the CLI
  help so users understand the 128 KiB clamp.

### Arguments

| Flag | Description |
|------|-------------|
| `path` | Root path to scan (positional) |
| `--glob PATTERN` | Override glob pattern (`**/*` default) |
| `--json` | Emit JSON lines (one record per match + profile summary + hunt hits) |
| `--html PATH` | Write HTML report (detects STDOUT when PATH=`-`) |
| `--diff BEFORE AFTER` | Print unified diff between snapshots |
| `--sample-size N` | Override sampling bytes (pass to `Detector`, clamped to the 512 KiB guardrail) |
| `--plugins module:Class,...` | Optional plugin override list for power users |
| `--hunt` | Enable hunt mode using default rules (writes to STDOUT/JSON) |
| `--hunt-rule PATH` | Load additional hunt rule definitions (TOML/JSON) |

## Packaging Checklist

1. Add `cli.py` module and entry point to `pyproject.toml`.
2. Update `README.md` with CLI usage examples once implemented, including diff
   and HTML invocations that demonstrate redaction placeholders.
3. Bump project version once CLI ships.
4. Manual validation: run CLI against sample tree, capture output (JSON/HTML/
   diff) in repo notes alongside redaction stats.

## Manual Validation Plan

- `driftbuster ./fixtures/sample` — ensure table output lists path, format,
  variant, confidence.
- `driftbuster ./fixtures/sample --json` — verify JSON lines emit detection,
  profile, and hunt records with masked tokens.
- `driftbuster ./fixtures/sample --html report.html` — open locally and confirm
  summary tables, diff snippets, and hunt badges render with inline assets only.
- `driftbuster --diff baseline.json current.json` — ensure canonical diff stats
  match manual expectations and placeholders replace tracked tokens.
- `driftbuster ./fixtures/sample --glob "**/*.config"` — confirm filtering.
- Document commands + results in the relevant `CLOUDTASKS.md` checklist before
  shipping.

## Open Questions

- Should we include a `--confidence-threshold` flag to filter noise?
- Is a progress indicator needed for large trees, or do we rely on STDOUT
  logging? (Default assumption: keep output quiet unless `--verbose` is added
  later.)
- Packaging: editable installs only at first, or publish to PyPI once the
  format catalog is fleshed out?
