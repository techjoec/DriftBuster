# DriftBuster

DriftBuster scans configuration trees, recognises known formats, and explains
what changed so you can respond before drift becomes production fallout.

## Quick Start

1. **Install**
   ```sh
   python -m pip install -e .
   ```
2. **Scan**
   ```sh
   python -m driftbuster.cli fixtures/config --glob "*.config"
   ```
   Add `--json` to capture machine-readable output.
3. **Review** — every match includes `format`, `variant`, confidence, and
   metadata such as namespaces, sampled bytes, and `catalog_version` (`0.0.1`).

## What You Get

- **Format detection** with bounded sampling (128 KiB default) so large trees
  stay responsive.
- **Clear explanations**: priority-ordered plugins record why a detection fired
  and attach metadata ready for diffing or reporting.
- **Profiles & hunt mode** to compare results against tagged expectations while
  flagging dynamic values like hostnames or thumbprints.
- **Reporting helpers** (`driftbuster.reporting`) that canonicalise content and
  generate JSON/text diffs with optional redaction.
- **Desktop preview** via the Avalonia UI in `gui/DriftBuster.Gui/` (`dotnet
  run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`).

## Learn More

- `docs/format-support.md` – current format coverage and module versions.
- `docs/config-files.md` – how DriftBuster treats common configuration files.
- `docs/customization.md` – tweak sampling, ordering, and plugin registration.
- `docs/profile-usage.md` – quick guide to configuration profiles.
- `docs/configuration-profiles.md` – full profile reference.
- `docs/hunt-mode.md` – find dynamic values before they break parity.

Additional roadmaps: `docs/format-backlog-briefing.md`,
`docs/reporting-roadmap.md`, `docs/testing-strategy.md`.

## Project Layout

```
src/driftbuster/
├─ catalog.py        # Detection catalog and usage metadata
├─ core/             # Detector orchestration and shared types
└─ formats/          # Plugin registry and built-in detectors
```

## Contributing

- Keep heuristics deterministic and explainable.
- Prefer minimal, reviewable changes; update docs alongside code.
- See `CONTRIBUTING.md`, `docs/legal-safeguards.md`, and
  `docs/reviewer-checklist.md` for manual verification steps.

## License

DriftBuster ships under `LICENSE`. Notices and CLA templates live in `NOTICE`,
`CLA/INDIVIDUAL.md`, and `CLA/ENTITY.md`.
