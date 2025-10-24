# DriftBuster Release Notes Rails

To keep updates easy to scan (even when Velopack ships the whole bundle), every
release must include a Markdown file that follows the structure below. The
packaging script enforces these guards automatically.

## Required Layout

```
# DriftBuster <version>

## Core
- Bullet points, each describing user-visible changes. Use `None` when there are
  no changes.

## Formats
- Group entries by format name. Only list what actually changed; otherwise `None`.

## GUI
- Call out UX tweaks, bug fixes, or new flows.

## Installer
- Capture Velopack configuration, bootstrapper, or delivery tweaks.

## Tooling
- Document packaging/build script changes, release engineering tweaks, or
  ecosystem updates.

## Notes
- Optional free-form context (upgrade steps, known issues, roll-back guidance).
```

### Additional Guidance
- Use present tense, one sentence per bullet.
- Prefix format items with the module name (e.g. `- JSON:`) when relevant.
- When a section lists real changes (instead of `None`), append a matching entry
  to the component’s changelog under `notes/changelog/` (e.g. `core.md`,
  `gui.md`, `installer.md`, `tooling.md`, or `formats/<name>.md`).
- Lifetime data (dates, operator) should live in `Notes` if required.
- The file name convention is `notes/releases/<version>.md`.
- When theme updates land, embed or link to the refreshed captures in
  `docs/assets/themes/` and reference the manifest table in
  `docs/ux-refresh.md#theme-capture-manifest` so reviewers can trace when the
  visuals were last regenerated.

## Component Changelog Files
- `notes/changelog/core.md`
- `notes/changelog/gui.md`
- `notes/changelog/installer.md`
- `notes/changelog/tooling.md`
- `notes/changelog/formats/<format>.md`

Each file uses the heading pattern `## <version>` followed by bullets. Use one
file per formatter, keeping names lowercase/kebab-case (e.g. `json.md`).

## Top-Level Changelog

- A repository-wide `CHANGELOG.md` summarizes notable changes by version. Keep
  it concise and link to component changelogs for details.

## Version Matrix

Track package versions in `versions.json` so downstream packaging scripts stay in
sync.

| Component  | Source                                  | Version Source                  |
|------------|-----------------------------------------|---------------------------------|
| Core       | Python package & `DriftBuster.Backend`  | `Directory.Build.props` / `versions.json` (`core`) |
| GUI        | Avalonia desktop app                    | `gui/GuiVersion.props` / `versions.json` (`gui`)   |
| PowerShell | `cli/DriftBuster.PowerShell` module     | `DriftBuster.psd1` / `versions.json` (`powershell`) |

When cutting a release:

1. Update `versions.json` and run `python scripts/sync_versions.py`.
2. Rebuild the backend so the PowerShell module packages the matching DLL.
3. Mention the backend/core dependency version in GUI release notes and module
   documentation if it changes.

## Using the Script

```
dotnet tool restore
scripts/build_velopack_release.sh \
  --version 1.2.3 \
  --rid win-x64 \
  --release-notes notes/releases/1.2.3.md
```

The script fails fast when any required section is missing. Use `Notes` to point
users to doc updates or manual migration steps.

### Performance & Async Stability (A3) Summary
- Virtualised server catalog heuristics now default to a 400-item threshold and fall back to the existing non-virtualised layout when the override disables recycling on constrained hosts.
- Environment variables `DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD` and `DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION` give operators deterministic control when preparing release notes for low-memory or high-volume environments.
- Capture the active override plus sample virtualization decisions in `artifacts/perf/virtualization-baseline.json` and link the evidence bundle from GUI release entries.
