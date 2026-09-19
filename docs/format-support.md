# Format Support Matrix

This document tracks the configuration formats that DriftBuster understands
today, their current maturity, and the module versions declared by each format
plugin. Versions come from each plugin class's `Version` property and are
reported by `FormatRegistry.PluginVersions()`, so keep this table in step with
the code.

| Format family            | Variants / focus                                                 | Plugin | Module version | Status       | Notes |
|--------------------------|------------------------------------------------------------------|--------|----------------|--------------|-------|
| Structured configuration | `.config` web/app/machine files, build transforms, assembly sidecars | xml    | 0.0.6          | Stabilising  | Transform scope, precedence, schema provenance, attribute hints, MSBuild metadata, and line-level namespace provenance hashes are populated. |
| Generic XML              | Application manifests (`.manifest`), resources (`.resx`), XAML UI | xml    | 0.0.6          | Stabilising  | Namespace logging captures per-declaration hashes + line numbers, schema provenance, `.resx` resource keys, MSBuild project detection, and attribute hints surface alongside hunt-aligned tokens. |
| JSON                     | Generic JSON, comment-friendly `jsonc`, `appsettings*.json`      | json   | 0.0.3          | Preview      | Large-sample validation and sampling guardrails are still being tuned. |
| Registry export          | Registry Editor exports (`.reg`), version 5 (UTF-16LE) and `REGEDIT4` | registry-export | 0.0.1  | Preview      | Requires the export header; records the editor version, hives, key and value counts, and deletions. |
| Registry live scan       | JSON/YAML scan manifests (`registry_scan` token/keywords/patterns) | registry-live | 0.0.1      | Preview      | Integrates live Windows Registry hunts via definition files. |
| INI                       | Classic/sectionless, dotenv gating, directive spillover metadata | ini    | 0.0.2          | Preview     | Records encoding, comment style, sensitive key hints, and classifies dotenv/unix-conf/hybrid variants for remediation planning. |
| YAML                     | Generic YAML, Kubernetes manifest hints                           | yaml   | 0.0.3          | Preview      | Parser-free heuristics capture document markers, indentation tolerances, review metadata, and `apiVersion`/`kind` hints. |
| Conf DSL                 | Logstash pipeline configs (`input`/`filter`/`output` blocks)      | conf   | 0.0.1          | Preview      | Tight heuristics avoid stealing `.conf` INI-like files covered by the INI plugin. |
| Text config              | Directive-style configs (OpenSSH, OpenVPN)                        | text   | 0.0.2          | Preview      | Fallback detector for whitespace-delimited directives; filename/content hints refine variants. |
| TOML                     | Generic TOML, arrays of tables, package manifests (Cargo, pyproject), tool settings files | toml   | 0.0.4          | Preview      | Detects `[table]`, `[[array-of-tables]]`, inline tables, spacing tolerances, dotted keys, quoted/array values; parser-free. Outside `.toml` files it needs a TOML-only construct, so sectioned `key=value` INI files stay INI. |
| HCL                      | HashiCorp configs (Nomad/Vault/Consul)                            | hcl    | 0.0.1          | Preview      | Detects `job {}`, `server {}`, `listener {}`, `seal {}` blocks + `key = value` pairs. |
| Script config            | PowerShell, batch, CMD and VBScript                               | script | 0.0.1          | Preview      | Counts each language's constructs line by line; runs before XML so a script embedding XML stays a script. |
| Dockerfile               | Multi-stage builds and directives                                 | dockerfile | 0.0.1       | Preview      | Filename/Dockerfile hint, `FROM` on first non-comment line, and common directives (RUN/COPY/ARG). |
| Binary hybrid            | SQLite databases, binary property lists, Markdown with YAML front matter | binary-hybrid | 0.1.0 | Preview | Header signatures on the raw sample; SQLite table count and plist top-level keys recorded as metadata. |

## Settings comparison

A multi-server scan compares every file setting by setting, so a difference reads as a named setting with each server's
value rather than as diff lines. Files are matched across servers by their path under the scanned root. Each format's
settings are named like this:

| Format | Setting names |
|--------|---------------|
| .NET XML configs | `appSettings:<key>`, `connectionStrings:<name>`, then element paths with `@attribute` (`system.web/compilation@debug`); an element with a `key`, `name` or `id` attribute is named by it (`rules/logger[*]@minlevel`), repeated elements by position (`server[2]`) |
| Other XML | Element paths and `@attribute`, as above |
| JSON | Dotted paths (`Logging.LogLevel.Default`), array items by position (`Hosts[2]`); comments are tolerated |
| YAML | Dotted paths, sequence items by position; a second document is prefixed `doc2.` |
| TOML | `table.key`, arrays of tables by position (`plugin[2].name`) |
| PowerShell, batch, CMD, VBScript | The assigned variables as written: `$Name`, `$env:NAME`, `NAME` from `set NAME=`, `Const Name` and `Name` in VBScript |
| Registry exports (`.reg`) | `[key path] value name` (`(Default)` for `@`); strings unescaped, `dword` with its decimal value, expandable and multi-strings decoded, deletions as `(deleted)` |
| INI, `.env`, `.properties` | `[section] key`, or the bare key before any section |
| HCL, nginx and other conf, Dockerfile, text | The directive or key, prefixed by the blocks it sits in (`http.server.listen`); repeated names are numbered (`RUN #2`) |
| SQLite, property lists, Markdown with front matter and other binary-hybrid files | One `(file contents)` entry compared by hash |

A file that does not parse as its format is read line by line instead. Values that look like secrets (by setting name or by
the secret scanner's rules) are compared but never shown. A file with more settings than a table can hold is also compared
as a whole, so a difference past the cut still shows.

## Catalog reference links

Detection metadata carries a `catalog_references` array sourced from the
catalog. Each entry points to the reviewer guidance for that
format so downstream tooling can deep link into the appropriate reference
material. Current mappings:

| Catalog format        | Reference |
|-----------------------|-----------|
| `registry-export`     | `docs/detection-types.md#registryexport` |
| `registry-live`       | `docs/detection-types.md#registrylive` |
| `structured-config-xml` | `docs/detection-types.md#structuredconfigxml` |
| `xml`                 | `docs/detection-types.md#xml` |
| `json`                | `docs/detection-types.md#json` |
| `yaml`                | `docs/detection-types.md#yaml` |
| `toml`                | `docs/detection-types.md#toml` |
| `ini`                 | `docs/detection-types.md#ini` |
| `properties`          | `docs/detection-types.md#keyvalueproperties` |
| `unix-conf`           | `docs/detection-types.md#unixconf` |
| `script-config`       | `docs/detection-types.md#scriptconfig` |
| `embedded-sql-db`     | `docs/detection-types.md#embeddedsqldb` |
| `binary-dat`          | `docs/detection-types.md#genericbinarydat` |

## Version Tracking Guidance

- When you adjust detection heuristics or metadata for a plugin, bump its
  `Version` property in the plugin class (for example, `JsonPlugin.Version`).
- Check `FormatRegistry.RegistrySummary()` to confirm ordering, priorities,
  and versions after registering new plugins.
- Update this matrix whenever a plugin version changes or a new format module
  lands so downstream users can see maturity at a glance.

## Roadmap References

- `docs/detection-types.md` lists catalog priorities and the usage data backing
  each class.
