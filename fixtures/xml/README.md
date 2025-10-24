# XML Fixtures

This directory stores anonymised XML samples that exercise the namespace
provenance logging added to the XML detector.

## Files

| File | Purpose | Namespace provenance |
|------|---------|----------------------|
| `namespace_provenance_sample.xml` | Synthetic application manifest with multiple namespace declarations. | Default namespace `urn:example:driftbuster:manifest`, prefixes `compat`/`provenance` for compatibility + audit notes. |

## Provenance notes

- All namespace URIs use the neutral `urn:example:driftbuster:*` pattern so no
  vendor identifiers leak into sample metadata.
- The detector records each declaration with a short SHA-1 hash of the
  `xmlns` attribute.  To reproduce the preview hash for the default namespace:

  ```python
  import hashlib

  hashlib.sha1("xmlns|urn:example:driftbuster:manifest".encode("utf-8")).hexdigest()[:12]
  ```

- The manifest fixture matches the legal guardrails in
  `docs/legal-safeguards.md#xml-namespace-fixtures` and can be redistributed as
  part of regression evidence bundles.
