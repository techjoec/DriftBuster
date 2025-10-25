# Fixture Catalog Provenance

This index tracks the provenance for each fixture directory so compliance reviews
and vendor sample rehearsals can reference sanitisation steps quickly.

| Directory | Contents snapshot | Source reference | Sanitisation highlights |
| --------- | ----------------- | ---------------- | ----------------------- |
| `binary/` | SQLite preferences + binary plist pairs | Generated via local scripts documented in `fixtures/binary/README.md` | Contains only placeholder keys and values; hashes recorded in MANIFEST.json |
| `config/` | Mixed `.config`, `appsettings.json`, and `.env` templates | Derived from default framework templates and open-source samples | Vendor names replaced with neutral identifiers; secrets converted to environment variables |
| `font_telemetry/` | JSON telemetry captures around stale vs healthy runs | Captured from synthetic drift scenarios in `tests/scripts/test_font_health_summary.py` | Hostnames anonymised; timestamps clamped to ±24h of capture note |
| `sql/` | Schema excerpts and redacted exports | Built from synthetic database definitions documented in `fixtures/sql/README.md` | Table/column names generic; sample rows replaced with hashed placeholders |
| `xml/` | Namespace provenance manifest | Authored specifically for namespace testing (`fixtures/xml/README.md`) | Uses `urn:example:driftbuster:*` URNs and synthetic IDs |
| `yaml/` | Structured config variations | Generated from canonical templates referenced in `fixtures/yaml/README.md` | Contains deterministic placeholders and neutral hostnames |
| `vendor_samples/` | Telemetry + directory integration samples | Adapted from public vendor documentation motifs; see `fixtures/vendor_samples/README.md` | Hostnames swapped to `example.invalid`, tokens replaced with environment placeholders, and hashing guidance recorded |

When adding new fixtures, update the relevant subdirectory README and append a
row here with sourcing + sanitisation notes. Avoid storing proprietary
artifacts; prefer synthetic or publicly documented structures with neutral
naming.
