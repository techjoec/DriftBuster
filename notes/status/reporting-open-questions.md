# Reporting open question register

This register captures the remaining decisions, guardrails, and follow-up
triggers for the reporting stack now that Area A11 implementation work has
landed. Use it when triaging token approvals, diff payload safety, and future
CLI surfacing so the backlog stays aligned with the canonical documentation.

## Snapshot summary

| Topic | Status | Resolution / notes | Follow-up owner |
| --- | --- | --- | --- |
| A11.8.1 – Diff safety thresholds | **Resolved** (2025-11-21) | 256 KiB canonical buffer and 128 KiB / 600 line diff clamps enforced in `reporting.diff`; HTML/CLI renderers emit safety digests. | Re-run clamps if new formats exceed guardrails. |
| A11.8.2 – Canonicalisation options | **Resolved** (2025-11-16) | `canonicalise_json` sorts keys and feeds unified diff + adapter pipelines; regression tests cover ordering stability. | Raise new variants in this register before extending canonicalisers. |
| A11.8.3 – Token approval storage | **Resolved** (2025-11-21) | JSON + SQLite stores shipped via `TokenApprovalStore`; checklist updated with schema guidance. | Keep schema additions backward compatible; log changes here first. |
| A11.8.4 – Pending token CLI surfacing | **Planned** | `docs/hunt-mode.md` describes `pending-tokens` CLI output plus noise controls. Implementation deferred until CLI activation resumes. | Ownership: CLI activation lead (see `notes/status/cli-plan.md`). Update status once parser lands. |
| A11.8.5 – Monthly review cadence | **Active** | Cadence mirrored in `notes/checklists/token-approval.md` and `notes/status/token-approval-review.md`. | Compliance reviewer rotating monthly. Document skips/blocks here. |

## Usage

1. When reviewers discover a new reporting open question, add a subsection below
   with the date, summary, and planned resolution. Mirror the high-level entry
   into Area A11.8 of `CLOUDTASKS.md` until implemented.
2. When a question is resolved, update the table above with the completion date
   and move the detailed subsection into the **Closed questions** list while
   keeping links to code/tests/docs that landed.
3. During the first-Tuesday cadence run, log whether any new blockers surfaced.
   If none, append a one-line confirmation referencing the monthly tracker in
   `notes/status/token-approval-review.md`.

## Detailed notes

### Pending token CLI surfacing (A11.8.4)
- Last updated: 2025-11-24
- Current status: CLI plan documented; implementation queued behind Area A18
  activation.
- Guardrails:
  - Ensure quiet/default modes respect operator noise budgets before exposing
    unresolved token counts.
  - Require regression fixtures covering noisy hunts vs. constrained output.
- Next steps: Open a new CLOUDTASKS area once CLI activation resumes to cover
  parser wiring, tests, and docs.

### Monthly review cadence (A11.8.5)
- Last updated: 2025-11-24
- Cadence: First Tuesday UTC review aligning with token approval checklist.
- Evidence: Record CLI run outputs in `notes/status/token-approval-review.md`
  and include blockers + remediations in this register when they appear.
- Escalation: If two consecutive reviews report blockers, raise a dependency on
  Area A18 before the third run.

## Closed questions

### Diff safety thresholds (A11.8.1)
- Resolved: 2025-11-21
- Implementation: `src/driftbuster/reporting/diff.py` enforces clamp sizes and
  surfaces digests consumed by adapters.
- Coverage: `tests/reporting/test_diff_masking.py` exercises oversize payload
  handling and digest output expectations.

### Canonicalisation options (A11.8.2)
- Resolved: 2025-11-16
- Implementation: `canonicalise_json` sorts keys before diff generation.
- Coverage: JSON diff fixtures ensure deterministic ordering across runs.

### Token approval storage backend (A11.8.3)
- Resolved: 2025-11-21
- Implementation: `TokenApprovalStore` now dumps/loads JSON and SQLite.
- Documentation: Checklist updates outline schema, storage guidance, and
  rotation expectations for reviewers.

