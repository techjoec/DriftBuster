# Configuration Files with DriftBuster

DriftBuster focuses on configuration-centric formats. Use this guide to map the
most common file types to the detectors that understand them today.

## Core Families

- **`.config` (web/app/machine)** — handled by the XML plugin. Transform files
  such as `web.Release.config` and assembly sidecars (`*.exe.config`) are
  surfaced with dedicated variants. Namespaces, XML declarations, and
  `schema_locations` provenance metadata are captured automatically.
- **Manifests (`*.manifest`)** — recognised as `app-manifest-xml` with
  confidence bumps when the assembly namespace is present.
- **Resources (`*.resx`)** — classified as `resource-xml` and include captured
  resource key previews (`resource_keys`) in the detection payload.
- **XAML (`*.xaml`)** — flagged as `interface-xml` whenever UI namespaces are
  discovered.
- **JSON (`*.json`, `*.jsonc`)** — the JSON plugin highlights structured
  settings (`appsettings*.json`) and comment-friendly payloads (`jsonc`).

Check `docs/format-support.md` for module versions and maturity notes.

## Tips for Accurate Results

- Prefer **exact filenames** (`web.config`, `appsettings.json`) when possible.
  They carry strong hints for the built-in detectors.
- Keep files **text encoded (UTF-8/16)**. The registry auto-detects common BOMs
  and reports the codec in the metadata payload.
- When scanning large repositories, pass `--glob` filters to the CLI to avoid
  non-config noise (for example, `--glob "**/*.config"`).
- Use the metadata returned with each detection (`metadata` field) to confirm
  root element names, namespace declarations, and transform scope before acting
  on the result.

## Next Steps

- Need different sampling limits or plugin ordering? See
  `docs/customization.md`.
- Want to align detections with expected baselines? Jump to
  `docs/profile-usage.md`.
