# DriftBuster XML Format Changelog

# 0.0.4 — 2025-10-17
- Recognise MSBuild project, props, and targets payloads with dedicated variants
  plus metadata covering default targets, SDK declarations, and import hashes.
- Surface MSBuild-derived reasons (default targets, SDK, imports) and boost
  confidence for matches enriched with project metadata.
- Extend XML plugin tests to validate MSBuild metadata hashing and variant
  classification for both legacy and SDK-style project files.

# 0.0.3 — 2025-10-16
- Surface ``attribute_hints`` metadata covering connection strings, service
  endpoints, and feature flags with hashed values for drift tracking.
- Extend XML handling to treat `.targets` payloads as first-class XML and add
  hunt-aligned reasons for captured attribute hints.
- Expand hunt rules to include connection-string, service-endpoint, and
  feature-flag tokens aligned with the new metadata fields.

## 0.0.2 — 2025-10-15
- Capture XML schema provenance in ``schema_locations`` metadata and surface
  matching detection reasons.
- Extract `.resx` resource key previews and expose them via ``resource_keys``
  along with supporting detection reasons.
- Bump XML plugin version to ``0.0.2`` to reflect the new metadata contract.

## 0.0.1
- Initial heuristic implementation covering `.config`, manifest, `.resx`, and
  XAML payloads with namespace-aware metadata.
