# Plugin Test Checklist

This checklist standardizes tests for new and existing format plugins under
`src/driftbuster/formats/`. Keep per‑file coverage ≥ 90% and validate behavior
end‑to‑end where practical.

Recommended test cases:

- Detection basics
  - Detects the intended format with a minimal, valid sample.
  - Returns `DetectionMatch` with: `format`, `variant` (if any), `reason`.
  - Score is within expected range and stable across typical inputs.

- Negative/near‑miss cases
  - Similar but invalid or malformed input does NOT produce a match.
  - Inputs that look like the format but contain sentinel blockers (e.g.
    doctype, entities) follow safe paths and either decline detection or skip
    unsafe parsing.

- Sampling and size limits
  - Large inputs are bounded by the plugin’s sampling window (no full‑file
    reads where not needed). Explicitly test at the sampling boundary.

- Metadata extraction
  - Extracted metadata keys are present and correct for typical inputs.
  - Namespaces/prefixes and root element attributes (where applicable).

- Security‑safe parsing
  - If a “defused” parser is supported (e.g. defusedxml), cover the secure
    branch and the fallback branch, including cases that disable parsing
    (e.g. inputs with DOCTYPE/ENTITY declarations).

- Error handling and resilience
  - Gracefully handles empty input, binary garbage, and truncated documents.
  - No unhandled exceptions for expected bad inputs.

- Redaction (if applicable)
  - Sensitive values detected by the plugin are represented with placeholders
    (or integration tests verify redaction during reporting).

Test scaffolding tips:

- Centralize sample builders (small helpers that return well‑formed vs. malformed
  content). Keep samples concise and focused on the condition under test.
- Prefer plain strings/bytes and run through the detector via
  `driftbuster.core.detector.Detector` to emulate realistic calls. For internal
  branches that are hard to reach, call internal helpers directly with clear
  intent.
- For platform‑specific details (e.g. XML secure paths), monkeypatch adapters
  rather than relying on optional dependencies being installed in the test env.

Verification targets:

- The plugin module’s file coverage ≥ 90%.
- Negative tests are present (not just “happy path”).
- Sampling limits are explicitly tested once.
- At least one security/robustness test (malformed input) is included.

