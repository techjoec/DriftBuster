# Reviewer Checklist

Use this sheet while reviewing pull requests or manual doc updates. Keep the
notes concise and link to the relevant checklist entry under `notes/` when you
log the run.

## Reference docs

- `docs/legal-safeguards.md` — vendor-neutral and anti-DRM guardrails.
- `docs/testing-strategy.md` — manual lint/test commands and deferred
  automation backlog.
- `docs/format-playbook.md` — detector workflow + diff/hunt checklist.

## Manual verification

```text
compileall: python -m compileall src
pycodestyle(core): python -m pycodestyle src/driftbuster/core
pycodestyle(formats/registry_live): python -m pycodestyle src/driftbuster/formats/registry_live
pycodestyle(registry helpers): python -m pycodestyle src/driftbuster/registry
```

Check off each line once you've read the output and confirmed no surprises.

## Review log

- [ ] Outputs stay vendor-neutral with no product names or proprietary snippets.
- [ ] Capture/report manifests avoid DRM-protected or binary-only payloads.
- [ ] Detector or doc changes keep formats universal without app-specific targeting.
- [ ] Manual lint/test commands executed (paste command output location here).
- [ ] Hunt/profile approvals recorded (`notes/checklists/hunt-profile-review.md`,
      `notes/checklists/profile-summary.md`).
- [ ] Legal review log updated (`notes/checklists/legal-review.md`).
- [ ] Follow-up TODOs (automation, backlog updates) mirrored in `CLOUDTASKS.md`.

Store this file locally if you want to annotate it; the committed copy should
remain a template.
