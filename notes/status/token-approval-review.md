# Token approval monthly review log

This log tracks the standing review of unresolved hunt tokens once the CLI
surfacing lands. Update it during the first Tuesday review window each month
(using the cadence documented in `notes/checklists/token-approval.md`).

## Cadence snapshot
- Run `python -m driftbuster.profile_cli pending-tokens <hunt.json> --approvals <approvals.json>`
  to generate the summary line and limited pending table.
- Append a new row to the monthly table with the run timestamp (UTC), the
  pending token count reported by the CLI, and any blockers or follow-up work.
- If the CLI highlights unresolved blockers, mirror them into Area A11.8 of
  `CLOUDTASKS.md` so the queue stays authoritative.

## Monthly tracker
| Month (YYYY-MM) | Run timestamp (UTC) | Pending tokens | Blockers / notes |
| --- | --- | --- | --- |
| 2025-11 | _pending first CLI run_ | _tbd_ | Capture CLI summary + follow-ups here. |

> Duplicate the last row and update the month heading when scheduling future
> reviews. Keep historical rows intact for auditing.
