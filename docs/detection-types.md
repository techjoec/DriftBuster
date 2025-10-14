# Detection Types Reference

`driftbuster.catalog` exposes the canonical detection metadata consumed by the
core detector (`DETECTION_CATALOG`) alongside usage-oriented estimates
(`FORMAT_SURVEY`). Detection runs in ascending priority order; the first
positive match wins. The tables below blend the shipped class definitions from
`DETECTION_CATALOG` (v0.0.1) with the usage insights from the format survey data
(v0.0.1).

## Active Detection Classes

| Priority | Class Name              | Catalog Format        | Primary Variant                | Key Extensions                     | Usage % | Detection Cues                |
|----------|------------------------|-----------------------|--------------------------------|------------------------------------|---------|-------------------------------|
| 10       | RegistryExport         | registry-export       | —                              | `.reg`                             | 10      | signature + prefix            |
| 20       | StructuredConfigXml    | structured-config-xml | web/app/machine + transforms   | `.config`                          | 12      | filename + section hints      |
| 30       | XmlGeneric             | xml                   | namespace-driven               | `.xml`, `.manifest`, `.resx`, `.xaml` | 14      | namespace + root metadata     |
| 40       | Json                   | json                  | generic                 | `.json`, `.jsonc`                  | 22      | bracket balance + parse       |
| 50       | Yaml                   | yaml                  | —                      | `.yml`, `.yaml`                    | 8       | key/colon indentation         |
| 60       | Toml                   | toml                  | —                      | `.toml`                            | 4       | bracketed sections + `=`      |
| 70       | Ini                    | ini                   | —                      | `.ini`, `.cfg`, `.cnf`             | 15      | section headers + `=`         |
| 80       | KeyValueProperties     | properties            | key-value-properties    | `.properties`                      | 3       | `=` / `:` pairs               |
| 90       | UnixConf               | unix-conf             | —                      | `.conf`                            | 2       | directives + `#`/`;` comments |
| 100      | ScriptConfig           | script-config         | shell-automation        | `.ps1`, `.bat`, `.cmd`, `.vbs`     | 4       | shebang/keyword scan          |
| 110      | EmbeddedSqlDb          | embedded-sql-db       | —                      | `.sqlite`, `.db`                   | 2       | page-structured signature     |
| 120      | GenericBinaryDat       | binary-dat            | —                      | `.dat`, `.bin`                     | 3       | entropy threshold             |
| 1000     | UnknownTextOrBinary    | —                     | —                      | _fallback_                         | —       | —                             |

### Embedded Variants

- **XmlGeneric** — variants `app-manifest-xml`, `resource-xml`, and `interface-xml`
  rely on namespace and root element metadata. They inherit the 30-series
  priorities (`31–33`) defined in `DETECTION_CATALOG` and fall back to
  `generic` only after namespace checks fail.
- **Json** — variants `jsonc`, `structured-settings-json`, and
  `runtime-package-json` are tracked in the format survey.
  `DETECTION_CATALOG` currently implements the first two (`41–42`). The core
  plugin surfaces `structured-settings-json` for `appsettings*.json` payloads
  (filename or `ConnectionStrings`/`Logging` keys) and promotes `jsonc` when
  inline or block comments are detected outside string literals.
- **Toml** — `PackageManifestToml` (61) and `ProjectSettingsToml` (62) cover
  common manifest and workspace descriptors.
- **ScriptConfig** — plan to surface the PowerShell, Batch, CMD, and VBScript
  flavours as metadata for downstream tooling once the core heuristics are in
  place.

## Backlog Formats

The format survey introduces four additional configurations to cover once the
core and XML work stabilise. They are not yet represented in
`DETECTION_CATALOG`.

| Catalog Format         | Variant                     | Key Extensions | Usage % | Detection Cues              |
|------------------------|-----------------------------|----------------|---------|-----------------------------|
| markdown-config        | embedded-yaml-frontmatter   | `.md`          | 0.5     | YAML front matter           |
| property-list          | xml-or-binary               | `.plist`       | 0.5     | header magic + XML decl     |
| ini-json-hybrid        | engine-hybrid               | `.ini`         | 0.5     | section headers + `{}` mix  |
| env-file               | dotenv                      | `.env`         | 1       | `KEY=VALUE` lines           |

> Meta note: the survey reports `total_formats = 17`, but the `formats` array
> currently enumerates 16 entries. Keep that discrepancy in mind if the data
> source refreshes.

## Implementation Order

1. **Core module** — stabilise detector orchestration, sampling, and the
   priority/metadata schema (in progress).
2. **XML family** — complete the `.config` and XML plugin coverage (structured
   config + generic XML variants).
3. **Remaining formats** — iterate through the rest of the catalog, starting
   with the highest-usage families (JSON, INI, YAML, TOML, etc.), then expand
   into the backlog formats above.

## Usage Notes

- Types may specify filename regexes as well as extensions. Treat both as
  heuristics when authoring plugins.
- `.config` matches now expose ``config_original_filename``,
  ``config_role``, and ``config_transform_scope`` so adapters can
  surface scope-aware drift summaries. Manual logs now pair those with
  ``config_ingested_at``, ``config_sanitised_at``, and
  ``config_verified_at`` timestamps for the neutral
  ``web-config``/``app-config``/``machine-config`` families plus the
  ``web-config-transform`` variant.
- XML matches expose ``root_local_name`` and ``root_namespace`` metadata to
  help downstream tools differentiate between manifest, resource, and XAML
  payloads even when filenames are ambiguous.
- Binary detectors rely on sampling thresholds; large opaque files may need
  increased sample sizes for confident matches.
- When multiple detectors might claim a file, adjust priorities so the most
  specific rule executes first.
- Inject experimental plugins via ``driftbuster.register`` +
  ``driftbuster.get_plugins(readonly=True)`` and disable ``sort_plugins`` if the
  registry order should stay untouched during manual testing.
- Registry names must stay unique—re-registering a different implementation
  with the same ``plugin.name`` raises ``ValueError`` so collisions surface
  immediately.

### Detection Metadata

Detections now ship with a normalised metadata dictionary that the
``validate_detection_metadata`` helper keeps aligned with the catalog. Keys are
lowercase slugs to remain JSON friendly and arrive pre-sanitised for adapters.

| Key               | Description                                                   | Example Value |
|-------------------|---------------------------------------------------------------|---------------|
| ``catalog_version`` | Detection catalog version embedded in the match payload.     | ``0.0.1``     |
| ``catalog_format``  | Canonical catalog identifier resolved from ``format_name``.   | ``xml``       |
| ``catalog_variant`` | Optional variant slug derived from ``DetectionMatch.variant``.| ``resource-xml``  |
| ``bytes_sampled``   | Number of bytes the detector analysed for the match.         | ``65536``     |
| ``encoding``        | Text codec used when content decoding succeeded.             | ``utf-8``     |
| ``sample_truncated``| Present when sampling hit the configured guardrail.          | ``true``      |

Sample metadata payload::

    {
        "catalog_version": "0.0.1",
        "catalog_format": "xml",
        "catalog_variant": "resource-xml",
        "bytes_sampled": 65536,
        "encoding": "utf-8",
        "sample_truncated": false
    }

Keep this document synchronised with `driftbuster/catalog.py` and the format
survey data whenever priorities, variants, or usage assumptions change.
