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

## JSON samples

- Clamp JSON detector analysis windows to 200 kB before running comment or
  brace heuristics so oversized vendor payloads never enter long-lived logs.
- Keep comment stripping ephemeral. Sanitise payloads in-memory for metadata
  only and discard the cleaned string once detection finishes.
- When recording validation evidence, summarise detector metadata instead of
  storing raw JSON; note when the analysis window truncated the sample.

## Binary fixtures

- Store generated SQLite, binary plist, and markdown front matter samples under
  `fixtures/binary/` with hashes recorded in `MANIFEST.json`.
- Regenerate the fixtures via `scripts/fixtures/binary/generate_samples.py` so
  the provenance script doubles as documentation.
- Keep placeholder values generic (environment labels, feature flags) and avoid
  importing third-party binaries or leaked production data.
- Reference the manifest entry when logging legal review updates so reviewers
  can verify digests quickly.

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

## Windows packaging guardrails

- **MSIX builds**
  - Bundle the generated MSIX with a matching `.appinstaller` manifest and SHA256 hash file so security teams can validate sideloaded packages.
  - Keep the signing certificate chain (issuer, thumbprint, expiry) recorded in `notes/checklists/legal-review.md` alongside each release entry.
  - Include the Windows App SDK, WinUI, and WebView2 redistribution notices inside the packaged `NOTICE` directory; update the file whenever dependencies change.
- **Portable/self-contained bundles**
  - Stage the WebView2 Evergreen offline installer (`MicrosoftEdgeWebView2RuntimeInstallerX64.exe`) and the .NET Desktop Runtime when shipping portable bundles so offline operators are not prompted to download components.
  - Publish hash manifests for every staged file (`*.exe`, `NOTICE`, `README`, dependency installers) into `artifacts/gui-packaging/publish-*.sha256` (or a sibling hashes file) and copy the manifest into the hand-off folder.
  - Document minimum OS requirements (Windows 10 1809+, x64) and disk footprint inside the operator hand-off notes to satisfy WebView2 redistribution terms.
- **Security evidence**
  - Store install/uninstall transcripts for each packaging flavour under `artifacts/gui-packaging/` (e.g., `publish-framework-dependent.log`, `publish-self-contained.log`) and reference them from the legal review log so auditors can trace environment parity.
  - Record any third-party dependency updates (e.g., WebView2 runtime version, Avalonia patch level) in `notes/status/gui-research.md` and refresh the NOTICE file before release builds.
  - Confirm that all redistributables shipped with the bundle allow offline redistribution and include their licence text within the package.

## Diff planner MRU storage

- Persist only sanitized summaries. MRU entries must never include raw file contents, secrets, or unmasked configuration values; the GUI enforces this by rejecting payloads where `payload_kind` resolves to `raw`.
- Store cache files under `%LOCALAPPDATA%/DriftBuster/cache/diff-planner/` (or the XDG data root). Operators may relocate the directory, but any alternate path must inherit the same restricted ACLs as the default location.
- Sanitized entries should cap at ten records and rotate automatically. Manual exports must mask timestamps, hostnames, and operator identifiers before sharing outside the local workstation.
- Record MRU telemetry samples (see `artifacts/logs/diff-planner-mru-telemetry.json`) when auditing sanitization behaviour and capture retention outcomes in `notes/checklists/legal-review.md`.

## SQL snapshot safeguards

- Mask or hash sensitive columns using the CLI options documented in
  `fixtures/sql/README.md` before exporting.
- Store generated manifests (`sql-manifest.json`) and masked exports in a
  restricted directory with the same retention plan as the source evidence.
- Record the anonymisation choices inside `notes/status/gui-research.md` when
  sharing samples so reviewers understand what data was transformed.
- Keep checksum files under `artifacts/sql/` so downstream consumers can
  confirm the artefacts were not modified after approval.

## XML namespace fixtures

- Use anonymised namespaces (`urn:example:*`) and placeholder assembly identities for XML samples stored under `fixtures/xml/`.
- Document namespace provenance (filename, declaration line numbers, hash previews) inside `fixtures/xml/README.md` so auditors can trace the recorded metadata.
- Avoid bundling vendor-specific schema files; reference public specifications or redact proprietary URIs before archiving.

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
