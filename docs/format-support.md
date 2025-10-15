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
| Structured configuration | `.config` web/app/machine files, build transforms, assembly sidecars | xml    | 0.0.4          | Stabilising  | Transform scope, precedence, schema provenance, attribute hints, and MSBuild metadata now populate automatically. |
| Generic XML              | Application manifests (`.manifest`), resources (`.resx`), XAML UI | xml    | 0.0.4          | Stabilising  | Namespace logging, schema provenance, `.resx` resource keys, MSBuild project detection, and attribute hints surface alongside hunt-aligned tokens. |
| JSON                     | Generic JSON, comment-friendly `jsonc`, `appsettings*.json`      | json   | 0.0.2          | Preview      | Large-sample validation and sampling guardrails are still being tuned. |
| INI                       | Classic/sectionless, dotenv gating, directive spillover metadata | ini    | 0.0.2          | Preview     | Records encoding, comment style, sensitive key hints, and classifies dotenv/unix-conf/hybrid variants for remediation planning. |

Last updated: 2025-10-17.

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
- `ROADMAP.md` calls out the remaining XML backlog items (canonical transforms,
  schema provenance, namespace reporting).
