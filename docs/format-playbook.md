# Format Expansion Playbook

This playbook captures the default workflow for introducing or refining a
format detector. Follow these steps unless the roadmap calls out a
format-specific exception.

> **Quick reference:** Use `docs/format-addition-guide.md` for the canonical
> checklist covering catalog updates, package layout, testing, and validation.
> The remainder of this playbook provides the deeper context and background for
> those steps.

## 1. Prep & Scoping

- Confirm the format appears in `src/driftbuster/catalog.py` (`FORMAT_SURVEY`
  and `DETECTION_CATALOG`). If it is missing, add metadata first.
- Check `CLOUDTASKS.md` for an area covering the work; update it with
  subtasks/checklists if needed.
- Gather representative fixtures (realistic, anonymised where required) and
  store them locally for manual validation. Do not add them to the repo unless
  specifically requested.

### Scope & Inclusion

- **Included:** Open standards or de-facto formats with public specifications
  or vendor documentation and clear, text-based representations that are easy
  to diff.
- **Excluded:** Binary/opaque policy stores unless an authoritative public
  specification exists (e.g., DMTF MOF is in scope; legacy `.pol` binaries are
  not).

### Top Configuration Formats (Windows-first mix)

1. **INI** (`.ini`, `.cfg`) — de-facto. Sectioned key=value files supported by
   Windows profile APIs; straightforward for human edits and line diffs. [link](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprivateprofilestring)
2. **Registry exports** (`.reg`) — de-facto. Text serialization of registry
   keys/values for scripted changes; export before/after for reliable diffs. [link](https://support.microsoft.com/en-us/topic/how-to-add-modify-or-delete-registry-subkeys-and-values-by-using-a-reg-file-9c7f37cf-a5e9-e1cd-c4fa-2a26218a1a23)
3. **XML** (`.xml`) — W3C standard. Schema-validatable markup used in
   structured configs (`app.config`, manifests). [link](https://www.w3.org/TR/xml/)
4. **JSON** (`.json`) — RFC 8259 / ECMA-404. Ubiquitous machine-readable
   configuration; pairs with JSON Schema for validation-aware diffs. [link](https://datatracker.ietf.org/doc/html/rfc8259)
5. **YAML 1.2** (`.yaml`, `.yml`) — open spec. Human-oriented superset of JSON
   common in DevOps workflows. [link](https://yaml.org/spec/1.2.2/)
6. **TOML v1.0.0** (`.toml`) — open spec. Deterministic “INI with types” for
   modern manifest tooling. [link](https://toml.io/en/v1.0.0)
7. **Key/value properties** (`.properties`) — de-facto. Line-oriented
   `key=value` files defined by `java.util.Properties`. [link](https://docs.oracle.com/javase/8/docs/api/java/util/Properties.html)
8. **Environment files** (`.env`) — de-facto. Twelve-Factor pattern for loading
   configuration from environment variables. [link](https://12factor.net/config)
9. **JSON5** (`.json5`) — de-facto. Comment-friendly JSON variant that compiles
   to strict JSON. [link](https://spec.json5.org/)
10. **HJSON** (`.hjson`) — de-facto. Relaxed JSON syntax for hand-edited
    configs. [link](https://hjson.github.io/)
11. **HCL** (`.tf`, `.hcl`) — de-facto. HashiCorp configuration language powering
    Terraform/Consul/Vault. [link](https://github.com/hashicorp/hcl)
12. **HOCON** (`.conf`) — de-facto. JSON superset with includes and
    substitutions used across JVM stacks. [link](https://github.com/lightbend/config)
13. **EDN** (`.edn`) — de-facto. Extensible data notation with tagged values
    from the Clojure ecosystem. [link](https://github.com/edn-format/edn)
14. **Dhall** (`.dhall`) — open spec. Typed, non-Turing configuration language
    that compiles to JSON/YAML. [link](https://dhall-lang.org/)
15. **Jsonnet** (`.jsonnet`) — de-facto. Programmable JSON for composing config
    families. [link](https://jsonnet.org/)
16. **Property list** (`.plist`) — de-facto. XML/binary key-value store used by
    Apple platforms; prefer XML for diffs. [link](https://developer.apple.com/library/archive/documentation/General/Conceptual/DevPedia-CocoaCore/PropertyList.html)
17. **INF** (`.inf`) — de-facto. Driver/setup scripts with sectioned text for
    Windows deployments. [link](https://learn.microsoft.com/en-us/windows/win32/setupapi/about-inf-files)
18. **Administrative templates** (`.admx`, `.adml`) — de-facto. XML policy
    schema mapping to registry settings. [link](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/policy/admx-schema)
19. **PowerShell data files** (`.psd1`) — de-facto. Hashtable syntax for module
    manifests and settings. [link](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_data_files)
20. **PowerShell DSC MOF** (`.mof`) — formal standard (DMTF MOF). Desired State
    Configuration output used for audited state. [link](https://learn.microsoft.com/en-us/powershell/dsc/configurations/write-compile-apply-configuration)

#### INI lineage consolidation

- **Detector family:** `detector_lineage.family == "ini-lineage"` exposes a
  single metadata surface for classic INI, Java properties, dotenv env files,
  and directive-heavy Unix conf payloads. The detector persists a `signals`
  snapshot (sections, directives, exports, density) so downstream adapters can
  explain why a classification was chosen.
- **Secret hygiene:** Sensitive key matches flow into
  `secret_classification.entries` and the plugin emits remediation stubs per
  category (credentials, tokens, key material). Env-file variants always append
  the `env-sanitisation-workflow` remediation linking to
  `scripts/fixtures/README.md` for scrubbed fixture generation.
- **Variant parity:** `detector_lineage.variant` mirrors catalog variants
  (`sectioned-ini`, `java-properties`, `dotenv`, `directive-conf`, etc.) so
  catalogue reviews map 1:1 with runtime detections.

### Diff Tool Guidance

- Favour text representations (`.reg`, XML plists, YAML/TOML) as canonical diff
  inputs; decode binary payloads only when public specs exist (e.g., DSC MOF).
- Parse structured formats (JSON, YAML, TOML, XML, HCL, HOCON, EDN, Dhall,
  Jsonnet output) into trees and diff by key/path to minimise whitespace noise.
- Wire validation before diffing: JSON ➜ JSON Schema, XML ➜ XSD, TOML/HCL/
  HOCON/EDN ➜ library validators, Dhall ➜ type checker.
- Windows-first baseline: ensure `.reg`, `.ini`, `.inf`, `.admx`/`.adml`, and
  `.psd1` flows are covered early—they represent common enterprise scenarios.

### Residual Uncertainty

- Ranking reflects a Windows-first, cross-OS viewpoint; adjust when industry
  focus shifts.
- Niche device standards (e.g., YANG for network hardware) stay out of scope
  until they become relevant to DriftBuster’s target configurations.

## 2. Implementation Standards

- Place new detectors under `src/driftbuster/formats/<format>/plugin.py`.
- Keep registration side-effects limited to module import (use
  `formats/__init__.py` for eager registration).
- Call `register(Plugin())` exactly once during module import; `_ensure_unique`
  raises a `ValueError` if a different object with the same `plugin.name`
  sneaks through.
- Prefer lightweight heuristics: filename hints, signatures, sampling-based
  content checks. Avoid full parsing or external dependencies unless agreed.
- Populate detection metadata with catalog-aligned keys (`format_name`,
  `variant`, `confidence`, `reasons`, relevant metadata fields).
- Respect sampling limits—never read entire files when a bounded sample gives
  the same signal.
- Watch for format drift (line endings, whitespace, tag ordering). Surface
  canonical metadata that downstream diff tooling can use to highlight those
  changes.
- Surface dynamic token metadata whenever a detector can reliably label
  placeholders (hostnames, cert hashes, secrets). Route those fields through
  hunt mode (`docs/hunt-mode.md`) so reviewers can approve or reject expected
  churn explicitly.

## 3. Documentation Requirements

- Update `docs/detection-types.md` with:
  - New table entries or variant notes.
  - Practical guidance on interpreting metadata/confidence.
  - Known limitations or ambiguous cases.
- Mention any registry ordering requirements when documenting the detector.
- Capture the current plugin ordering with `driftbuster.registry_summary()` and
  archive the JSON output in `notes/checklists/registry.md` for the review
  cycle.
- Refresh `README.md` if public usage instructions change (e.g., sample code or
  metadata descriptions).
- Adjust `CLOUDTASKS.md` acceptance gates if the new work shifts future
  dependencies.
- Cross-link supporting docs: diff/hunt workflows belong in
  `docs/format-playbook.md`, sample sourcing stays under
  `docs/testing-strategy.md`, and compliance reminders live in
  `docs/legal-safeguards.md`.

## 3a. Manual diff & dynamic token checklist

Use this quick checklist once detector code lands. It keeps diff workflows
deterministic and highlights which docs to touch:

1. Capture a **baseline** sample from the good inventory noted in
   `docs/testing-strategy.md`.
2. Run the detector against both baseline and drift samples, storing the
   metadata/hunt output locally (never in-repo).
3. Call `diff_summary_snapshots` from `ProfileStore` helpers if profiles are in
   play. Note the results in `notes/checklists/profile-summary.md`.
   When comparing more than one before/after pair, run
   `driftbuster.reporting.diff.summarise_diff_results` so reviewers receive a
   single metadata bundle for every comparison.
4. Record dynamic token decisions in `notes/checklists/hunt-profile-review.md`.
5. Update the relevant doc sections (`docs/configuration-profiles.md`,
   `docs/hunt-mode.md`, `CLOUDTASKS.md` (areas A10-A12)) if reviewers need new
   context to interpret the diff.

### JSON detector checklist

- Run `PYTHONPATH=src python -m driftbuster.cli fixtures/config/appsettings.json`
  and archive the output in `notes/snippets/json-cli-run.md`.
- Confirm the reasons mention JSON structure, key/value detection, and the
  `structured-settings-json` variant when the sample contains
  `ConnectionStrings` or `Logging` keys.
- Check the metadata block for `top_level_type`, `top_level_keys`, and
  `settings_hint` so profile/hunt tooling inherits the same context.
- When scanning comment-heavy payloads (`*.jsonc`), verify the reasons note the
  comment detection and that the variant flips to `jsonc`.
- Log any manual linting or sanitisation steps alongside the CLI command in
  `notes/checklists/core-scan.md` before marking the area complete.

## 4. Validation & Gates

- Manual-only verification (guardrail):
  - Run the detector against a mixed fixture set (`python -m driftbuster` or a
    short script) and record observed matches, including confidence/reasons.
  - Capture the command(s) and observations in repo notes (update the relevant
    `CLOUDTASKS.md` checklist item).
- When applicable, note whether the detector captures format drift cues so the
  reporting adapters can produce diff/patch output.
- Identify settings that vary per deployment (hostnames, thumbprints, etc.) and
  suggest hunt-mode rules or metadata fields to capture them explicitly.
- Ensure mis-detections fall back to the generic/binary detectors rather than
  raising exceptions.
- Confirm error handling remains friendly (no raw stack traces for expected
  scenarios such as unreadable files).
- Cross-check the manual lint/test commands in
  `docs/testing-strategy.md#detector-manual-lint--test-checklist` and log the
  output in the matching checklist entry.

## 5. Finishing Checklist

- ✅ Code lives under `src/driftbuster/formats/<format>/` with registration in
  `src/driftbuster/formats/__init__.py`.
- ✅ Catalog metadata updated (and any new metadata keys reflected in
  `core/types.py` if needed).
- ✅ Docs refreshed (`docs/detection-types.md`, optionally `README.md`).
- ✅ Manual verification log added; acceptance gates in `CLOUDTASKS.md` ticked.
- ✅ Follow-up tasks for downstream formats captured in `CLOUDTASKS.md` or
  `CLOUDTASKS.md` if gaps remain.
- 🚧 Deferred automation items recorded in `docs/testing-strategy.md` under the
  "Deferred automation" block (typing + fuzz harnesses).

Stick to these standards so each detector lands consistently and the upcoming
format work remains predictable.
