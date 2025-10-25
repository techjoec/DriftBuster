# CLOUDTASKS.md — Active Work Template

<!--
Schema reference
- H#.<area> [deps=] — Area title
  **REASON:** Why this area matters for near-term shipping.
  **MUST NOT:** Hard stops (what to avoid while working this area).
  **MUST:** Non-negotiable requirements.
  **ACCEPT GATES:** Conditions that must be true before marking the area complete.
  **REQUIRED RELATED WORK:** ≥4 concrete subpaths (files/modules/tests) agents must advance. Use hierarchical numbering (1.1, 1.1.1…) for nested subtasks.
- Append new areas at the top. Move finished areas to `CLOUDTASKS-COMPLETED.md` using the same schema.
- Task IDs (`T-xxxxxx`) stay in CLOUDTASKS.md; cross-reference them inside subtasks when relevant.
-->

## A0. Drag/Drop Await Regression Fix [deps=]

**REASON:** `ServerSelectionView.OnServerCardPointerPressed` no longer awaits the drag operation (`gui/DriftBuster.Gui/Views/ServerSelectionView.axaml.cs:63`), so any drag/drop exception is now swallowed and the UI can silently stall. We need to restore awaited execution while keeping the injectable drag/drop service for tests.

**MUST NOT:** Revert the new drag/drop harness or remove the injectable service; avoid suppressing exceptions with broad try/catch.

**MUST:** Reintroduce awaiting/propagation of the drag task, update tests to accommodate the awaitable API, and keep telemetry untouched.

**ACCEPT GATES:** Headless drag/drop tests pass (`dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj`), no swallowed exceptions remain, and manual smoke confirms drag errors still surface to the UI thread.

**REQUIRED RELATED WORK:**
- [x] 0.1 Restore awaited drag/drop invocation `gui/DriftBuster.Gui/Views/ServerSelectionView.axaml.cs:63`.
  - [x] 0.1.1 Adjust `ServerSelectionViewTests` harness to handle awaited drag operations `gui/DriftBuster.Gui.Tests/Ui/ServerSelectionViewTests.cs`.
  - [x] 0.1.2 Ensure `IDragDropService` contract returns a `Task` that propagates faults without suppression `gui/DriftBuster.Gui/Views/ServerSelectionView.axaml.cs`.
- [x] 0.2 Add regression coverage validating drag errors bubble to callers (mock service throwing) `gui/DriftBuster.Gui.Tests/Ui/ServerSelectionViewTests.cs`.

## A1. Multi-Server Card Validation Coverage [deps=]

**REASON:** Recent card UX refactors rely on `ServerSelectionViewModel` summaries and drag/drop ordering (`gui/DriftBuster.Gui/ViewModels/ServerSelectionViewModel.cs:178`, `gui/DriftBuster.Gui/Views/ServerSelectionView.axaml.cs:42`). Without targeted tests the new tooltips and host reorder lane can regress silently.

**MUST NOT:** Skip drag/drop guard rails, toggle global virtualization thresholds, or stub out toast telemetry—tests must exercise real view model logic.

**MUST:** Add deterministic unit and headless UI tests that cover validation summaries, busy-state gating, and drag/drop insertion semantics while keeping telemetry assertions lightweight.

**ACCEPT GATES:** `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total` passes with the new suites; `artifacts/logs/gui-validation/` captures refreshed transcripts; coverage report highlights increased lines for the touched classes.

**REQUIRED RELATED WORK:**
- [x] 1.1 Expand validation summary coverage for host cards `gui/DriftBuster.Gui/ViewModels/ServerSelectionViewModel.cs:178`.
  - [x] 1.1.1 Add unit tests covering root error precedence, pending counts, and disabled states in `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelValidationTests.cs`.
  - [x] 1.1.2 Assert tooltip/automation bindings surface updated summaries in `gui/DriftBuster.Gui.Tests/Ui/ServerSelectionViewTests.cs`.
- [x] 1.2 Cover `CanAcceptReorder` busy gating and identity checks `gui/DriftBuster.Gui/ViewModels/ServerSelectionViewModel.cs:1096`.
  - [x] 1.2.1 Extend `ServerSelectionViewModelAdditionalTests` to exercise busy-state false negatives and identical-host rejection `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs`.
- [x] 1.3 Exercise drag/drop handlers for server cards `gui/DriftBuster.Gui/Views/ServerSelectionView.axaml.cs:42`.
  - [x] 1.3.1 Build a headless drag/drop harness covering pointer press and drop callbacks in `gui/DriftBuster.Gui.Tests/Ui/ServerSelectionViewTests.cs`.
  - [x] 1.3.2 Verify lower-half drops place hosts after the target slot and maintain indexes `gui/DriftBuster.Gui.Tests/Ui/ServerSelectionViewTests.cs`.
- [x] 1.4 Assert toast warning telemetry fires for attention-required hosts `gui/DriftBuster.Gui/ViewModels/ServerSelectionViewModel.cs:1004`.
  - [x] 1.4.1 Craft mixed-availability scan responses and validate toast invocations plus activity entries in `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs`.

## A2. Run Profiles Schedule Card Tests [deps=A1]

**REASON:** The profile card revamp hinges on `RunProfilesViewModel.ScheduleEntry` validation (`gui/DriftBuster.Gui/ViewModels/RunProfilesViewModel.cs:769`) and metadata commands. Missing coverage risks silent regressions in schedule windows and metadata edits.

**MUST NOT:** Bypass real validation paths, hardcode filesystem fixtures, or weaken existing coverage thresholds.

**MUST:** Add focused view model tests that exercise `IsBlank`, window/timezone validation, and metadata command revalidation without introducing platform-specific dependencies.

**ACCEPT GATES:** New tests pass under the same coverage command as A1; saved transcripts in `artifacts/logs/gui-validation/` reference the added suites; coverage for `RunProfilesViewModel` schedule branches rises above the current baseline.

**REQUIRED RELATED WORK:**
- [x] 2.1 Verify `ScheduleEntry.IsBlank` treats untouched cards as optional `gui/DriftBuster.Gui/ViewModels/RunProfilesViewModel.cs:984`.
  - [x] 2.1.1 Add unit tests asserting blank schedules remain error-free after validation in `gui/DriftBuster.Gui.Tests/ViewModels/RunProfilesViewModelTests.cs`.
  - [x] 2.1.2 Confirm partially-filled cards surface required-field errors once any value is provided `gui/DriftBuster.Gui.Tests/ViewModels/RunProfilesViewModelTests.cs`.
- [x] 2.2 Cover schedule window/timezone combinations `gui/DriftBuster.Gui/ViewModels/RunProfilesViewModel.cs:791`.
  - [x] 2.2.1 Add tests for missing start/end pairs and timezone enforcement in `gui/DriftBuster.Gui.Tests/ViewModels/RunProfilesViewModelTests.cs`.
  - [x] 2.2.2 Validate trimmed window values land in `ScheduleDefinition.Window` via `ToDefinition()` `gui/DriftBuster.Gui.Tests/ViewModels/RunProfilesViewModelTests.cs`.
- [x] 2.3 Exercise metadata commands triggering revalidation `gui/DriftBuster.Gui/ViewModels/RunProfilesViewModel.cs:1050`.
  - [x] 2.3.1 Ensure `AddMetadataCommand` registers change handlers and re-validates schedules in `gui/DriftBuster.Gui.Tests/ViewModels/RunProfilesViewModelTests.cs`.
  - [x] 2.3.2 Confirm removing metadata updates error state and unhooks listeners `gui/DriftBuster.Gui.Tests/ViewModels/RunProfilesViewModelTests.cs`.
- [x] 2.4 Lock in tag parsing deduplication for schedule cards `gui/DriftBuster.Gui/ViewModels/RunProfilesViewModel.cs:1117`.
  - [x] 2.4.1 Add tests ensuring mixed separators collapse into distinct, trimmed tags during `ToDefinition()` `gui/DriftBuster.Gui.Tests/ViewModels/RunProfilesViewModelTests.cs`.

## A3. Test Artifact Pollution Cleanup [deps=A0]

**REASON:** The warning-toast telemetry test now writes `artifacts/logs/gui-validation/server-selection-attention-toast.json` on every run (`gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs:317`), leaving the repo dirty and violating the clean-test requirement.

**MUST NOT:** Disable the telemetry capture or skip the test; avoid committing generated artifacts.

**MUST:** Redirect evidence writes to an ignored temp location, ensure the log directory is not mutated, and keep assertions intact.

**ACCEPT GATES:** `git status` stays clean after `dotnet test`; telemetry evidence is captured in a temp path with assertions on in-memory JSON; CLOUDTASKS entry moved to completed once verified.

**REQUIRED RELATED WORK:**
- [x] 3.1 Refactor toast telemetry test to use a temp directory instead of `artifacts/logs/gui-validation` `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs:317`.
  - [x] 3.1.1 Ensure temp path creation respects OS-specific temp dirs and is cleaned up after assertions `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs`.
- [x] 3.2 Add guard assertion that repo state remains clean post-test by verifying no files under `artifacts/logs/gui-validation` changed `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs`.
- [x] 3.3 Document the new temp evidence approach in test comments to prevent regressions `gui/DriftBuster.Gui.Tests/ViewModels/ServerSelectionViewModelAdditionalTests.cs`.

# End of priority queue
<!-- PR prepared: 2025-02-14T18:45:00Z -->
<!-- make_pr anchor -->
