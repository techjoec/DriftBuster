# DriftBuster Roadmap

## Core

- Stabilise the detector orchestration, sampling budget, and metadata schema.
- Keep `driftbuster/catalog.py` authoritative for detection class metadata.
- Ensure the registry utilities expose the usage metadata needed for planning.
  - `driftbuster.registry_summary()` now returns an ordered snapshot of
    registered plugins for manual reviews; expand the payload with usage stats
    once adapters are back on the table.
- Introduce diff/patch generation utilities so detections can surface
  before/after comparisons and formatting drift (line endings, tag placement,
  etc.).
- Integrate hunt-mode detection for dynamic values (hostnames, thumbprints,
  version numbers) and plan transformation hooks for future templating.

## Formats

| Priority | Format focus | Rationale | Hold considerations |
| --- | --- | --- | --- |
| 1 | XML family | Finish `.config` heuristics, surface schema-driven hints, and align variant tagging before downstream tooling depends on them. | Requires catalog updates for schema provenance and hunt token expansion for attribute drift. |
| 2 | JSON | Deliver confident parsing with `jsonc` + `appsettings` support so metadata consumers inherit consistent variant IDs. | Blocked until sampling guardrails prove stable with large vendor payloads. |
| 3 | INI lineage | Unify INI, key/value properties, directive conf, and dotenv handling to eliminate duplicate catalog entries. | Needs remediation hint mapping + secret/token classification for hunt mode. |
| 4 | Structured text | Introduce YAML and TOML detectors with tiered confidence to bridge structured/unstructured coverage gaps. | Ensure lint guidance codifies whitespace/indentation tolerances before rollout. |
| 5 | Binary/Hybrid | Cover embedded SQL signatures, opaque binary, markdown front matter, property lists, and engine-driven hybrid mixes for parity with legacy tooling expectations. | Dependent on binary diff adapters and legal sign-off for redistributed fixtures. |

- Remaining XML backlog items once transforms settle:
  - Canonical diff rendering for XML attribute ordering and whitespace churn.
  - Extended transform precedence rules (multi-stage build pipelines).
  - Namespace provenance logging to feed future reporting adapters.

## Metadata Enhancements

- Add catalog-level severity hints and remediation tips per format so reports
  can prioritise drift surfaced by high-risk detectors.
- Expand variant metadata to capture remediation guidance once hunt tokens
  mature (depends on dynamic token detection to avoid false positives).
- Explore attaching schema or reference documentation links directly to the
  metadata payload for adapters to surface contextual help.

## Outputs & Tooling

- Add JSON/HTML renderers alongside the planned CLI so scans can export
  machine-readable reports and lightweight dashboards.
- Provide diff/patch-style output showing detected drift between stored
  captures.
- Support automated capture + comparison pipeline (store snapshots, highlight
  change history, alert when configs diverge).
- Windows-first GUI exploration (low priority) to visualise scan results using
  the same output adapters.

## Compliance & Testing Plans

- Document legal/IP guardrails to ensure vendor configurations are processed
  safely (no redistribution of proprietary material).
- Build a strategy for sourcing vendor config samples and generating fuzz
  inputs to stress detectors (manual plan first, implementation later).

Each step should land with updated usage notes in `docs/detection-types.md`
so the catalog and implementation stay aligned.
