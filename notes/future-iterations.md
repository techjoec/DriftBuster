# Future Iterations

Ideas below sketch the next wave of multi-server improvements. Each entry calls out the technical groundwork needed before implementation.

## Scheduled scans
- Automate multi-server plans on a cadence by replaying saved JSON plans and writing fresh exports.
- Prerequisites: lightweight scheduler host (Windows Task Scheduler, cron, or a bundled timer loop), encrypted credential cache when roots require elevation, and a collision guard so overlapping runs queue instead of racing.

## Alerting hooks
- Emit drift summaries directly from the orchestrator once a batch completes.
- Dependencies: outbound webhook client with retry/backoff, templated payload builder for catalog + drilldown highlights, and configurable secret handling for credentials.

## Bulk export improvements
- Package catalog, drilldown, and diffs into a single archive with manifest metadata for quick sharing.
- Requirements: streaming ZIP writer to avoid buffering large outputs, checksum generation for each artifact, and versioned manifest schema that records baseline information.

## Pluggable notification transports
- Allow users to plug in chat, email, or custom webhook notifications without editing the core engine.
- Dependencies: transport registry with a simple adapter interface, background dispatch queue that survives GUI restarts, and sandboxed configuration files for connection secrets.
