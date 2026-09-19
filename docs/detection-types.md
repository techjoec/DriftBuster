# Detection Types Reference

The detection catalog (`DetectionCatalog.Default`, built in
`gui/DriftBuster.Backend/Detection/Catalog/DetectionCatalogData.cs`)
holds the canonical detection metadata consumed by the core detector; the usage
percentages are survey estimates kept in this document. Every plugin runs and the
highest confidence wins; a tie goes to the plugin earlier in priority order. All claims are kept as the match's
candidates, strongest first (`scan --json` prints them as `candidates`). The tables below blend the shipped class definitions from
the catalog (v0.0.4) with the usage insights from the format survey data
(v0.0.4).

## Active Detection Classes

| Priority | Class Name              | Catalog Format        | Default Severity | Primary Variant / Notes                 | Key Extensions                      | Usage % | Detection Cues                |
|----------|------------------------|-----------------------|------------------|-----------------------------------------|-------------------------------------|---------|-------------------------------|
| 10       | RegistryExport         | registry-export       | high             | regedit5 (UTF-16LE), regedit4            | `.reg`                               | 10      | export header + `[HKEY_…]` sections |
| 15       | RegistryLive           | registry-live         | medium           | scan-definition                          | `.json`, `.yml`, `.yaml`             | —       | `registry_scan` manifest key  |
| 20       | StructuredConfigXml    | structured-config-xml | high             | web/app/machine + transform variants     | `.config`                            | 12      | filename + section hints      |
| 30       | XmlGeneric             | xml                   | medium           | generic, msbuild, manifest/resource/XAML | `.xml`, `.manifest`, `.resx`, `.xaml` | 14     | namespace + root metadata     |
| 40       | Json                   | json                  | medium           | generic / jsonc / structured-settings    | `.json`, `.jsonc`                    | 22      | bracket balance + parse       |
| 50       | Yaml                   | yaml                  | medium           | generic / kubernetes-manifest            | `.yml`, `.yaml`                      | 8       | key/colon indentation         |
| 60       | Toml                   | toml                  | medium           | generic, array-of-tables, package-manifest-toml, project-settings-toml | `.toml`                              | 4       | bracketed sections + `=`      |
| 70       | Ini                    | ini                   | medium           | sectioned-ini, dotenv, hybrid, desktop   | `.ini`, `.cfg`, `.cnf`               | 15      | section headers + key density + extension hints |
| 75       | Hcl                    | hcl                   | high             | hashicorp-nomad / -vault / -consul, generic | `.hcl`                          | —       | extension + block/attribute structure |
| 80       | KeyValueProperties     | properties            | medium           | java-properties                          | `.properties`                        | 3       | extension + `=`/`:` pairs + continuations |
| 90       | UnixConf               | unix-conf             | high             | directive-conf + apache/nginx/SSH/VPN    | `.conf`                              | 2       | directive keywords + comment markers |
| 100      | ScriptConfig           | script-config         | high             | ps1-shell, batch-script, cmd-shell, vbscript; generic (Dockerfiles) | `.ps1`, `.bat`, `.cmd`, `.vbs`, `Dockerfile` | 4 | per-language constructs (`param(`, `set NAME=`, `Dim`, …) |
| 110      | EmbeddedSqlDb          | embedded-sql-db       | high             | —                                       | `.sqlite`, `.db`                     | 2       | page-structured signature     |
| 120      | GenericBinaryDat       | binary-dat            | low              | —                                       | `.dat`, `.bin`                       | 3       | entropy threshold             |
| 130      | Plist                  | plist                 | medium           | xml-or-binary                            | `.plist`                             | 0.5     | `bplist00` header             |
| 140      | MarkdownConfig         | markdown-config       | low              | embedded-yaml-frontmatter                | `.md`                                | 0.5     | leading `---` YAML front matter |
| 1000     | UnknownTextOrBinary    | unknown-text-or-binary | info            | fallback                                 | _fallback_                           | —       | —                             |

Default severity labels mirror the canonical values embedded in the catalog
(`DetectionCatalogData.cs`) so console and registry summaries share a single
source of truth.

## Severity and Remediation Hints

`DetectionMetadata.ValidateDetectionMetadata` injects catalog-provided severity hints and
remediation stubs for every detection class. Use these entries to brief
reviewers and to script downstream workflows without hard-coding guidance.

### RegistryExport

- **Severity hint:** Registry exports capture entire hive snapshots, including
  secrets, policy settings, and service fingerprints.
- **Remediations:**
  - `registry-export-lockdown` (secrets): Store exported hives in restricted
    evidence shares and rotate credentials referenced in the dump.

### RegistryLive

- **Severity hint:** Registry scan definitions describe automated hive reads
  and target tokens that expose sensitive audit scope.
- **Remediations:**
  - `registry-live-scope-review` (review): Confirm monitoring tokens align with
    approved hosts and rotate any credentials referenced in the manifest.

### StructuredConfigXml

- **Severity hint:** Application configuration files expose secrets,
  connection strings, and runtime policy toggles that impact production
  systems.
- **Remediations:**
  - `structured-config-rotate-secrets` (secrets): Rotate credentials stored in
    configuration sections and confirm transforms match approved deployment
    scopes.
  - `structured-config-hardening` (hardening): Review debug switches and
    permissive runtime settings before promoting captured configs to shared
    baselines.

### Xml

- **Severity hint:** Generic XML manifests advertise capabilities, endpoints,
  and policy grants that can expose infrastructure layout when leaked.
- **Remediations:**
  - `xml-provenance-review` (review): Confirm manifest namespaces and
    deployment identifiers map to approved environments before sharing samples
    externally.
  - `xml-sanitise-identifiers` (sanitisation): Strip unique identifiers or
    replace them with anonymised tokens prior to archiving manifests in shared
    stores.

### Json

- **Severity hint:** JSON configuration files reveal feature flags, API
  endpoints, and secrets that map directly to runtime access.
- **Remediations:**
  - `json-secret-rotation` (secrets): Rotate keys or tokens stored in captured
    JSON configs and ensure redacted copies replace archival snapshots.
  - `json-flag-review` (review): Audit feature toggles and environment
    overrides before applying configs to ensure they respect approved
    deployment policies.

### Yaml

- **Severity hint:** YAML manifests encode infrastructure state, secrets
  references, and rollout policies that leak environment topology.
- **Remediations:**
  - `yaml-secret-reference-audit` (review): Audit Secret and ConfigMap
    references before distributing manifests and scrub environment identifiers
    when possible.
  - `yaml-deployment-scope` (hardening): Verify namespace and replica settings
    to prevent accidental cross-environment rollouts when replaying manifests.

### Toml

- **Severity hint:** TOML project manifests reveal dependency feeds, signing
  requirements, and build output paths that identify release pipelines.
- **Remediations:**
  - `toml-feed-audit` (review): Review [[tool]] sections for internal
    registries or credentials and relocate them to secure secret stores before
    sharing manifests.
  - `toml-build-scope` (hardening): Sanitise path and signing configuration to
    avoid leaking build infrastructure details in exported manifests.

### Ini

- **Severity hint:** INI and dotenv style files often embed credentials,
  tokens, and environment toggles that impact access control immediately.
- **Remediations:**
  - `ini-secret-rotation` (secrets): Rotate secrets surfaced in dotenv or
    credential sections and confirm masked samples replace raw exports.
  - `ini-sanitisation-workflow` (sanitisation): Follow the sanitisation
    workflow before sharing dotenv fixtures to prevent leaking production
    values. See `scripts/fixtures/README.md` for the scrub steps referenced by
    this remediation entry.

<a id="ini-dotenv"></a>
#### Dotenv variant

- **Variant severity hint:** Dotenv environment files commonly store plaintext
  secrets, service URLs, and deployment toggles that require immediate
  rotation when discovered.
- **Variant remediations:**
  - `ini-dotenv-rotate-secrets` (secrets): Rotate keys stored in dotenv files
    and replace evidence copies with sanitised variants before sharing.
  - `ini-dotenv-sanitise-identifiers` (sanitisation): Strip hostnames and
    environment identifiers from dotenv exports prior to archiving or
    distribution.

### Hcl

- **Severity hint:** HCL configurations describe Nomad jobs, Vault listeners
  and seals, and Consul agents, and routinely embed tokens, TLS material
  paths, and cluster addresses.
- **Remediations:**
  - `hcl-token-rotation` (secrets): Rotate tokens and unseal material
    referenced in captured HCL and replace archived copies with redacted
    variants.
  - `hcl-listener-review` (hardening): Review listener, TLS, and ACL stanzas
    against hardened baselines before redeploying captured configs.

### KeyValueProperties

- **Severity hint:** Java-style properties files concentrate service
  endpoints, credentials, and feature toggles for entire JVM applications.
- **Remediations:**
  - `properties-credential-scan` (secrets): Scan captured properties for
    passwords or tokens and migrate them into managed secret stores
    immediately.
  - `properties-comment-scrub` (sanitisation): Review inline comments for
    deployment notes or hostnames and redact sensitive context before sharing.

### UnixConf

- **Severity hint:** Unix configuration files govern listeners, crypto
  policies, and authentication hooks that immediately influence service
  exposure.
- **Remediations:**
  - `unix-conf-hardening` (hardening): Review captured directives against
    hardened baselines and disable permissive modules before redeploying
    configs.
  - `unix-conf-access-review` (review): Confirm referenced key, certificate,
    and log paths carry restricted permissions before sharing archives.

### ScriptConfig

- **Severity hint:** Script-based configs can execute arbitrary changes, embed
  credentials, and provision infrastructure when replayed without review.
- **Remediations:**
  - `script-config-scope` (review): Validate script scopes and ensure they run
    against lab environments before applying to production hosts.
  - `script-config-secret-hygiene` (secrets): Replace inline credentials with
    secure parameter stores and scrub tokens before archiving scripts.

### EmbeddedSqlDb

- **Severity hint:** Embedded SQLite databases retain raw operational data,
  including user records and tokens, making them high-risk evidence.
- **Remediations:**
  - `embedded-sql-redaction` (sanitisation): Mask or drop sensitive rows before
    distributing captured databases and document transformations in the
    evidence log.
  - `embedded-sql-retention` (retention): Apply the 30-day retention policy
    and record purge decisions once investigations close.

### GenericBinaryDat

- **Severity hint:** Opaque binary blobs are unclassified evidence; treat them
  cautiously until confirmed non-sensitive.
- **Remediations:**
  - `binary-dat-triage` (review): Triages samples with dedicated tooling before
    storing them long term to determine whether further sanitisation is
    required.
  - `binary-dat-redaction` (sanitisation): If the blob contains extracted
    credentials or certificates, replace it with hashed summaries before
    sharing.

### Plist

- **Severity hint:** Property lists carry application preferences, launch
  agent definitions, and embedded credentials for desktop and mobile tooling.
- **Remediations:**
  - `plist-secret-review` (secrets): Review captured property lists for stored
    tokens or account identifiers and redact them before sharing.

### MarkdownConfig

- **Severity hint:** Markdown front matter carries site generator settings and
  publishing metadata that can expose author identities and internal paths.
- **Remediations:**
  - `markdown-frontmatter-scrub` (sanitisation): Strip author, host, and path
    identifiers from front matter before archiving captured documents.

INI-family detectors rank structural evidence (section headers, directive blocks, brace hybrids) ahead of extension-only cues so shared `.conf` and `.properties` suffixes keep their dedicated variants. Dotenv matches remain gated by known filenames and export/`=` density, allowing Java properties to retain the `java-properties` variant even when sections are absent.

### Plugin Families and Aliases

Some format plugins expose families that normalise to existing catalog classes to keep reporting stable while we trial heuristics:

- `dockerfile` → catalog `script-config` (shared “script-like” detection family). Metadata and reasons remain Dockerfile-specific.
- `hcl` → catalog `hcl` with `hashicorp-nomad`, `hashicorp-vault`, `hashicorp-consul` and `generic` variants.

### Embedded Variants

- **XmlGeneric** — variants `app-manifest-xml`, `resource-xml`, and `interface-xml`
  rely on namespace and root element metadata. They take priorities `34–36`
  (`msbuild-*` are `31–33`, `xslt-xml` is `37`) and fall back to `generic`
  only after namespace checks fail.
- **Json** — variants `jsonc`, `structured-settings-json`, and
  `runtime-package-json` are tracked in the format survey.
  The catalog implements the first two (`41–42`). The core
  plugin surfaces `structured-settings-json` for `appsettings*.json` payloads
  (filename or `ConnectionStrings`/`Logging` keys) and promotes `jsonc` when
  inline or block comments are detected outside string literals.
- **Toml** — `package-manifest-toml` (62) for `Cargo.toml` or files with
  `[package]`, `[project]`, `[build-system]` or `[tool.poetry…]` tables;
  `project-settings-toml` (63) for tool configuration files (`rustfmt.toml`,
  `ruff.toml`, `netlify.toml`, `.cargo/config.toml`, …) or files whose tables
  are all `[tool.*]`; otherwise `array-of-tables` (61) or `generic`.
- **ScriptConfig** — the `script` plugin emits `ps1-shell`, `batch-script`,
  `cmd-shell` and `vbscript` from each language's constructs (two with a
  script extension, three without); `.cmd` picks `cmd-shell`. Dockerfiles
  report `generic` through the `dockerfile` alias.

## Usage Notes

- Types may specify filename regexes as well as extensions. Treat both as
  heuristics when authoring plugins.
- `.config` matches expose `config_original_filename`,
  `config_role`, and `config_transform_scope` so adapters can
  surface scope-aware drift summaries. Manual logs pair those with
  `config_ingested_at`, `config_sanitised_at`, and
  `config_verified_at` timestamps for the neutral
  `web-config`/`app-config`/`machine-config` families plus the
  `web-config-transform` variant.
- XML matches expose `root_local_name` and `root_namespace` metadata to
  help downstream tools differentiate between manifest, resource, and XAML
  payloads even when filenames are ambiguous.
- `schema_locations` metadata records XSD provenance for XML payloads,
  and `.resx` files include `resource_keys` previews for quick auditing.
- XML detections attach `attribute_hints` metadata so hunt mode can align
  connection strings, service endpoints, and feature flags with profile
  expectations.
- MSBuild `.targets`, `.props`, and project payloads emit `msbuild_*`
  metadata fields summarising default targets, SDK declarations, and hashed
  import references to help diff tooling track build graph drift.
- Binary detectors rely on sampling thresholds; large opaque files may need
  increased sample sizes for confident matches.
- When multiple detectors might claim a file, adjust priorities so the most
  specific rule executes first.
- Inject experimental plugins by passing an explicit plugin list to `Detector`
  and set `sortPlugins: false` if the order should stay untouched during manual
  testing.
- Registry names must stay unique—registering a different implementation
  with the same `Name` throws `ArgumentException` so collisions surface
  immediately.

### Detection Metadata

Detections ship with a normalised metadata dictionary that the
`DetectionMetadata.ValidateDetectionMetadata` helper keeps aligned with the catalog. Keys are
lowercase slugs to remain JSON friendly and arrive pre-sanitised for adapters.

| Key               | Description                                                   | Example Value |
|-------------------|---------------------------------------------------------------|---------------|
| `catalog_version` | Detection catalog version embedded in the match payload.     | `0.0.4`     |
| `catalog_format`  | Canonical catalog identifier resolved from `format_name`.   | `xml`       |
| `catalog_variant` | Optional variant slug derived from `DetectionMatch.variant`.| `resource-xml`  |
| `bytes_sampled`   | Number of bytes the detector analysed for the match.         | `65536`     |
| `encoding`        | Text codec used when content decoding succeeded.             | `utf-8`     |
| `sample_truncated`| Present when sampling hit the configured guardrail.          | `true`      |

Sample metadata payload:

```json
{
  "catalog_version": "0.0.4",
  "catalog_format": "xml",
  "catalog_variant": "resource-xml",
  "bytes_sampled": 65536,
  "encoding": "utf-8"
}
```

Keep this document synchronised with `DetectionCatalogData.cs` and the format
survey data whenever priorities, variants, or usage assumptions change.
