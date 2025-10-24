# Core formats status — INI lineage audit

## Encoding & secret hygiene verification
- ✅ `IniPlugin` v0.0.3 retains codec detection for UTF-8 BOM, UTF-16, and Latin-1 samples. Metadata now records `encoding_info` alongside the unified `detector_lineage` payload so downstream tooling can trace the decision path.
- ✅ Sensitive key discovery writes structured `secret_classification` entries (credential, token, key-material) and surfaces `security_focus_keys` for reviewer triage.
- ✅ Remediation stubs emit per-category rotation guidance plus the `env-sanitisation-workflow` hook that links to `scripts/fixtures/README.md` for scrub-ready dotenv fixtures.

## Fixtures & sanitisation trail
- Dotenv fixtures in regression tests depend on scrub scripts documented under `scripts/fixtures/README.md`. The remediation metadata points reviewers/operators at the same path to keep shared samples sanitised.
