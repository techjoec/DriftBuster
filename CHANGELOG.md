# Changelog

All notable changes to this project are documented here. This file complements
component-level logs under `notes/changelog/` and the per-release notes under
`notes/releases/`.

This format follows a simplified Keep a Changelog style: sections are grouped
by Added, Changed, Fixed, and Docs.

## [Unreleased]

### Added
- `registry-live` format plugin for registry scan definition manifests (JSON/YAML).
- Windows Registry live scan utilities (enumerate apps, suggest roots, search).
- Offline runner support for `registry_scan` sources; writes `registry_scan.json`.

### Changed
- Catalog includes `RegistryLive` class; validation maps `registry-live`.

### Fixed
- Test import collision for the `scripts` package during collection.

### Docs
- Comprehensive registry docs: API, offline runner, and GUI notes.
- Updated detection types and format support matrices to include registry-live.

## [0.0.2] - 2025-10-16

Initial public structure refresh: JSON/XML/INI detectors, offline runner,
profile/hunt helpers, GUI scaffolding, and packaging scripts.


[Unreleased]: https://example.invalid/driftbuster/compare/v0.0.2...HEAD
[0.0.2]: https://example.invalid/driftbuster/releases/tag/v0.0.2
