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
5. **No translated or adapted source**
   - Never translate, port, transcribe, or adapt another project's source code
     into this repository, whatever licence it carries: a rewrite in C# still
     carries the original copyright and its licence obligations.
   - Implement from public documentation and specifications instead — the .NET
     API documentation, published standards and algorithm papers, vendor format
     and protocol documentation.
   - Prefer .NET built-ins (`System.Text.RegularExpressions`, `TimeZoneInfo`,
     `DateTimeOffset`, `System.IO.Path`, `System.IO.Enumeration`, LINQ ordering)
     over a hand-written equivalent.
   - Record every new dependency in `THIRD-PARTY-NOTICES.txt` with its licence
     text or SPDX identifier and its copyright line.

These guardrails cover every feature, note, and capture helper.

## Catalog severity language

- Severity hints embedded in the detection catalog stay neutral: they describe
  risk categories without naming vendors or proprietary products.
- Remediation stubs reference internal documentation only (`docs/*` and
  scrub guides) so downstream operators do not treat them as legal mandates for
  third-party systems.
- Variant-specific severity copy (e.g., dotenv guidance) stays vendor-neutral
  and references internal documentation only.
- Keep future catalog updates aligned with this language review; deviations
  require re-approval before shipping.

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
- When a fixture changes, update its SHA-256 and size in `MANIFEST.json` in the
  same change so the manifest stays the provenance record.
- Keep placeholder values generic (environment labels, feature flags) and avoid
  importing third-party binaries or leaked production data.
- Reference the manifest entry when logging legal review updates so reviewers
  can verify digests quickly.

## Registry remoting safeguards

- Store registry remoting credentials in environment variables or credential
  profiles; never commit inline `password` fields to JSON or PowerShell calls.
- Limit explicit hive roots to the minimum scope required for the investigation
  and review manifests for the `requested_roots` trace before sharing evidence.
- When staging remote captures, keep the WinRM working directory under a
  restricted path (the module default is `$env:ProgramData\DriftBuster\RemoteScan`;
  each run removes its own staging folder) and archive manifests and
  `registry_scan.json` outputs before sharing.
- Run `driftbuster capture run --registry-scan ...` on secured workstations so
  registry summaries join filesystem manifests without copying raw hive exports.

## Bundled dependencies

- The GUI is Avalonia (Fluent theme, DataGrid, ItemsRepeater) with the embedded
  Inter font, CommunityToolkit.Mvvm and Velopack; the backend uses
  Microsoft.Data.Sqlite and YamlDotNet; the console tool uses System.CommandLine.
  Every redistributed component, including the Inter font and the native
  libraries inside SkiaSharp, HarfBuzzSharp and SQLitePCLRaw, is listed in
  `THIRD-PARTY-NOTICES.txt` with its licence and copyright line; refresh that
  file whenever package references change (versions live in the `.csproj`
  files). Licences that require their text to travel with the binaries (OFL for
  Inter, and the SkiaSharp/HarfBuzzSharp upstream notices covering FreeType,
  HarfBuzz, libpng and the rest) are reproduced in full in that same file, so a
  package upgrade means re-copying the upstream notices as well as the version.
- Self-contained publishes ship the .NET runtime, which the notices file covers.
- `LICENSE`, `NOTICE` and `THIRD-PARTY-NOTICES.txt` are copied into the GUI and
  console tool publish output and into the PowerShell module package.
- Record redistribution package hashes in `artifacts/gui-packaging/` when
  preparing MSIX or portable bundles.

## Windows packaging guardrails

- **MSIX builds**
  - Bundle the generated MSIX with a matching `.appinstaller` manifest and SHA256 hash file so security teams can validate sideloaded packages.
  - Keep the signing certificate chain (issuer, thumbprint, expiry) recorded in `notes/checklists/legal-review.md` alongside each release entry.
  - Ship `LICENSE`, `NOTICE` and `THIRD-PARTY-NOTICES.txt` inside the package; update the notices whenever dependencies change.
- **Portable/self-contained bundles**
  - Ship self-contained bundles, or stage the .NET 10 Desktop Runtime installer beside a framework-dependent bundle, so offline operators are not prompted to download components.
  - Publish hash manifests for every staged file (`*.exe`, `NOTICE`, `README`, dependency installers) into a hashes file beside the bundle and copy the manifest into the hand-off folder.
  - Document minimum OS requirements (Windows 10 1809+, x64) and disk footprint inside the operator hand-off notes.
- **Security evidence**
  - Keep the publish transcript for each packaging flavour with the release bundle (commands in `artifacts/gui-packaging/README.md`) and reference it from the legal review log.
  - Record any third-party dependency updates (e.g., .NET runtime version, Avalonia patch level) in the release notes (`notes/releases/<version>.md`) and refresh `THIRD-PARTY-NOTICES.txt` before release builds.
  - Confirm that all redistributables shipped with the bundle allow offline redistribution and include their licence text within the package.

## Run profile secret scanning safeguards

- Run profile executions must persist only scrubbed artefacts. Validate that captured files replace matched secrets with the `[SECRET]` placeholder before sharing evidence.
- Document every CLI or GUI override that adds secret ignore rules or patterns. Record the justification and reviewer in `notes/checklists/legal-review.md`.
- Treat the run metadata `secrets` block as restricted telemetry. Store it alongside the run output and never detach the findings list from its redacted files.
- When reviewing manual redactions, diff the captured snippets against the original sources to confirm masking preserved surrounding context without leaking the secret value.

## Diff planner MRU storage

- Persist only sanitized summaries. MRU entries hold file paths, a display name, `payload_kind` and a sanitized digest; they never hold file contents, secrets, or unmasked configuration values.
- Store cache files under `%LOCALAPPDATA%/DriftBuster/cache/diff-planner/` (or the XDG data root). Operators may relocate the directory, but any alternate path must inherit the same restricted ACLs as the default location.
- Sanitized entries should cap at ten records and rotate automatically. Manual exports must mask timestamps, hostnames, and operator identifiers before sharing outside the local workstation.
- Record MRU telemetry samples (the GUI writes `logs/diff-planner-telemetry.json` under the data root at runtime) when auditing sanitization behaviour and capture retention outcomes in `notes/checklists/legal-review.md`.

## SQL snapshot safeguards

- Mask or hash sensitive columns using the CLI options documented in
  `fixtures/sql/README.md` before exporting.
- Store generated manifests (`sql-manifest.json`) and masked exports in a
  restricted directory with the same retention plan as the source evidence.
- Record the anonymisation choices in a README beside the shared samples so
  reviewers understand what data was transformed.
- Keep checksum files under `artifacts/sql/` so downstream consumers can
  confirm the artefacts were not modified after approval.

## XML namespace fixtures

- Use anonymised namespaces (`urn:example:*`) and placeholder assembly identities for XML samples stored under `fixtures/xml/`.
- Document namespace provenance (filename, declaration line numbers, hash previews) inside `fixtures/xml/README.md` so auditors can trace the recorded metadata.
- Avoid bundling vendor-specific schema files; reference public specifications or redact proprietary URIs before archiving.

## Retention

- Default retention window for database snapshots and reporting artefacts is
  **30 days** unless a documented investigation requires an extension.
  Extensions must include a new expiry date, responsible owner, and the reason
  for holding the artefact.
- Run `driftbuster maint purge-reporting-retention captures/ artifacts/reporting/`
  every review cycle to list purge candidates (`--retention-days` defaults to 30). Re-run with `--confirm` only
  after updating `notes/checklists/legal-review.md` with the planned deletions
  and the operator initials approving the purge.
- During manual audits, spot-check JSON/HTML/diff outputs for the `[REDACTED]`
  placeholder before deleting artefacts. Record the filenames inspected,
  confirm placeholder usage, and store the evidence summary in the legal review
  log.
- Track retention decisions in `notes/checklists/legal-review.md` alongside the
  scenario that produced each artefact and note when purge scripts were
  executed.
- Purge expired exports plus their manifests, checksum files, and scratch
  directories. Record the purge completion in the same log entry along with the
  command that performed the deletion.
- When sharing artefacts externally, duplicate them into a fresh directory and
  re-run masking to avoid reusing long-lived copies.
