# Changelog

All notable changes to this project are documented here. This file complements
component-level logs under `notes/changelog/` and the per-release notes under
`notes/releases/`.

This format follows a simplified Keep a Changelog style: sections are grouped
by Added, Changed, Fixed, and Docs.

## [0.2.0] - 2026-09-17

### Changed
- The whole engine runs on .NET: detection, diff, hunt and secret scanning, multi-server orchestration, profiles, scheduling, registry scan, SQL export, reporting and capture live in `DriftBuster.Backend`. No Python interpreter or package is needed anywhere.
- The GUI multi-server tab runs scans in process instead of starting an external engine.
- The PowerShell module requires PowerShell 7.6 and no longer starts external processes.
- The offline runner (`scripts/driftbuster-offline-runner.ps1`) runs on Windows PowerShell 5.1 with nothing to install.
- Release builds of the GUI and console tool are self-contained publishes that carry the .NET runtime.

### Added
- `driftbuster` console tool with `scan`, `diff`, `hunt`, `multi-server`, `profile`, `detection-profile`, `schedule`, `registry-scan`, `sql-export`, `report`, `capture`, `version`, `release` and `maint` commands.

### Fixed
- Config identities no longer collide across applications that share a file name.
- One unreadable file no longer fails a whole host scan; it is skipped and reported.
- Catalog validation accepts valid plugin output for binary plists, Markdown front matter, Logstash and HCL variants.
- XML canonicalisation keeps namespace prefixes.
- The install-path hunt rule matches Windows paths.
- The diff planner detects content type for every file instead of relying on an extension list.
- Secret redaction always terminates.

### Removed
- Slack, Teams and SMTP notification adapters.

## [0.0.3]

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
- GUI: Cleared the Avalonia 11.2 release-blocker by realigning results catalog sorting and toast resource lookups with updated build/test guidance.

### Docs
- Comprehensive registry docs: API, offline runner, and GUI notes.
- Updated detection types and format support matrices to include registry-live.
- Updated GUI guide/notes and README to cover theme toggle, health indicator, button variants, and table/settings refinements.
- Logged the Dark+/Light+ palette refresh with new release-ready captures and manifest updates under `docs/assets/themes/` and `docs/ux-refresh.md`.
- Added Phase 6 release validation checklist in `notes/status/gui-research.md` summarising coverage, smoke status, and pending evidence bundles.

## [0.0.2] - 2025-10-16

Initial public structure refresh: JSON/XML/INI detectors, offline runner,
profile/hunt helpers, GUI scaffolding, and packaging scripts.


[0.2.0]: https://example.invalid/driftbuster/compare/v0.0.3...v0.2.0
[0.0.3]: https://example.invalid/driftbuster/compare/v0.0.2...v0.0.3
[0.0.2]: https://example.invalid/driftbuster/releases/tag/v0.0.2
