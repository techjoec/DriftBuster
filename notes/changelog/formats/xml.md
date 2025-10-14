# DriftBuster XML Format Changelog

## 0.0.2 — 2025-10-15
- Capture XML schema provenance in ``schema_locations`` metadata and surface
  matching detection reasons.
- Extract `.resx` resource key previews and expose them via ``resource_keys``
  along with supporting detection reasons.
- Bump XML plugin version to ``0.0.2`` to reflect the new metadata contract.

## 0.0.1
- Initial heuristic implementation covering `.config`, manifest, `.resx`, and
  XAML payloads with namespace-aware metadata.
