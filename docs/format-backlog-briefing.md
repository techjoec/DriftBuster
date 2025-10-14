# Format Expansion Briefing

The backlog remains on HOLD, but we now have a ready-to-run outline covering heuristics, samples, and legal guardrails so new detectors can land quickly once prioritised.

## Priority Snapshot

| Priority | Format focus | Key rationale | Readiness notes |
| --- | --- | --- | --- |
| 1 | XML family | Lock reliable `.config` and schema-backed parsing before downstream adapters inherit inconsistent metadata. | Requires catalog schema provenance fields and attribute hunt tokens. |
| 2 | JSON | Harden parsing across `json`, `jsonc`, and structured settings JSON (`appsettings.json`) to stabilise variant tagging for reports. | Needs large-sample validation against new sampling guardrails. |
| 3 | INI lineage | Unify INI, key/value properties, directive conf, and dotenv handling to prevent duplicate catalog entries. | Requires remediation guidance, secret detection hints, and encoding audit. |
| 4 | Structured text | Deliver YAML/TOML detectors with tiered confidence to bridge structured/unstructured coverage. | Must define indentation tolerance policy before surfacing matches. |
| 5 | Binary/Hybrid | Support embedded SQL, opaque binary, markdown front matter, property lists, and engine-driven hybrid mixes for legacy parity. | Depends on binary diff adapters and legal approval for redistributed fixtures. |

## Format Playbooks

### XML family
- **Detection heuristics:**
  - Extensions: `.config`, `.xml`, `.resx`, `.targets`.
  - Patterns: XML declaration, `<configuration>` roots, schemaLocation attributes, namespace URIs.
  - Hunt tokens: attribute drift for connection strings, endpoints, feature flags.
  - Catalog updates: schema provenance now emitted in ``schema_locations`` metadata; namespace confidence weighting remains on the roadmap.
- **Sample and testing needs:**
  - Public samples: generic runtime configs, orchestration manifests, open-source application templates.
  - Manual pass: scan well-formed, minified, and malformed XML; confirm metadata tags round-trip.
  - Fuzz ideas: attribute shuffling, namespace removal, character encoding flips (UTF-8/UTF-16/Latin-1), truncated closing tags.
- **Legal considerations:**
  - Strip proprietary URIs and credentials before storage.
  - Ensure redistribution rights for vendor schemas or link to public versions only.

### JSON
- **Detection heuristics:**
  - Extensions: `.json`, `.jsonc`, `.appsettings.json` (structured settings JSON).
  - Patterns: braces with key/value density, trailing comment markers (`//`, `/* */`) for JSONC.
  - Hunt tokens: dynamic environment keys (`Endpoint`, `TenantId`, `ClientSecret` placeholders).
  - Catalog updates: variant identifiers for JSONC and nested profile formats, plus compression flags for large payloads.
- **Sample and testing needs:**
  - Public samples: workflow definitions, editor settings, environment configuration templates.
  - Manual pass: large vendor JSONs to validate sampling/truncation metadata and reason normalisation.
  - Fuzz ideas: introduce trailing commas, reorder arrays, strip comments, inject UTF-8 BOM.
- **Legal considerations:**
  - Redact tokens and GUIDs before storing fixtures.
  - Maintain provenance log when samples originate from permissive licenses.

### INI lineage
- **Detection heuristics:**
  - Extensions: `.ini`, `.cfg`, `.conf`, `.properties`, `.env`.
  - Patterns: section headers (`[section]`), key/value separators (`=`, `:`), dotenv prefix handling.
  - Hunt tokens: secrets (`password`, `token`), service endpoints, feature toggles.
  - Catalog updates: unify encoding expectations, add field describing comment syntax to support adapters.
- **Sample and testing needs:**
  - Public samples: database configs, web server templates, application property files, Twelve-Factor dotenv examples.
  - Manual pass: verify key ordering preservation, comment retention, and mixed encoding (UTF-8 + Latin-1) scenarios.
  - Fuzz ideas: duplicate keys, mixed newline styles, missing section headers, BOM-prefixed dotenv files.
- **Legal considerations:**
  - Ensure any vendor-derived configs are anonymised (hostnames, credentials).
  - Document removal of customer-specific routes before archival.

### Structured text (YAML/TOML)
- **Detection heuristics:**
  - Extensions: `.yml`, `.yaml`, `.toml`.
  - Patterns: colon-delimited mappings, indentation-based structure, TOML tables and dotted keys.
  - Hunt tokens: deployment targets, image digests, semantic version pins.
  - Catalog updates: add indentation tolerance metadata and multi-document stream indicators.
- **Sample and testing needs:**
  - Public samples: orchestration manifests, automation workflows, build descriptors, package manifests.
  - Manual pass: multi-document YAML, anchors/aliases, TOML arrays of tables; ensure diff adapters respect ordering.
  - Fuzz ideas: indentation drift, anchor removal, quoting changes, invalid numeric formats.
- **Legal considerations:**
  - Strip internal repository URLs before storage.
  - Confirm open-source license compatibility for reused manifests.

### Binary/Hybrid
- **Detection heuristics:**
  - Extensions: `.sqlite`, `.db`, `.plist`, `.md`, `.ini`, `.json` combos for hybrid game/runtime layouts.
  - Patterns: file signatures (SQLite header, plist XML/binary magic), markdown front matter fences (`---`).
  - Hunt tokens: schema version changes, binary blob hashes, front matter metadata like `draft`, `author`.
  - Catalog updates: indicate binary vs hybrid payload, attach adapter hints for diff tooling.
- **Sample and testing needs:**
  - Public samples: embedded database fixtures, platform-neutral property list templates, static site markdown collections.
  - Manual pass: confirm binary detection gracefully degrades when sampling truncated; validate metadata serialisation.
  - Fuzz ideas: truncate binaries, flip endianness markers, swap front matter keys, inject invalid UTF-8 sequences.
- **Legal considerations:**
  - Do not store proprietary database dumps; rely on generated fixtures.
  - Maintain anonymisation script references for markdown metadata derived from internal sources.

## Testing and Legal Impact Summary
- Manual testing will rely on documented fixtures with provenance notes captured alongside each sample set.
- Lint guidance must expand to describe whitespace/encoding handling before YAML/TOML rollout.
- Legal safeguards require redaction tooling for binary extracts plus schema redistribution checks for XML.

## Decision Log

| Date (UTC) | Format(s) | Decision | Impact | Notes |
| --- | --- | --- | --- | --- |
| _Pending_ | All | Awaiting user prioritisation to lift HOLD. | None yet. | Populate once prioritisation occurs. |
