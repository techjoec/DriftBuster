# Testing Strategy

Automated tests back the detection engine, format plugins, diff, hunt and
secret scanning, profiles and scheduling, registry and SQL export, reporting,
the console tool, the PowerShell module, the offline runner, and the Avalonia
view models. Manual validation continues to play a role for vendor fixtures and
reporting flows.

Policy: maintain ≥ 83% total line coverage over the merged Backend, CLI and GUI
test report. Treat this as a hard baseline for new and modified components.

## Automated test suite

- `scripts/verify_coverage.sh` — runs the three .NET test projects with
  coverlet, merges their reports into `build/coverage/merged/` and fails below
  the threshold (`DOTNET_THRESHOLD`, default 83). When `pwsh` is on `PATH` it
  also runs the Pester suites. `--perf-smoke` adds the `Category=PerfSmoke`
  GUI suite and logs it under `artifacts/perf/`.
- `dotnet test gui/DriftBuster.Backend.Tests/DriftBuster.Backend.Tests.csproj`
  — detection, catalog and plugins, diff, hunt, secrets, multi-server,
  profiles, scheduling (including DST), registry, SQL export, reporting and
  capture. Windows registry backend tests skip on other platforms.
- `dotnet test cli/DriftBuster.Cli.Tests/DriftBuster.Cli.Tests.csproj` — the
  `driftbuster` commands, including `version` and `release`.
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
- `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter Overflow_moves_extra_toasts`
  verifies toast overflow behaviour and should run after modifying toast capacity or overflow UI.
- Pester: `cli/DriftBuster.PowerShell.Tests/DriftBuster.PowerShell.Tests.ps1`
  (module) and `scripts/DriftBusterOfflineRunner.Tests.ps1` (offline runner).
- `scripts/lint_all.sh` — `dotnet format DriftBuster.sln --verify-no-changes`
  plus `scripts/lint_powershell.ps1` (PSScriptAnalyzer over `cli/` and
  `scripts/`, failing on any warning or error).
- `dotnet build` runs the Meziantou and built-in analyzers with code style
  enforcement (see `Directory.Build.props`). Address any analyzer warnings
  before committing.

### Review flags and profile ignores
- Plugins may mark oddities with `metadata.needs_review` and `review_reasons`.
- Profiles can suppress review flags per config via
  `metadata.ignore_review_flags = true`.
- Tests cover flag emission and profile‑based suppression: the `*FlagsTests`,
  `XmlWellformedFlagTests` and `YamlFlagsAndGatingTests` under
  `gui/DriftBuster.Backend.Tests/Detection/Plugins/`, and
  `gui/DriftBuster.Backend.Tests/Profiles/Detection/DetectorProfileReviewIgnoreTests.cs`.
- New detector heuristics bump the plugin `Version` and update `docs/format-support.md`
  and `docs/detection-types.md`; every variant and metadata field needs a matching test under
  `gui/DriftBuster.Backend.Tests/Detection/Plugins/`.

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

Prioritised sourcing for the current compliance push lives below.

### Vendor sample sourcing plan (2025-10-25)

1. **Telemetry retention bundle** — Track retention-focused JSON configs inside
   `fixtures/vendor_samples/telemetry_collector_sample.json`. Source structure
   from public pruning guidance, convert real hostnames to the `example.invalid`
   domain, and swap tokens for environment variable placeholders. During manual
   rehearsals, pair this fixture with `driftbuster maint purge-reporting-retention
   --retention-days` to confirm pruning guardrails stay aligned with compliance docs.
2. **Directory sync export** — Maintain the YAML payload at
   `fixtures/vendor_samples/identity_directory_sample.yaml` to rehearse identity
   integrations. Derive the payload layout from open standards documentation,
   inject deterministic hashing guidance for identifiers, and log any
   incremental sync deviations in `notes/checklists/manual-tests.md`.
3. **Review cadence** — Every time new vendor formats are requested, log the
   proposed sample in `notes/checklists/manual-tests.md`, prepare a synthetic
   fixture in `fixtures/vendor_samples/`, and append a provenance row to
   `fixtures/README.md`. Decline samples that cannot be built from public or
   synthetic material without exposing proprietary data.

- Expand the table row-by-row as new detectors appear. Keep the first column
  aligned with catalog format identifiers so cross-referencing stays painless.
- Store only the links to these samples or short extraction scripts in a
  private, access-controlled mirror repository, together with their retrieval
  commands; do not commit the raw vendor fixtures here.

### Legal coordination

- Cross-check every new sample against `docs/legal-safeguards.md` to confirm it
  avoids vendor names, application branding, and proprietary specifics.
- Skip importing encrypted, DRM-protected, or proprietary binaries into the
  project. Build fixtures from public or original material only.
- Replace real identifiers with neutral placeholders before saving snippets or
  notes inside the repo.

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
- Capture the `metadata.plan_transform` blocks from `driftbuster hunt` output alongside approvals so future
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
- When preparing a manual review, fetch the sanitized sample, run `driftbuster hunt`
  with the relevant `--exclude` patterns, and capture approvals in the checklist
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
- Record the resulting `plan_transform` placeholders from `driftbuster hunt` next to the
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
2. Apply deterministic mutations with scripts kept in the local notes folder;
   capture command output in `notes/checklists/manual-tests.md`.
3. Run the detector manually (`driftbuster scan <file> --json`) and log
   metadata deltas.
4. Record any parsing or sampling issues alongside remediation ideas. If a
   mutation reveals a bug, file it in the issue tracker referencing the sample
   row.
5. Keep prospective fuzz scripts documented in the checklist file rather than
   wiring them into automation.

## Validation Workflow

- When adding a detector, update this plan with new sample sources and fuzz
  strategies.
- Record manual execution steps (commands, expected outcomes) alongside the
  detector checklists under `notes/checklists/`.
- Before shipping major releases, run through the curated sample set and note
  anomalies for follow-up.

## Capture manifests

- `driftbuster capture run` defaults: root `.`, glob `**/*`, output directory
  `captures`, placeholder `[REDACTED]`.
- Capture manifests (schema version `1.0`) require `--environment` and
  `--reason` plus an operator (`--operator`, or `DRIFTBUSTER_CAPTURE_OPERATOR`
  / `USER` in the environment). The command stops with an error when those are
  missing, so rehearsals must provide them.
- A capture refuses to run without mask tokens unless `--allow-unmasked` is
  passed.

## Metadata Validation Routine

- Generate detection outputs against the fixture set (`driftbuster scan
  fixtures --json`); every match carries the catalog keys that
  `DetectionMetadata.ValidateDetectionMetadata` adds. Capture the resulting
  payloads in `notes/checklists/metadata-mapping.md`.
- Flag failures immediately in the checklist and attach the offending metadata
  payload so regressions are visible without re-running the scan.

### Pre-release checklist

- Re-run the scan across XML, .config, and binary fixtures.
- Confirm `catalog_version` and `catalog_format` align with the catalog in
  `gui/DriftBuster.Backend/Detection/Catalog/DetectionCatalogData.cs`.
- Review diff logs to ensure no unexpected metadata keys vanished between
  releases.

## Open Items

- Finalise the mirror repository structure for storing retrieval scripts and
  redaction helpers.
- Evaluate structured diff tooling to compare expected vs. actual metadata for
  fuzzed fixtures without enabling automated pipelines.
- Source additional public YAML and INI samples and extend the inventory.
- Draft placeholder mutation recipes for future binary formats (e.g., firmware
  slices) while keeping them manual-only.
- For INI/CONF updates, log manual checks that preserve key ordering, comment markers (inline vs line), mixed newline handling, and encoding detection (UTF BOMs vs Latin-1) in `notes/checklists/manual-tests.md` to mirror backlog expectations.
