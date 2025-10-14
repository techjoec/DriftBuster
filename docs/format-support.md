# Format Support Matrix

This document tracks the configuration formats that DriftBuster understands
today, their current maturity, and the module versions declared by each format
plugin. Versions are surfaced directly from the underlying plugin classes via
`driftbuster.formats.plugin_versions()` so the registry and documentation stay
aligned.

| Format family            | Variants / focus                                                 | Plugin | Module version | Status       | Notes |
|--------------------------|------------------------------------------------------------------|--------|----------------|--------------|-------|
| Structured configuration | `.config` web/app/machine files, build transforms, assembly sidecars | xml    | 0.0.2          | Stabilising  | Schema discovery (xsi:*) and layered transform precedence now surface in metadata. |
| Generic XML              | Application manifests (`.manifest`), resources (`.resx`), XAML UI | xml    | 0.0.2          | Stabilising  | Canonicaliser preserves namespace order and exposes schema references for reporting highlights. |
| JSON                     | Generic JSON, comment-friendly `jsonc`, `appsettings*.json`      | json   | 0.0.1          | Preview      | Large-sample validation and sampling guardrails are still being tuned. |

Last updated: 2025-10-21.

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
