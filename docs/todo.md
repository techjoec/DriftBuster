# Upcoming Work Stubs

## Encryption support for config files
See docs/encryption.md for the crypto plan; hook the offline config loader into the DPAPI/AES flow before packaging.

## Realtime secret scanning
TODO: mirror the offline scrubber by loading `src/driftbuster/secret_rules.json`, masking suspect lines before persistence, logging masked context, and exposing "ignore" controls for acknowledged matches.

## Scheduled tasks & notification channels
TODO: design a lightweight scheduler that can issue drift alerts and produce recurring backups.
TODO: add SMTP, Slack, and Teams notification adapters behind a single alerting interface.

## Remote system scanning options
TODO: ship a PowerShell tool bundle that can target \\server\drive$ admin shares and WinRM remoting sessions.
TODO: extend the config schema to describe remote credentials and batching rules.

## Live registry scanning
TODO: map hive traversal helpers into the offline runner so registry snapshots land in the manifest.

## SQL and other database configuration stores
TODO: future work to snapshot SQL or similar config databases via portable export routines.
