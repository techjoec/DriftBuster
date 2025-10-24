# Format Support Matrix

This document tracks the configuration formats that DriftBuster understands
today, their current maturity, and the module versions declared by each format
plugin. Versions are surfaced directly from the underlying plugin classes via
`driftbuster.formats.plugin_versions()` so the registry and documentation stay
aligned.

See `docs/coverage-golden-standard.md` for the canonical list of detector
variants and metadata expectations each format must satisfy.

| Format family            | Variants / focus                                                 | Plugin | Module version | Status       | Notes |
|--------------------------|------------------------------------------------------------------|--------|----------------|--------------|-------|
| Structured configuration | `.config` web/app/machine files, build transforms, assembly sidecars | xml    | 0.0.6          | Stabilising  | Transform scope, precedence, schema provenance, attribute hints, MSBuild metadata, and line-level namespace provenance hashes now populate automatically. |
| Generic XML              | Application manifests (`.manifest`), resources (`.resx`), XAML UI | xml    | 0.0.6          | Stabilising  | Namespace logging captures per-declaration hashes + line numbers, schema provenance, `.resx` resource keys, MSBuild project detection, and attribute hints surface alongside hunt-aligned tokens. |
| JSON                     | Generic JSON, comment-friendly `jsonc`, `appsettings*.json`      | json   | 0.0.1          | Preview      | Large-sample validation and sampling guardrails are still being tuned. |
| Registry live scan       | JSON/YAML scan manifests (`registry_scan` token/keywords/patterns) | registry-live | 0.0.1      | Preview      | Integrates live Windows Registry hunts via definition files; avoids `.reg` exports. |
| INI                       | Classic/sectionless, dotenv gating, directive spillover metadata | ini    | 0.0.1          | Preview     | Records encoding, comment style, sensitive key hints, and classifies dotenv/unix-conf/hybrid variants for remediation planning. |
| YAML                     | Generic YAML, Kubernetes manifest hints                           | yaml   | 0.0.1          | Preview      | Heuristic detector (no parser dep) with document/list/indentation signals; detects `apiVersion`/`kind`. |
| Conf DSL                 | Logstash pipeline configs (`input`/`filter`/`output` blocks)      | conf   | 0.0.1          | Preview      | Tight heuristics avoid stealing `.conf` INI-like files covered by the INI plugin. |
| Text config              | Directive-style configs (OpenSSH, OpenVPN)                        | text   | 0.0.1          | Preview      | Fallback detector for whitespace-delimited directives; filename/content hints refine variants. |
| TOML                     | Generic TOML, arrays of tables                                    | toml   | 0.0.1          | Preview      | Detects `[table]`, `[[array-of-tables]]`, dotted keys, quoted/array values; no parser dependency. |
| HCL                      | HashiCorp configs (Nomad/Vault/Consul)                            | hcl    | 0.0.1          | Preview      | Detects `job {}`, `server {}`, `listener {}`, `seal {}` blocks + `key = value` pairs. |
| Dockerfile               | Multi-stage builds and directives                                 | dockerfile | 0.0.1       | Preview      | Filename/Dockerfile hint, `FROM` on first non-comment line, and common directives (RUN/COPY/ARG). |

Last updated: 2025-10-24.

## Version Tracking Guidance

- When you adjust detection heuristics or metadata for a plugin, bump its
  `version` attribute in the plugin class (for example,
  `JsonPlugin.version`).
- Run `driftbuster.formats.registry_summary()` to confirm ordering, priorities,
  and versions after registering new plugins.
- Update this matrix whenever a plugin version changes or a new format module
  lands so downstream users can see maturity at a glance.

## Roadmap References

- `docs/format-backlog-briefing.md` captures planned coverage and outstanding
  heuristics before additional formats leave HOLD.
- `docs/detection-types.md` lists catalog priorities and the usage data backing
  each class.
- `CLOUDTASKS.md` area A8 tracks the remaining XML backlog items (canonical transforms,
  schema provenance, namespace reporting).
