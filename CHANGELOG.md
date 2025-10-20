# Changelog

All notable changes to this project are documented here. This file complements
component-level logs under `notes/changelog/` and the per-release notes under
`notes/releases/`.

This format follows a simplified Keep a Changelog style: sections are grouped
by Added, Changed, Fixed, and Docs.

## [Unreleased]

### Added
- `registry-live` format plugin for registry scan definition manifests (JSON/YAML).
- `yaml` format plugin (heuristic, no parser dep) with Kubernetes manifest hinting.
- `conf` DSL plugin for Logstash pipeline configs.
- `text` directive plugin for OpenSSH/OpenVPN and generic directive-style configs.
- `toml` format plugin (tables, arrays-of-tables, dotted keys, quoted/array values).
- `hcl` format plugin (Nomad/Vault/Consul block detection + key assignments).
- `dockerfile` plugin (filename hint, first-line FROM, common directives).
- Windows Registry live scan utilities (enumerate apps, suggest roots, search).
- Offline runner support for `registry_scan` sources; writes `registry_scan.json`.
 - Review flags in format plugins: detectors now annotate `needs_review` with `review_reasons` for suspicious or malformed inputs (e.g., JSON parse failures, XML not well-formed, YAML tabs, TOML suspect patterns, INI malformed sections). Profiles can opt-out per config via `metadata.ignore_review_flags`.

### Changed
- Catalog includes `RegistryLive` class; validation maps `registry-live`.
 - YAML/INI ordering updated (YAML before INI). YAML skips heavy comment prologs; INI avoids YAML extensions and tolerates colon-only `.preferences` files.
 - Normalized plugin aliases in metadata validation (`dockerfile`→`script-config`, `hcl`→`ini`).
- GUI: Sharper theme palette, accent/outline button variants, larger defaults (buttons/inputs), refined card/table styling, backend health indicator, and header theme toggle.
 - Detection heuristics tightened to treat file extensions as hints for confidence rather than gates; content signals drive detection.
 - YAML/INI gating strengthened to avoid extension-only classification; YAML requires structural signals; INI avoids extension-based shortcuts.
 - JSON and TOML now surface oddity hints (e.g., parse failures, trailing commas, bare keys) via metadata for manual review.
 - XML plugin adds an optional well-formedness check (bounded sample) and marks malformed samples for review while still reporting structure cues.

### Fixed
- Test import collision for the `scripts` package during collection.

### Docs
- Comprehensive registry docs: API, offline runner, and GUI notes.
- Updated detection types and format support matrices to include registry-live.
 - Updated GUI guide/notes and README to cover theme toggle, health indicator, button variants, and table/settings refinements.

## [0.0.2] - 2025-10-16

Initial public structure refresh: JSON/XML/INI detectors, offline runner,
profile/hunt helpers, GUI scaffolding, and packaging scripts.


[Unreleased]: https://example.invalid/driftbuster/compare/v0.0.2...HEAD
[0.0.2]: https://example.invalid/driftbuster/releases/tag/v0.0.2
