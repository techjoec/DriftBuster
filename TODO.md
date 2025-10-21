# TODO

## Multi-Server Scan Flow Enhancements
- [ ] Build server selection screen capturing six hosts with editable labels (`App Inc`, `Supporting App`, `FreakyFriday`) and search scope toggles (all drives, single drive, custom roots).
- [ ] Implement hunt root management: default to `C:\Program Files`, show status badges per root, allow adding/removing entries like `D:\Program Files` with validation.
- [ ] Add run orchestration that executes scans in parallel batches, surfaces per-server progress, and preserves successful results when re-running missing hosts only.
- [ ] Persist last-used roots and server-label mapping for the session (memory cache/config file) while avoiding background writes unless user opts in.

## Results Catalog UI
- [ ] Design consolidated config catalog grid mirroring mock: baseline dropdown (`Match/Drift`), presence counts, drift totals with color tags, last updated timestamp, detected format per config artifact.
- [ ] Provide filters for coverage (all servers vs partial), drift severity, format type, and search-as-you-type on config names.
- [ ] Highlight missing artifacts (e.g., `plugins.conf` 1/6) with investigation links and quick re-scan shortcut scoped to affected servers.

## Drilldown Experience
- [ ] When a config (e.g., `config.ini`) is selected, render detailed pane with server list, presence badges, baseline selector, drift metrics, redaction status, and side-by-side/unified diff toggle.
- [ ] Include metadata sidebar summarising last scan time, detector provenance, change counts, and actionable notes (masked tokens, validation issues).
- [ ] Enable exporting per-config reports (HTML/JSON) and launching targeted re-scan for specific servers directly from the drilldown.

## Backend/API Support
- [ ] Extend scan API to accept multiple root paths per server and return structured status (`found`, `not_found`, `in_progress`) with timestamps.
- [ ] Normalise config keys by logical identifier so files on different drives align across servers; fall back to relative path heuristics when identifiers are absent.
- [ ] Cache diff artefacts between runs to avoid recomputation when underlying files unchanged; invalidate intelligently when hunt roots change.

## UX Feedback & Resilience
- [ ] Surface toast/inline guidance when scans fail (permissions, offline server) with retry guidance and log links.
- [ ] Add session activity feed capturing root updates, scan triggers, and export events for quick troubleshooting.
- [ ] Write headless UI tests covering the new result grid filters and drilldown diff toggles; ensure coverage stays above 90% per project policy.

## Documentation & Follow-Up
- [ ] Update `docs/multi-server-demo.md` to reflect new workflow (initial C drive scan, subsequent D drive addition, diff catalog and drilldown).
- [ ] Add quickstart snippet to README outlining server selection, root management, and drilldown usage.
- [ ] Plan future iteration notes: scheduled re-scans, alerting hooks, and bulk export improvements.
