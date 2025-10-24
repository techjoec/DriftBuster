# Catalog Review Summary — 2025-10-24

- Confirmed `driftbuster.catalog.DETECTION_CATALOG` now embeds canonical slugs, default severities, and variant aliases for each class.
- Verified alias lookups (`dockerfile`, `env-file`, `embedded-sqlite`) resolve through the metadata-driven mapping in `validate_detection_metadata`.
- Recorded that strict validation rejects unknown variants while non-strict mode continues to normalise custom formats for downstream tooling.
