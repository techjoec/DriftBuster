# Plugin Test Checklist

This checklist standardizes tests for new and existing format plugins under
`gui/DriftBuster.Backend/Detection/Plugins/`, with tests in
`gui/DriftBuster.Backend.Tests/Detection/Plugins/`. Validate behavior end‑to‑end
where practical and keep the merged coverage gate (83% total line) green.

Recommended test cases:

- Detection basics
  - Detects the intended format with a minimal, valid sample.
  - Returns `DetectionMatch` with `FormatName`, `Variant` (if any) and `Reasons`.
  - `Confidence` is within the expected range and stable across typical inputs.

- Extension as hint (not a gate)
  - Extension alone must not trigger detection. Content signals should gate.
  - Cover with-and-without-extension cases (confidence nudge vs content-only).

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

- Review flags (if applicable)
  - Plugins surface oddities via `metadata.needs_review` and
    `metadata.review_reasons` (e.g., JSON parse failed, XML not well-formed,
    YAML tabs, TOML trailing commas, INI malformed sections).

- Security‑safe parsing
  - Parsers read scanned files with external resolution and entity expansion off (XML goes
    through `SafeXml`); cover inputs that must not expand or fetch anything (a DOCTYPE that
    declares entities, an external DTD reference).

- Error handling and resilience
  - Gracefully handles empty input, binary garbage, and truncated documents.
  - No unhandled exceptions for expected bad inputs.

- Redaction (if applicable)
- Sensitive values detected by the plugin are represented with placeholders
  (or integration tests verify redaction during reporting).

- Definition manifests (if applicable)
  - For plugins that detect definition files (e.g., `registry-live`), include
    tests that parse minimal JSON and heuristic YAML, validate extracted
    metadata keys (token/keywords/patterns), and reject unrelated inputs.

Test scaffolding tips:

- Centralize sample builders (small helpers that return well‑formed vs. malformed
  content). Keep samples concise and focused on the condition under test.
- Prefer plain strings/bytes and run through the detector via
  `Detector` (`gui/DriftBuster.Backend/Detection/Detector.cs`) to emulate realistic calls. For internal
  branches that are hard to reach, call internal helpers directly with clear
  intent.
- For platform‑specific details (e.g. XML secure paths), use the internal test
  seams rather than relying on the host platform.

Verification targets:

- The merged coverage gate (`scripts/verify_coverage.sh`) stays green.
- Negative tests are present (not just “happy path”).
- Sampling limits are explicitly tested once.
- At least one security/robustness test (malformed input) is included.
