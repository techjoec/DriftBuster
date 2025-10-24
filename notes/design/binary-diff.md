# Binary & Hybrid Diff Adapter Blueprint

## Objective
Design binary- and hybrid-format diff adapters that extend DriftBuster reporting so structured
metadata stays reviewable even when the source artifact mixes binary segments with embedded
text (e.g. SQLite catalogues, Apple property lists, markdown files with binary attachments).

## Scope & Constraints
- Keep adapters within the existing `src/driftbuster/formats` plugin architecture.
- Preserve the 90% per-file coverage requirement by isolating IO-bound helpers for injection.
- Do not introduce platform-specific dependencies; rely on pure Python or optional extras.
- Avoid leaking raw binary payloads in reports. Use deterministic sampling and redaction flags.
- Respect retention guardrails: all temporary decoding outputs must be stored under `artifacts/`
  with manifest entries for future auditing.

## Supported Artifact Classes
1. **Embedded SQL containers** (SQLite, DuckDB): extract schema + logical rows for diffing while
   hashing untouched blobs.
2. **Property list bundles** (binary `.plist` and `.mobileconfig`): normalise to canonical JSON
   while tracking binary-only nodes for reviewers.
3. **Hybrid markdown** with front matter or appended binary payloads: expose YAML metadata,
   preserve markdown diff, and flag binary tail segments as elided evidence.

Future additions (e.g. OOXML, Protobuf) should reuse the extension points defined here without
changing adapter contracts.

## Architecture
```
formats/
  binary/
    __init__.py         # registry + adapter factory
    sqlite.py           # SQL container inspection (uses stdlib sqlite3)
    plist.py            # Property list decoding via plistlib/biplist fallback
    markdown_hybrid.py  # Markdown + binary tail parsing
reporting/
  diff.py               # Exposes BinaryDiffRender plan + masking helpers
scripts/
  fixtures/binary/      # Repro scripts for anonymised samples
```

### Adapter Lifecycle
1. Detector identifies candidate file via catalog metadata (`catalog.py` extension required by
   task 8.5.1).
2. Adapter loads canonical representation and yields `FormatDiffResult` with:
   - `metadata`: decoding summary (hashes, codecs, row counts).
   - `structure`: ordered nodes for diff rendering.
   - `binary_segments`: offsets + reason codes for redacted regions.
3. `reporting.diff.build_binary_diff` (new helper) compares canonical nodes and produces a mixed
   textual/binary summary, emitting redaction tokens when necessary.
4. CLI/GUI renderers display textual diff plus a table of binary segments with reviewer guidance.

### Error Handling
- Gracefully degrade when decoding fails: emit `binary_segments` entries marked `decode-error`
  with captured exception metadata (type, message) but without raw payload.
- Use structured logging hooks so retention/audit tasks can track anonymised failures.

## Testing Strategy
- Create deterministic fixtures covering:
  - SQLite database with embedded BLOB column changes.
  - Binary plist toggling nested keys.
  - Markdown file where binary tail flips size/hash.
- Add regression tests under `tests/formats/test_binary_adapters.py` asserting:
  - Canonical representations normalise ordering and ensure deterministic hashes.
  - Diff helper redacts binary segments while surfacing metadata.
  - Error paths tag failures without leaking payloads.
- Extend `tests/reporting/test_diff_masking.py` to validate binary masking tokens once adapters
  land (ties into task 8.5.3).

## Fixture & Script Requirements
- Store anonymised samples under `fixtures/binary/` with README describing provenance checks.
- Provide generation scripts under `scripts/fixtures/binary/` that:
  - Create synthetic SQLite DBs with seeded random data.
  - Produce plist fixtures using Python's plistlib with controlled key ordering.
  - Assemble markdown hybrids from text templates + dummy binary blobs.
- Each script must emit a manifest (`manifest.json`) capturing SHA-256 hashes and redaction
  rationale so legal review has clear traceability.

## Reporting Integration
- Extend `reporting/diff.py` with `build_binary_diff` returning both textual hunks and a
  structured list of binary evidence records (`BinaryDiffEvidence`).
- Update diff adapters to respect existing masking flags so downstream outputs (HTML, CLI) can
  highlight redacted segments without rehydrating blobs.
- Ensure CLI summary surfaces:
  - Binary segment counts per file.
  - Evidence paths for extracted metadata stored in `artifacts/`.

## Documentation & Evidence
- Update `docs/legal-safeguards.md` with redistribution rationale before shipping adapters.
- Record anonymisation walkthroughs and hashes in `notes/checklists/legal-review.md`.
- Capture rendered diff samples (textual output + binary evidence table) under
  `artifacts/formats/binary/` to support audits.

## Open Questions
1. Can we share a lightweight hexdump visualiser without exceeding retention policies?
2. Should SQLite adapters optionally expose schema-only diffs when row churn is high?
3. Do we need per-format toggles allowing operators to suppress binary evidence exports entirely
   in high-sensitivity environments?

Document owners should revisit these questions when implementing task 8.5.1 and coordinate with
legal review before enabling any optional exports.
