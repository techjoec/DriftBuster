# Reporting adapters checklist

- **Before running:** gather approved redaction tokens (from hunt hits or legal
  guidance) and confirm baseline/current snapshots live outside the repository.
- **Diff helper verification:**
  - Use the curated "good" and "bad" config pair stored in the secure sample
    share.
  - Log both the `DiffPlan` and `plan_to_kwargs` output via
    `driftbuster.core.diffing`. Feed the resulting kwargs into
    `build_unified_diff` manually so the blueprint stays in sync.
  - Combine every generated `DiffResult` with
    `driftbuster.reporting.diff.summarise_diff_results` and store the JSON
    payload alongside the raw diff to confirm reviewers get the bundled view.
  - Paste the unified diff output into this checklist and record whether
    placeholders replaced every tracked token.
- **JSON lines export:**
  - Call `render_json_lines` (or the future CLI `--json`) with detection
    matches, `ProfileStore.summary()`, and hunt results.
  - Confirm the emitted records include `type=detection`,
    `type=profile_summary`, and `type=hunt_hit` entries in sequence.
  - Note the placeholder token counts reported by the redaction filter.
- **HTML report review:**
  - Render `render_html_report` with the same payloads and open the file in a
    local browser (offline mode).
  - Verify the detection summary table, diff section, hunt badges, and redaction
    summary align with the JSON data.
  - Capture screenshots or textual descriptions of anomalies in this checklist.
- **Registry summary usage review:**
  - Call `registry_summary()` from `src.driftbuster.registry` with a recorded
    `RegistryStore` snapshot after a representative multi-server run.
  - Confirm the summary payload lists `usage.statistics` with the expected
    counters (`totalServers`, `totalProfiles`, `activeProfiles`,
    `detectorInvocations`).
  - Cross-check the counters against the captured run artefacts (diff JSON,
    profile exports) and log the evidence here before promoting the summary to
    reviewers.
- **Follow-up:** if any placeholder is missing, rerun with updated token lists
  before distributing artefacts. Log remediation steps alongside manual lint
  commands (`python -m compileall src`) once the reporting bundle is ready.
- **Revert plan:** when an adapter output shape regresses, revert to the last
  known-good commit (record the hash here), restore the stored artefacts from
  secure storage, and pause distribution until the replacement diff/HTML/JSON
  outputs pass the checklist again.
