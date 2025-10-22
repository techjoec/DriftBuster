# CLOUDTASKS.md — Active Work Template

<!--
Schema reference
- H#.<area> [deps=] — Area title
  **REASON:** Why this area matters for near-term shipping.
  **MUST NOT:** Hard stops (what to avoid while working this area).
  **MUST:** Non-negotiable requirements.
  **ACCEPT GATES:** Conditions that must be true before marking the area complete.
  **REQUIRED RELATED WORK:** ≥4 concrete subpaths (files/modules/tests) agents must advance. Use hierarchical numbering (1.1, 1.1.1…) for nested subtasks.
- Append new areas at the top. Move finished areas to `CLOUDTASKS-COMPLETED.md` using the same schema.
- Task IDs (`T-xxxxxx`) stay in CLOUDTASKS.md; cross-reference them inside subtasks when relevant.
-->

## A1. <Area title> [deps=]

**REASON:** Summarize why this area advances the current milestone and why it matters right now.

**MUST NOT:** List redlines that would invalidate success (e.g., regressions, scope creep, missing reviews).

**MUST:** Call out the non-negotiable outcomes, deliverables, or behaviors required for completion.

**ACCEPT GATES:** Detail measurable conditions that must be verified before moving the area to `CLOUDTASKS-COMPLETED.md`.

**REQUIRED RELATED WORK:**
- [ ] 1.1 Describe the primary implementation slice and reference the exact files/modules/tests it touches (`path/to/file.ext`).
  - [ ] 1.1.1 Break down the first nested task with concrete outputs and file scopes (`path/to/file.ext`).
  - [ ] 1.1.2 Continue enumerating nested tasks as needed with clear owners/files/tests.
- [ ] 1.2 Capture the validation work (tests, scripts, manual checklists) required to certify the area.
- [ ] 1.3 Add supporting or follow-up tasks that unblock or complete the area.

## A2. <Next area title> [deps=A1]

**REASON:** Explain the dependency chain and the user/business/system value delivered by continuing here.

**MUST NOT:** Document pitfalls or regressions that must be avoided while executing this area.

**MUST:** Enumerate the mandatory behaviors, coverage, or assets that define success for this scope.

**ACCEPT GATES:** List checklist items that reviewers can verify to approve completion, including analytics, accessibility, or operational gates.

**REQUIRED RELATED WORK:**
- [ ] 2.1 Outline the implementation work and cite the affected repo paths (`path/to/module.js`).
- [ ] 2.2 Capture UX/Docs/Infra follow-through tied to this area (e.g., `docs/feature.md`, `scripts/deploy.sh`).
- [ ] 2.3 Note integration or contract updates needed to keep dependent surfaces in sync (`api/contracts/*.json`).
- [ ] 2.4 Identify validation artifacts (tests, dashboards, checklists) and where they live (`tests/module.spec.ts`).

## A3. <Next area title> [deps=]

**REASON:** Use this slot for an upcoming or parallel track; indicate its readiness or blockers.

**MUST NOT:** Record constraints or risky shortcuts that agents should avoid when picking up this queue item.

**MUST:** Specify the commitments or measurable outputs required before closing the loop on this track.

**ACCEPT GATES:** Set the review/QA/launch confirmations that must exist to archive the area, including linked evidence.

**REQUIRED RELATED WORK:**
- [ ] 3.1 Summarize the first execution step and map it to repo assets (`src/...`).
- [ ] 3.2 Add any cross-functional coordination (reviews, sign-offs, syncs) with responsible parties.
- [ ] 3.3 Capture supporting migrations, scripts, or infrastructure changes tied to the delivery.
- [ ] 3.4 State the validation and monitoring actions required post-merge.


# End of priority queue
<!-- PR prepared: 2025-09-25T23:45:35.865647+00:00 -->
<!-- make_pr anchor -->
