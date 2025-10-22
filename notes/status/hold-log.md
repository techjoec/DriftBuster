# HOLD Check-ins

> **Gate:** Do not begin Areas A8 and beyond until this log records explicit user approval lifting HOLD.

## Weekly Check-ins
### Week of 2025-10-11
- Decision logged: user opted to keep reporting work (A8) on HOLD and continue core + XML iterations only.
- Need confirmation that existing sample catalog is sufficient for whichever path is chosen.
- Clarify whether manual lint/test policy changes are expected before A8 work proceeds.
- Status log now documents GUI user requirements for review (`notes/status/gui-research.md#user-requirements`).

### Decision-Ready Summary
| Blocker | Approval required | Notes |
| --- | --- | --- |
| Reporting adapter rollout plan | Awaiting future go-ahead; current focus stays on core + XML | Scope captured in `CLOUDTASKS.md` area A10; revisit once user authorises A8 resumption. |
| Capture manifest workflow | Sign-off that `scripts/capture.py` storage defaults keep outputs vendor-neutral | Ensure sample catalog in `docs/testing-strategy.md` matches the guardrails in `docs/legal-safeguards.md#hold-exit-briefing`. |
| Manual verification coverage | User reaffirmed manual-only compile/lint gate for current work | `notes/checklists/core-scan.md` checklist remains authoritative; continue logging command output before shipping core/XML changes. |

## User Responses
| Date logged | Inquiry | Response | Follow-up |
| --- | --- | --- | --- |
| — | — | — | — |

## Logging Procedure
1. Append a new weekly section when questions change or a new calendar week starts.
2. Record inbound responses in the table immediately; include date, short summary, and resulting action.
3. Only mark HOLD cleared when the response explicitly authorises starting A8 (update the gate notice accordingly).
