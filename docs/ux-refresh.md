# UX Refresh Notes

This file captures the working notes for the current Avalonia UX polish effort so that future
iterations have a concrete baseline.

## Responsive Server Layout (P1)
- Host cards moved from a fixed `WrapPanel` into an `ItemsRepeater` using `UniformGridLayout`.
- Min/max sizing keeps each card between 320 px and 440 px wide while the layout adapts at
  1280, 1440, and 1920 px breakpoints without gutter collapse.
- Validation summary surfaces through a shared tooltip/automation label that reports "All roots
  ready", "pending validation", or specific error text (duplicate/relative paths, missing roots).
- Focus-visible styles for `Button`, `ComboBox`, `TextBox`, and `ToggleSwitch` were added to
  the theme dictionary to give keyboard navigation a high-contrast outline.
- Access keys on the execution command strip: **Alt+R** (Run all), **Alt+M** (Run missing),
  **Alt+C** (Cancel). Confirmed Narrator announces them with the new focus outlines.

## Catalog & Drilldown Productivity (P2)
- Catalog `DataGrid` now provides click-to-sort headers across Config, Drift, Coverage, Format,
  Severity, and Updated columns. The current descriptor is cached inside the session snapshot so
  it restores with the rest of the multi-server state.
- Drilldown headers gained a metadata strip: format, baseline host label/ID, drift count, and
  provenance. This keeps analysts from scrolling before deciding whether to escalate a config.
- Added a **Copy JSON** action alongside the export pair. It uses the sanitised payload already
  handed to the export path, removes the need to save a temp file, and fires a toast/status entry
  so there is evidence in the activity timeline.

## Diff Planner MRU (P2)
- Diff planner header exposes a **Recent plans** dropdown that replays sanitized MRU entries with
  trimmed file metadata, digests, and comparison counts. Entries persist in
  `%LOCALAPPDATA%/DriftBuster/cache/diff-planner/mru.json` (or the XDG data root equivalent) and cap
  at ten records to avoid stale payload buildup.
- Sanitized payloads only: raw backend responses are rejected and a telemetry record (`payload_kind`
  `raw`) is logged before the GUI drops the entry. Refer to the structured events in
  `artifacts/logs/diff-planner-mru-telemetry.json` when auditing the guardrail.
- Screenshot capture checklist lives alongside the GUI guide:
  1. Load sanitized fixtures from `artifacts/samples/diff-planner/` and toggle **Sanitized JSON**.
  2. Verify the footer banner shows **Sanitized summary** and that digests (not raw text) appear in
     both panes.
  3. Save captures into `docs/assets/diff-planner/` using `YYYYMMDD-mru-<theme>-<resolution>.png`.
  4. Record capture metadata (theme, resolution, fixture) in the asset manifest table below once
     populated.
- Follow-up: populate `docs/assets/diff-planner/manifest.md` once the first sanitized capture set
  lands; include theme/resolution columns so refreshes remain auditable.

## Avalonia 11.2 Migration Follow-up (P5)
- Results catalog view and toast converters now target Avalonia 11.2 APIs. See `docs/windows-gui-guide.md#avalonia-112-migration-notes` for the build/test checklist captured during the release-blocker sweep.
- When preparing release communications, link to the dedicated entry under `docs/release-notes.md#avalonia-112-results-catalog-alignment` so downstream teams inherit the migration summary without restating it here.
- Store Release build artefacts and SHA-256 hashes in `artifacts/builds/avalonia-11-2/` for any future diffing runs; reference them when verifying toast stacks or catalog sort persistence regressions.

## Validation & Testing Checkpoints
- Headless tests cover responsive host layout, catalog sort persistence, and the new clipboard
  command. Ensure `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj` stays green
  after any UX iteration.
- Future work: capture 1280/1440/1920 px snapshots under `artifacts/ux/` once the automated
  screenshot harness lands. Track this follow-up in CLOUDTASKS.md (area A4) when the tooling is ready.

Last updated: 2025-03-09 — regenerate after any notable UX change.

## Asset Provenance
- Store UI screenshots under `docs/assets/` with filenames that encode resolution, theme, and feature (e.g. `20250214-server-grid-1440px-dark.png`).
- Record each capture in the benchmark log or design notes with source resolution, theme, and data set used.
- Link assets back to the benchmark entry or doc section that motivated the capture so refresh cycles stay traceable.

### Theme capture manifest

| Capture | Theme | Resolution | Fixture | Notes |
|---------|-------|------------|---------|-------|
| `docs/assets/themes/20250309-theme-darkplus-1600px.png` | Dark+ | 1600×900 | Multi-server validation baseline | Highlights contrast-checked command strip, responsive host grid, and toast revisions. |
| `docs/assets/themes/20250309-theme-lightplus-1600px.png` | Light+ | 1600×900 | Multi-server validation baseline | Shows softened neutral layers, accent outlines, and activity timeline readability tweaks. |

## Notifications & Timeline (P3)
- Toast host now draws from tokenised colours/icons defined in `Notifications.axaml`, caps the visible stack at three cards, and exposes an overflow expander with counts.
- Timeline filter adds **Warnings** and **Exports** options; selection is persisted alongside session state (`ServerSelectionCache.activity_filter`).
- Export and copy actions log with `ActivityCategory.Export`, enabling the dedicated filter without string matching.

