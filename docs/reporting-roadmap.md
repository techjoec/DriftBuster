# Reporting & Output Roadmap

This document tracks the forthcoming reporting stack that layers on top of the
core detector.

## HOLD Prerequisites

- Confirm HOLD blockers cleared in `notes/status/hold-log.md#decision-ready-summary` before touching adapters.
- Ensure reports stay vendor-neutral and avoid product-specific naming as noted in `docs/legal-safeguards.md#hold-exit-briefing`.
- Keep the manual compile/lint commands in `notes/checklists/core-scan.md` as the gating workflow until automation returns.
- Capture defaults in `scripts/capture.py` should align with the neutral-sample guidance captured in `docs/testing-strategy.md#hold-exit-checklist-hooks` prior to resuming A8 work.

## Output Targets

- **JSON lines:** machine-readable output for automation pipelines. Mirrors
  `DetectionMatch.to_dict()` and appends profile metadata when available.
- **HTML summary:** lightweight, static report highlighting key detections,
  profile hits, and drift indicators (no external assets required).
- **Diff/Patch view:** textual diff of configuration snapshots (line endings,
  tag ordering, whitespace/format drift). Backed by the same sampling logic so
  runs stay deterministic.
- **CLI adapters:** extend the future `driftbuster` command with flags such as
  `--json`, `--html`, and `--diff` to emit the above formats.
- **GUI (Windows-first, low priority):** simple desktop shell that renders the
  HTML/JSON output. Scope discussion pending once CLI + reports stabilize.
- **Hunt integration:** include dynamic value findings (server names, etc.) in
  JSON/HTML reports so operators can confirm expected per-server state.
- **Token transformation:** design a pipeline that can mark confirmed dynamic
  values and later feed them into config templates or patch generators (future).

## Metadata Consumption

- All adapters consume ``summarise_metadata`` outputs so `catalog_version`,
  `catalog_format`, and `catalog_variant` remain consistent regardless of
  presentation layer.
- JSON emitters must store the metadata blob verbatim alongside the rendered
  match for later auditing; HTML views summarise the same keys but keep the raw
  dictionary available in an expandable panel.
- Diff tooling needs to compare before/after metadata dictionaries to highlight
  guardrail hits (e.g., sample truncation, encoding shifts) in addition to file
  content changes.

## Diff Canonicalisation & Masking

- `driftbuster.reporting.diff.canonicalise_text` normalises newlines, strips
  trailing spaces, and keeps content ordering intact so text-only deltas avoid
  noise from platform-specific encodings.
- XML payloads run through `canonicalise_xml`, which trims insignificant
  whitespace, sorts attribute keys, and serialises via ElementTree so spacing
  changes stop generating diff churn.
- `build_unified_diff` returns a `DiffResult` object that includes canonical
  inputs, unified diff output, and aggregate stats (added/removed/changed line
  counts). `render_unified_diff` keeps the legacy string helper for CLI usage.
- Redaction flows through `resolve_redactor`: callers pass explicit
  `RedactionFilter` instances or `mask_tokens` sequences (often sourced from
  approved hunt hits). Every diff line is masked before diff generation to
  guarantee placeholders show up in stats and HTML badges.
- When canonical inputs already contain placeholders such as `{{ token_name }}`
  the diff helper tags them as `expected_token=True` so HTML/JSON adapters can
  style them differently from unknown secrets.
- If a rule expects a token but the placeholder is missing, mark the diff entry
  as `unresolved_token` and fall back to the standard redaction string
  (`[[TOKEN:token_name]]`) so reviewers can spot gaps without leaking data.
- Redaction filters treat both raw values and token placeholders uniformly; the
  same `RedactionFilter` entry should mask the literal value and any
  ``[[TOKEN:...]]`` surrogate so downstream tooling cannot accidentally surface
  sensitive context.
- Hunt integration: when the CLI surfaces hunt hits, the same token list feeds
  the JSON and HTML adapters so placeholders stay consistent across detections,
  diffs, and hunt excerpts. Guardrails from `docs/legal-safeguards.md` still
  apply—keep outputs free of vendor identifiers and proprietary snippets.

## Core Diff Blueprint

- `src/driftbuster/core/diffing.py` now hosts a HOLD-safe blueprint capturing
  the target diff interface (`DiffPlan`, `build_diff_plan`, `plan_to_kwargs`).
- Manual scripts should log the planned kwargs and execute the diff via
  `driftbuster.reporting.diff.build_unified_diff` until implementation lands.
- Checklist references live in `notes/snippets/xml-config-diffs.md` and
  `notes/checklists/reporting-tests.md`; update both when blueprint parameters
  evolve.
- The placeholder `execute_diff_plan` raises `NotImplementedError` intentionally
  as a guardrail—only manual validation should call into reporting helpers for
  now.

## Token Replacement API Design

- Draft `collect_token_candidates(hunt_payload, approvals)` returning a
  read-only mapping of `token_name` → `TokenCandidate` (rule metadata, latest
  excerpt hash, source paths).
- Define `prepare_token_replacements(snapshot, candidates, *, strategy)` which
  yields replacement instructions (path, token_name, placeholder, fallback
  behaviour). `strategy` defaults to "redact" and may later grow options such as
  "inject-placeholder" once automation is approved.
- The API surfaces pure data structures (dataclasses or typed dictionaries) so
  manual scripts can apply replacements without hidden I/O.
- Emit dry-run output: list files that would change, unresolved tokens, and any
  approvals missing metadata. This keeps manual workflows predictable until a
  CLI wrapper exists.

### Token metadata requirements

- `token_name`: canonical placeholder identifier shared with hunt rules and
  configuration profiles.
- `source_rule`: origin hunt rule ID so auditors can trace regex coverage.
- `value_hash`: checksum of the approved secret (stored outside the repo) for
  tamper detection without copying the value.
- `last_confirmed`: UTC timestamp from the approval log.
- `approved_by`: reviewer identifier (initials or handle) recorded in the
  checklist.
- `sensitivity`: low/medium/high flag controlling redaction emphasis in reports.

### Outstanding design questions

- Where should long-term approval records live once automation arrives (JSON
  manifest vs. lightweight SQLite DB)?
- How should expired approvals behave—warn only, or block substitution entirely?
- Can the same API cover multi-value tokens (arrays) without complicating
  manual review steps?

## Automation & Capture Pipeline

1. **Capture step:** run scans on a schedule (manual trigger for now) and write
   JSON snapshots to a designated directory (timestamped).
2. **Comparison step:** load previous snapshot, compute diffs (format + value)
   using the diff adapter, and flag changes.
3. **History log:** append metadata (hostname, branch, commit, operator) to a
   manifest for audit trails.
4. **Manual verification:** operators review diff/HTML outputs to confirm drift
   before acting.

These steps will live alongside CLI tooling with optional helper scripts. No
automated upload/storage is planned until legal/compliance review completes.

### Capture storage layout

- Manual runs use `scripts/capture.py run` and default to storing artefacts in a
  `captures/` directory beside the repository root.
- Each run writes `<capture-id>-snapshot.json` (full record) and
  `<capture-id>-manifest.json` (counts + timings). Override the directory with
  `--output-dir` when storage needs to sit elsewhere.
- The snapshot embeds relative paths so auditors can move the capture directory
  without breaking later comparisons.

### Manifest metadata requirements

- The manifest records the environment label, operator name, host, capture
  reason, and UTC timestamp gathered from CLI flags (`--environment`,
  `--operator`, `--reason`).
- Duration buckets cover the detection phase, hunt scan, and the overall run so
  slowdowns are traceable without reprocessing raw payloads.
- Redaction stats (placeholder, token count, total replacements) confirm the
  masking policy without echoing sensitive values.
- Profile totals surface from the associated snapshot so downstream comparison
  scripts avoid rehydrating the full payload unless deeper analysis is needed.

### Cleanup workflow

- Retain only the most recent set of manifests/snapshots per environment unless
  an investigation remains open.
- When deleting older captures, remove both the snapshot and manifest together
  and log the purge in the legal review notes.
- Operators must confirm that external copies (shared drives, removable media)
  are also deleted before closing the review.

## Compliance Guardrails

- Keep reports free of vendor names, product branding, and proprietary payloads
  so every output stays anonymous and shareable.
- Skip distributing binary dumps or DRM-protected content; focus on detector
  metadata and summaries instead.
- Reference `docs/legal-safeguards.md` for the high-level policy.

## Retention & Disposal

- Store JSON snapshots and HTML/diff artefacts in restricted locations only.
- Retain capture directories for a maximum of 30 days unless a review remains
  open.
- When a review closes, securely delete the snapshot files and manifests;
  document the purge in the legal review log.

## Manual Audit Steps

1. Generate outputs using the reporting adapters with the appropriate
   redaction tokens.
2. Inspect the JSON, HTML, and diff artefacts to confirm the placeholder is
   present wherever sensitive content previously appeared.
3. Write a snapshot manifest with ``write_snapshot`` including operator and
   classification metadata.
4. Record the run in `notes/checklists/legal-review.md`, noting any anomalies or
   follow-up questions for legal review.

## Open Questions

- How much configuration content can we safely surface in diffs without risking
  sensitive data leaks?
- Should the diff logic operate on canonicalized representations (e.g., sorted
  keys) to reduce noise?
- Do we need pluggable storage for capture manifests (SQLite vs. JSON files)?
- What storage format best fits the token approval log once automated
  substitution begins?
- How do we surface unresolved tokens in the CLI without overwhelming manual
  reviewers?
