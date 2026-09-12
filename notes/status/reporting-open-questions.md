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

## Usage

1. When reviewers discover a new reporting open question, add a subsection below
   with the date, summary, and planned resolution. Mirror the high-level entry
   into Area A11.8 of `CLOUDTASKS.md` until implemented.
2. When a question is resolved, update the table above with the completion date
   and move the detailed subsection into the **Closed questions** list while
   keeping links to code/tests/docs that landed.

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

