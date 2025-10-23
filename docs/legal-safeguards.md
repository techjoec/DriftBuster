# Legal Safeguards

We keep the project lightweight and respect other creators:

1. **No vendor or app names**
   - Use neutral labels in docs, fixtures, and examples.
   - Replace real product names with generic placeholders.
2. **Universal formats only**
   - Focus on format-level behaviour; skip heuristics tied to branded apps.
   - Keep detector metadata generic so it applies across ecosystems.
3. **No DRM or binary cracking**
   - Do not import encrypted, DRM-protected, or proprietary binaries.
   - Skip tasks that involve reverse engineering closed formats.
4. **No reverse-engineered IP**
   - Build samples from public information or original work only.
   - Avoid copying configuration fragments that could expose private systems.

These guardrails cover every feature, note, and capture helper.

## GUI frameworks

- **WinUI 3 / Windows App SDK**
  - Include the Windows App SDK and WinUI acknowledgements plus the WebView2 Evergreen redistribution notice in the packaged NOTICE file.
  - Distribute the Microsoft WebView2 installer alongside offline bundles so operators can install the runtime without network calls.
  - Record redistribution package hashes in `artifacts/gui-packaging/` when preparing MSIX or portable bundles.
- **Tkinter**
  - Bundle the Python PSF licence text and Tcl/Tk copyright notice when shipping CPython runtimes.
  - Document any embedded CPython version inside the NOTICE manifest so security reviews can map CVE coverage quickly.
- **PySimpleGUI (Tk flavour)**
  - Provide the LGPLv3 licence text and a written offer (README reference) for source access if distributing modified wheels.
  - Track which PySimpleGUI artefact (official wheel vs patched build) ships so the notice stays accurate across releases.
- **Electron**
  - Maintain an enumerated list of bundled npm dependencies with licence identifiers in NOTICE; refresh it every release build.
  - Store checksum + SBOM outputs for the packaged Node modules alongside the release artefacts for audit.

## SQL snapshot safeguards

- Mask or hash sensitive columns using the CLI options documented in
  `fixtures/sql/README.md` before exporting.
- Store generated manifests (`sql-manifest.json`) and masked exports in a
  restricted directory with the same retention plan as the source evidence.
- Record the anonymisation choices inside `notes/status/gui-research.md` when
  sharing samples so reviewers understand what data was transformed.
- Keep checksum files under `artifacts/sql/` so downstream consumers can
  confirm the artefacts were not modified after approval.

## Retention

- Default retention window for database snapshots is **30 days** unless a
  documented investigation requires an extension. Extensions must include a new
  expiry date and the reason for holding the artefact.
- Track retention decisions in `notes/checklists/legal-review.md` alongside the
  scenario that produced the snapshot.
- Purge expired exports and their manifests, checksum files, and scratch
  directories. Record the purge completion in the same log entry.
- When sharing artefacts externally, duplicate them into a fresh directory and
  re-run masking to avoid reusing long-lived copies.

## HOLD Exit Briefing

- Check `notes/status/hold-log.md#decision-ready-summary` before resuming reporting work.
- Confirm the sample list in `docs/testing-strategy.md#hold-exit-checklist-hooks` sticks to the rules above and stays format-universal.
- Record HOLD clearance in the status log when the guardrails still hold true.
