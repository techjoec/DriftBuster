# Testing Strategy

Automated coverage now backs the detector, format plugins, hunt helpers, the
PowerShell/GUI backend library, and Avalonia viewmodels. Manual validation
continues to play a role for vendor fixtures and pre-HOLD reporting flows.

Policy: Maintain ≥ 90% line coverage across Python source (under `src/`) and
≥ 90% total line coverage for the .NET surface. Treat this as a hard baseline
for new and modified components.

## Automated test suite

- `pytest -q` — exercises detector metadata, profile helpers, diff planning,
  hunt rules, registry utilities, JSON/XML plugins, and CLI helpers. The suite
  injects the `src/` tree via `tests/conftest.py`.
- `pytest tests/multi_server -q` — validates the multi-server orchestration
  bridge, ensuring cache reuse, catalog aggregation, and drilldown payloads
  stay deterministic across runs.
- `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj` — runs
  the Avalonia headless suite (`[AvaloniaFact]`) covering MainWindow
  navigation, drilldown export/rescan, hunt mode flows, profile interactions,
  GUI converters, dispatcher-backed toast/session services, responsive host
  layout validation, catalog sort persistence, and the drilldown Copy JSON
  workflow. Launch this inside a tmux session (`tmux new -s codexcli-<pid>-tests 'dotnet test …'`)
  so long-running GUI runs don’t block your shell.
- `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter MainWindowUserJourneyTests`
  drives the end-to-end multi-server journey (catalog + drilldown + hunt +
  profiles) against the fake backend and should pass before claiming GUI
  parity with multi-host plans.
- `dotnet test gui/DriftBuster.Gui.Tests/Services/ToastServiceTests.cs --filter Overflow_moves_extra_toasts`
  verifies toast overflow behaviour and should run after modifying toast capacity or overflow UI.
- `dotnet build` now runs with the latest built-in analyzers and style
  enforcement (see `Directory.Build.props`). Address any analyzer warnings
  surfaced during builds before committing.
- `pwsh scripts/lint_powershell.ps1` — runs PSScriptAnalyzer across the
  PowerShell module and fails if any warnings or errors are detected.
- `python -m compileall src` — sanity compiles the entire Python tree.
- `python -m pycodestyle src` — style-checks all Python modules using the
  defaults codified in `setup.cfg` (currently 140-character lines).
- `dotnet format gui/DriftBuster.Backend/DriftBuster.Backend.csproj --verify-no-changes`
  — validates the backend library formatting against analyzer defaults.
- `dotnet format gui/DriftBuster.Gui/DriftBuster.Gui.csproj --verify-no-changes`
  — enforces GUI project code style expectations.
- `dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --verify-no-changes`
  — keeps test harness formatting aligned with the production projects.

Run both commands before landing changes that touch the Python core or the
Avalonia/PowerShell surfaces. Use `-q`/`--no-build` switches if you need to
minimise output or skip rebuilds during local iteration.

### Coverage measurement (quick commands)

- Python (engine/detectors/reporting)
  - `coverage run --source=src/driftbuster -m pytest -q`
  - `coverage report --fail-under=90` and/or `coverage json -o coverage.json`
  - Optional HTML: `coverage html` → open `htmlcov/index.html`
- .NET GUI (xUnit + coverlet collector)
  - `tmux new -s codexcli-<pid>-coverage 'dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --collect="XPlat Code Coverage" --results-directory artifacts/coverage-dotnet'`
  - Inspect `artifacts/coverage-dotnet/<run-id>/coverage.cobertura.xml` for
    per-viewmodel coverage and ensure the heavy UI surfaces (catalog,
    drilldown, multi-server orchestration) stay at or above the 90% line-baseline.
- Repo‑wide summary: `python -m scripts.coverage_report`
  - Prints Python %, .NET %, and the most under‑covered GUI classes.

### Review flags and profile ignores
- Plugins may mark oddities with `metadata.needs_review` and `review_reasons`.
- Profiles can suppress review flags per config via
  `metadata.ignore_review_flags = true`.
- Tests should cover: flag emission and profile‑based suppression.

Local guardrails:

- Python threshold: `coverage report --fail-under=90`
- .NET threshold (coverlet): `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total`

Shortcut: run `scripts/verify_coverage.sh` (POSIX shells) or `python -m scripts.verify_coverage` for the cross-platform equivalent to execute both suites with thresholds and print the combined summary.

## Vendor Sample Acquisition

- Collect publicly available configuration samples (open-source projects, vendor
  documentation) and sanitise them before use.
- Build a catalog of formats → sample sources. Track licensing status for each
  sample.
- Store references (URLs, extraction scripts) rather than raw proprietary
  files.
- Keep the inventory mirrored in both this document and
  `notes/checklists/manual-tests.md` so manual verification steps stay in sync.

### Sample Inventory Template

| Format | Source | Licensing | Sanitisation Notes |
| ------ | ------ | --------- | ------------------ |
| XML configuration | Public documentation sample converted to XML for parser stress tests | Open documentation terms | Strip organisation-specific IDs; replace URLs with neutral placeholders. |
| JSON telemetry | Open audit log example published under community governance docs | Open documentation terms | Remove timestamps older than 30 days; hash node names with deterministic salt. |
| Binary blob | Public CA certificate bundle metadata snapshot | Open documentation terms | Retain only certificate metadata headers; truncate bodies after first block to minimise sensitive material. |

- Expand the table row-by-row as new detectors appear. Keep the first column
  aligned with catalog format identifiers so cross-referencing stays painless.
- Store only the links to these samples or short extraction scripts in a
  private, access-controlled mirror repository. For day-to-day development,
  keep a local `samples/README.md` with retrieval commands but do not commit the
  raw fixtures here.

### Legal coordination

- Cross-check every new sample against `docs/legal-safeguards.md` to confirm it
  avoids vendor names, application branding, and proprietary specifics.
- Skip importing encrypted, DRM-protected, or proprietary binaries into the
  project. Build fixtures from public or original material only.
- Replace real identifiers with neutral placeholders before saving snippets or
  notes inside the repo.

### Reporting compliance hooks

- Treat font telemetry retention runs as compliance-sensitive. When enabling
  `--print-retention-metrics` or overriding `--retention-metrics-path`, record
  the resulting evidence location in `notes/checklists/legal-review.md` and
  follow the guardrails documented in `docs/legal-safeguards.md#font-telemetry-retention-compliance`.
- If the metrics file is disabled (`-`), capture the inline output in a
  restricted transcript and avoid copying it into public runbooks or issue
  trackers.
- Confirm that deleted filename references remain anonymised before attaching
  the metrics payload to investigations or review bundles.

## Profile & hunt sample logistics

Maintain a parallel inventory for configuration profiles so hunt approvals stay
grounded in reproducible fixtures.

### Sanitised "good" config inventory

- Track the baseline configs that represent healthy deployments. Store only
  references (URLs, archive hashes, or mirror repo paths) alongside masking
  notes.
- Extend the sample table with a `profile` column pointing at the relevant
  configuration profile name or identifier.
- For each entry, document the token placeholders you expect to approve (e.g.,
  `server_name`, `certificate_thumbprint`) and confirm the redaction method.
- Capture the `build_plan_transforms` output (or the
  `metadata.plan_transform` block from JSON hunts) alongside approvals so future
  diff plans inherit the same masking tokens without manual re-entry.
- Keep approval snapshots outside the repository; reference the location in
  `notes/checklists/hunt-profile-review.md`.

### Drift sample catalog

- Build a separate list of "bad" configs that demonstrate drift. Highlight the
  token names that should fail approval so reviewers can compare against the
  good inventory quickly.
- Record the command or script used to mutate the baseline into the drift
  sample (stored externally). Include notes on deterministic mutation steps so
  the sample can be recreated when needed.
- Tie each drift sample back to the detector metadata you expect to change, so
  manual hunts and profile diff reviews cover the same surface area.

### Retrieval workflow

- Keep a short README in the private mirror describing how to pull the good and
  drift inventories, including any authentication requirements.
- When preparing a manual review, fetch the sanitized sample, run `hunt_path`
  with the relevant `exclude_patterns`, and capture approvals in the checklist
  template.
- Note which placeholders require manual masking before storing the hunt output
  log. The log should live outside the repository but be linked from the
  checklist entry for traceability.

## Synthetic Fixture Generation

- Derive template-based generators for each format (e.g., parameterised JSON
  skeletons) to create varied inputs.
- Introduce fuzz hooks that mutate structure (missing keys, unexpected types,
  format drift like line endings or tag shuffles).
- Document how to reproduce fuzz runs manually; no automated fuzzing yet.
- Track dynamic token samples (hostnames, thumbprints, versions) so hunt-mode
  rules can be verified against real-world data.
- Record the resulting placeholders from `build_plan_transforms` next to the
  fuzzed sample so masking expectations remain reproducible.

### Format-Specific Fuzz Heuristics

- **XML configuration** — mutate attribute ordering, drop namespace prefixes,
  and randomise whitespace around closing tags. Inject UTF-16 byte-order marks
  to confirm the sampler respects encoding metadata.
- **JSON telemetry** — reorder array items, coerce numeric strings into numbers,
  and truncate nested objects at varying depths. Flip between Unix newlines and
  Windows carriage returns to surface newline handling bugs.
- **Binary blobs** — slice payloads at 512-byte boundaries, flip individual bits
  within metadata headers, and prepend/append null-byte padding. Validate that
  truncation signalling remains consistent with `metadata['sample_truncated']`.

### Manual Fuzz Workflow

1. Check out the linked sample reference and copy it into a disposable working
   directory outside the repository.
2. Apply deterministic mutations using `python - <<'PY'` snippets stored in the
   local notes folder; capture command output in `notes/checklists/manual-tests.md`.
3. Run the detector manually (`python -m driftbuster.scan --path <file>` or
   equivalent helper) and log metadata deltas.
4. Record any parsing or sampling issues alongside remediation ideas. If a
   mutation reveals a bug, file a TODO in `CLOUDTASKS.md` referencing the sample
   row.
5. Automation backlog: document prospective fuzz scripts but do not add them to
   CI yet. Track these placeholders under the "Future automation" block in the
   checklist file.

## Validation Workflow

- When adding a detector, update this plan with new sample sources and fuzz
  strategies.
- Record manual execution steps (commands, expected outcomes) alongside
  detector checklists in `CLOUDTASKS.md`.
- Before shipping major releases, run through the curated sample set and note
  anomalies for follow-up.

## HOLD Exit Checklist Hooks

- Cross-reference the decision summary in `notes/status/hold-log.md#decision-ready-summary` before expanding reporting coverage.
- Confirm the sample inventory rows cited here still follow the vendor-neutral guardrails in `docs/legal-safeguards.md#hold-exit-briefing`.
- Keep running the manual compile/lint block from `notes/checklists/core-scan.md`; log the results next to the HOLD exit review entry once approvals land.
- 2025-10-24 validation: Confirmed `scripts/capture.py` defaults (`root='.'`, glob `**/*`, output dir `captures`, placeholder `[REDACTED]`) match this readiness packet; update this note if defaults change.
- Document any capture manifest tweaks alongside the roadmap entry so `scripts/capture.py` defaults stay aligned with the readiness packet.
- Capture manifests (schema version `1.0`) require explicit `--environment` and
  `--reason` flags plus an operator identifier. The helper will abort if those
  fields are omitted, so rehearsals must provide them or set
  `DRIFTBUSTER_CAPTURE_OPERATOR` in the environment before execution.

## Metadata Validation Routine

- Generate detection outputs against the fixture set and pipe each
  ``DetectionMatch`` through ``validate_detection_metadata``; capture the
  resulting dictionaries in `notes/checklists/metadata-mapping.md`.
- Flag failures immediately in the checklist and attach the offending metadata
  payload so regressions are visible without re-running the scan.
- Use ``python - <<'PY'`` snippets to batch-validate outputs while keeping the
  process manual (automation still deferred).

### Pre-release checklist

- Re-run the validator across XML, .config, and binary fixtures.
- Confirm `catalog_version` and `catalog_format` align with
  `driftbuster.catalog.DETECTION_CATALOG`.
- Review diff logs to ensure no unexpected metadata keys vanished between
  releases.

### Future lint rule (deferred)

- Plan a lightweight static rule that checks for missing ``catalog_*`` keys in
  test fixtures once automation is allowed. Document this backlog item in
  `CLOUDTASKS.md` and revisit when CI guardrails open up.

## Detector manual lint & test checklist

- Run the automated suite plus the targeted lint block before checking in detector/profile work:

  ```sh
  pytest -q
  dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj
  python -m compileall src
  python -m pycodestyle src/driftbuster/core
  python -m pycodestyle src/driftbuster/formats/registry_live
  python -m pycodestyle src/driftbuster/registry
  ```

- `pytest` and `dotnet test` confirm behaviour across detector, plugins, hunt,
  CLI, API, and GUI layers.
- `python -m compileall src` — confirms helper modules (e.g.,
  `_validate_sample_size`) remain syntax safe across Python versions.
- `python -m pycodestyle src/driftbuster/core` — spot-checks detector style
  before pushing shared guardrails wider.
- `python -m pycodestyle src/driftbuster/formats/registry_live` — confirms the
  registry-live plugin follows the same conventions as the detector module.
- `python -m pycodestyle src/driftbuster/registry` — confirms runtime registry
  helpers follow the same conventions.
- Capture results in `notes/checklists/core-scan.md` along with fixture
  metadata so the troubleshooting table in `README.md` stays trustworthy.

### Deferred automation backlog

- `mypy src/driftbuster/core` to lock down callback typing.
- `mypy src/driftbuster/formats/registry_live` for registry-live type invariants.
- `mypy src/driftbuster/registry` for registry scan backend invariants.

## Open Items

- Finalise the mirror repository structure for storing retrieval scripts and
  redaction helpers.
- Evaluate structured diff tooling to compare expected vs. actual metadata for
  fuzzed fixtures without enabling automated pipelines.
- Source at least two additional public formats (YAML, INI) and extend the
  inventory once detector support becomes available.
- Draft placeholder mutation recipes for future binary formats (e.g., firmware
  slices) while keeping them manual-only.
- For INI/CONF updates, log manual checks that preserve key ordering, comment markers (inline vs line), mixed newline handling, and encoding detection (UTF BOMs vs Latin-1) in `notes/checklists/manual-tests.md` to mirror backlog expectations.
