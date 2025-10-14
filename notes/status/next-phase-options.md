# Next-Phase Option Grid (Post-HOLD)

| Option | Focus | Testing impact | Legal impact | Prerequisites |
| --- | --- | --- | --- | --- |
| Reporting adapters (A8) | Diff helper, JSON/HTML outputs, CLI flags | Expand manual matrix for diff/json/html outputs; ensure sample catalog stays vendor-neutral | Guardrail review per `docs/legal-safeguards.md`; confirm no branded data in stored diffs | Finalise diff canonicalisation design; confirm manual fixtures for HTML snapshots |
| Capture pipeline (A9) | Snapshot script, manifest, comparison helper | Manual end-to-end capture runs; add retention checklist steps | Manifest must omit operator secrets; storage retention policy approval | Reporting adapter availability; decision on storage location for manifests |
| Format backlog (A14) | Additional detectors beyond XML | Large sample acquisition; manual validation per new format heuristics | License review for every sample source; confirm anonymisation path | HOLD lifted with prioritised format list; sampler guardrail review | 
| Windows GUI exploration (A13 follow-up) | Prototype interactive shell for reports | Manual UI walkthrough; ensure diff outputs render correctly | Packaging/licensing audit; data residency for cached reports | Reporting outputs stabilised; decision on distribution mechanism |

## Option Notes
- **Reporting adapters** unblock downstream automation and provide clarity for auditors; requires decisions on diff canonicalisation (A8.1) and redaction hooks.
- **Capture pipeline** depends on reporting artifacts to compare snapshots meaningfully; manual storage guidance must stay within the vendor-neutral guardrails before scripts land.
- **Format backlog** remains a research-driven track; progress only once sample sourcing and legal approvals are refreshed.
- **Windows GUI** exploration can resume after reporting output stabilises to ensure UI prototypes have real data to consume.

## Shared Considerations
- Every option needs updated manual checklists; avoid automation until guardrails change.
- Expanding samples likely requires refreshing the catalog table in `docs/testing-strategy.md` with provenance notes.
- Coordinate with legal reviewer before publishing any new example outputs.
