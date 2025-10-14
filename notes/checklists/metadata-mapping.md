# Metadata Mapping Checklist

Track detector outputs against catalog identifiers to ensure metadata stays
aligned across releases.

## Fixture Catalog Mapping

| Fixture Path | Expected Format | Expected Variant | Notes |
|--------------|----------------|------------------|-------|
| `fixtures/xml/sample.resx` | `xml` | `resource-xml` | Resource XML metadata keys present. |
| `fixtures/config/web.config` | `structured-config-xml` | `web-config` | Transform flags recorded when applicable. |
| `fixtures/binary/config.dat` | `binary-dat` | `generic` | Verify fallback metadata only. |

## Framework Config Manual Verification

Confirm each canonical `.config` role produces the expected metadata and record
the supporting diff snippet paths.

| Variant | Fixture Path | Checklist | Diff Snippet |
|---------|--------------|-----------|--------------|
| web-config | `fixtures/config/web.config` | ☐ Variant ☐ Role ☐ Namespace captured | `notes/snippets/xml-config-diffs.md#web-config` |
| app-config | `fixtures/config/app.config` | ☐ Variant ☐ Role ☐ Namespace captured | `notes/snippets/xml-config-diffs.md#app-config` |
| machine-config | `fixtures/config/machine.config` | ☐ Variant ☐ Role ☐ Namespace captured | `notes/snippets/xml-config-diffs.md#machine-config` |
| web-config-transform | `fixtures/config/web.Release.config` | ☐ Variant ☐ Transform scope | `notes/snippets/xml-config-diffs.md#web-config-transform` |

- Store before/after metadata payloads in the referenced snippet file (use
  sanitised JSON only) and log the manual run date beside each section.
- Link any hunt token observations back to
  `notes/checklists/xml-config-verification.md` so drift analysis stays
  traceable.

## Diff Tracking

Record before/after `summarise_metadata` payloads to detect regressions.

- Capture baseline JSON via ``summarise_metadata`` prior to code changes.
- After modifications, re-run the detector and compare payloads with
  ``python -m json.tool`` or ``jq`` to highlight key differences.
- Paste noteworthy diffs below (link to commit/branch when possible):

```
<!-- Example:
- config/web.config
  - Before: catalog_variant="web-config"
  - After:  catalog_variant="web-config-transform" (expected after transform detection change)
-->
```

## Anomalies & Follow-ups

Log unexpected metadata and assign an owner for follow-up.

| Fixture | Issue | Owner | Notes |
|---------|-------|-------|-------|
| `fixtures/xml/custom.xml` | Missing `catalog_variant` | TBD | Investigate plugin coverage. |
