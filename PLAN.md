# UX & Workflow Refinement Plan

This plan captures the next iteration objectives for polishing the Avalonia GUI, harmonising workflows, and ensuring the supporting documentation/tests stay aligned. The changes build on the recent host card, navigation, and coverage refresh.

## Phase 1 – Layout & Accessibility Polish

- **Host Cards & Execution Banner**
  - Reduce wrap-panel width breakpoints so three cards fit on 1440 px without truncation.
  - Add keyboard focus visuals and explicit access keys (`_Run all`, `_Cancel`) to primary buttons.
  - Surface root validation errors via `ToolTip` on the status badge in addition to the inline text to improve screen-reader support.
- **Catalog & Drilldown Enhancements**
  - Introduce column sorting in the catalog grid (severity, last updated) while preserving filter state.
  - Provide a compact metadata summary strip above the diff preview (format, baseline host, drift count) so the user never has to scroll for essentials.
  - Add a “Copy JSON” button beside Export to mirror the HTML export parity.
- **Toast & Activity Timeline**
  - Apply consistent spacing, larger icons, and severity-specific accent lines; collapse older toasts into an overflow when more than three are active.
  - Allow timeline filtering (All / Warnings / Exports) via the existing combo box.

## Phase 2 – Workflow Streamlining

- **Multi-server Orchestration**
  - Support drag/drop root reordering inside host cards (requires `ObservableCollection` handling and UI cues).
  - Persist the last selected filter (coverage/severity) alongside the active session so users returning to the catalog resume their context.
  - Provide inline “View drilldown” buttons on execution summary rows once runs complete.
- **Diff Planner Productivity**
  - Add recent paths dropdown next to each browse box, sourcing from most recent successful runs.
  - Introduce a side-by-side JSON summary view with colour-coded highlights leveraging the new headless tests.

## Phase 3 – Performance & Visual Refinements

- **Virtualisation & Async Tweaks**
  - Enable virtualisation on large `ItemsControl` collections (activity timeline, drilldown servers) to keep scrolling smooth with >100 entries.
  - Marshal scan progress callbacks through the dispatcher with a buffered queue to avoid UI hitches during rapid updates.
- **Theme & Responsiveness**
  - Expand the theme palette with high-contrast variants (Dark+, Light+) and surface the selection in settings.
  - Fine-tune margins/paddings for 1080 p and 4K breakpoints (
    e.g., increase header padding at widths >1600, reduce card width at <1280).

## Supporting Tasks

1. **Testing & Coverage**
   - Extend headless tests to cover new filters, root drag/drop operations, and toast overflow behaviour.
   - Add Python coverage scenarios for `_build_catalog_and_drilldown` alternate branches (e.g., hosts with offline availability, mixed coverage statuses).
   - Maintain `scripts/verify_coverage.sh` as the umbrella gate (run via tmux sessions as outlined in docs).
2. **Documentation Updates**
   - Update `docs/windows-gui-guide.md` screenshots and narrative when each phase lands.
   - Add a quickstart section covering keyboard shortcuts and accessibility improvements.
   - Summarise any workflow tweaks in `CHANGELOG.md` under “Changed”.
3. **Design Reference Board**
   - Capture before/after snapshots in `docs/ux-refresh.md` (new file) so future contributors understand the rationale and the visual baseline.
   - Annotate the plan in `notes/status/gui-research.md#ux-refresh` with links to relevant issues/PRs.

## Execution Notes

- Each phase should close with a tmux-driven test run:
  - `tmux new -s codexcli-<pid>-tests 'dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --collect:"XPlat Code Coverage" --results-directory artifacts/coverage-dotnet'`
  - `tmux new -s codexcli-<pid>-verify './scripts/verify_coverage.sh'`
- Iterate on UI patches using `Avalonia.Headless.XUnit` so dispatcher-bound interactions stay deterministic.
- Keep PLAN.md updated as objectives are delivered (mark items complete, add next steps) to maintain a living roadmap.

