Windows Registry Live Scans
===========================

Overview
--------
- Enumerates installed applications from the Uninstall registry keys.
- Guesses likely registry roots for a selected app (HKCU/HKLM software trees and Wow6432Node).
- Searches registry values under those roots using user‑provided keywords and/or regex patterns.

Usage (Python API)
------------------
- List apps: `from driftbuster.registry import enumerate_installed_apps`
- Pick targets: `find_app_registry_roots("My App", installed=apps)`
- Search: `search_registry(roots, SearchSpec(keywords=("server",), patterns=(re.compile("https://"),)))`

Notes
-----
- Read‑only; writes are not supported.
- Windows only. On non‑Windows platforms, construct a custom backend or skip.
- Traversal enforces limits: max depth, max hits, and a time budget.

CLI Helper
----------
- A lightweight helper exposes common operations from the shell (Windows only):
  - List apps: `python -m driftbuster.registry_cli list-apps`
  - Suggest roots: `python -m driftbuster.registry_cli suggest-roots "Vendor App"`
  - Search: `python -m driftbuster.registry_cli search "Vendor App" --keyword server --pattern "api\\.internal\\.local"`
  - Emit config with pinned hive roots: `python -m driftbuster.registry_cli emit-config "VendorA" --root "HKLM\\Software\\VendorA,view=64"`

SQL Snapshot Exports
--------------------
- Capture masked SQLite exports directly from the CLI:
  - `python -m driftbuster.cli export-sql samples/sqlite/sample.sqlite --mask-column accounts.secret --hash-column accounts.email`
- Run the same workflow from PowerShell:
  - `Export-DriftBusterSqlSnapshot -Database samples/sqlite/sample.sqlite -MaskColumn accounts.secret -HashColumn accounts.email`
- Exports land in `sql-exports/` by default with a `sql-manifest.json` rollup containing table lists, row counts, and column policies.
- Record which columns were masked or hashed inside `notes/status/gui-research.md` so auditors can retrace the anonymisation steps.
- Store the masked database, manifest, and checksum bundle under a restricted directory until the retention deadline recorded in `notes/checklists/legal-review.md`.
- Follow the retention guidance in `docs/legal-safeguards.md#retention` and document purge completion when artefacts are deleted.

Offline Runner
--------------
- The offline collection tool can execute registry scans alongside file copies.
- Add a `registry_scan` source to `profile.sources` in your JSON config:

```
{
  "profile": {
    "name": "collect-config-and-registry",
    "sources": [
      { "path": "C:/ProgramData/VendorA/AppA" },
      {
        "alias": "vendorA-registry",
        "registry_scan": {
          "token": "VendorA AppA",
          "keywords": ["server", "api"],
          "patterns": ["https://", "api\\.internal\\.local"],
          "roots": [
            {"hive": "HKLM", "path": "Software\\VendorA\\AppA", "view": "64"}
          ],
          "max_depth": 12,
          "max_hits": 200,
          "time_budget_s": 10.0
        }
      }
    ]
  }
}
```

- Results are written to `data/<alias>/registry_scan.json` and summarised in the manifest.
- Non‑Windows hosts skip registry sources, recording a clear reason in the manifest/logs.
- Requested roots appear in the manifest (`requested_roots`) alongside the resolved traversal list so reviewers can confirm the collector stayed within scope.

Explicit Hive Roots
-------------------
- Use the CLI `--root` option when generating snippets to capture the exact hive paths and views you intend to traverse (`HKLM\\Software\\VendorA` or `HKLM\\Software\\VendorA,view=32`).
- Parsed roots populate the `registry_scan.roots` array; when present, the offline runner skips heuristic discovery and walks only the supplied descriptors.
- Manifest entries mirror both `roots` and `requested_roots` so auditors can reconcile requested scope with the resolved traversal.
- Feed generated outputs into manual manifests with `scripts/capture.py run --registry-scan data/vendorA/registry_scan.json` to embed token/roots/hit metadata alongside filesystem capture notes.

Remote Targets
--------------
- Add remote credentials without storing passwords in JSON by populating the
  optional `remote` and `remote_batch` blocks:

  ```json
  {
    "profile": {
      "name": "collect-config-and-registry",
      "sources": [
        {
          "alias": "hq-registry",
          "registry_scan": {
            "token": "VendorA AppA",
            "keywords": ["server"],
            "remote": {
              "host": "hq-gateway.internal",
              "username": "DOMAIN\\\\collector",
              "password_env": "DRIFTBUSTER_REMOTE_PASS",
              "transport": "winrm",
              "port": 5986,
              "use_ssl": true
            },
            "remote_batch": [
              {"host": "branch-01.internal", "username": "DOMAIN\\\\collector"},
              "branch-02.internal"
            ]
          }
        }
      ]
    }
  }
  ```

- The first entry under `remote` is used for the primary connection; any
  additional entries feed secondary hosts. Each target accepts these keys:
  `host` (required), `username`, `password_env`, `credential_profile`,
  `transport`, `port`, `use_ssl`, and `alias`. Inline `password` fields are
  rejected to prevent accidental leaks. When only a batch is required, skip the
  `remote` block and populate `remote_batch` with mappings or host strings.
- Generate JSON snippets from the CLI instead of hand-editing:
  `python -m driftbuster.registry_cli emit-config "VendorA" --remote-target "hq-gateway.internal,username=DOMAIN\\\\collector,password-env=DRIFTBUSTER_REMOTE_PASS" --remote-target branch-02.internal`.
- PowerShell operators can mirror the same workflow with `Invoke-DriftBusterRemoteScan`:
  - `Invoke-DriftBusterRemoteScan -ComputerName branch-01 -RemotePath 'ProgramData\\VendorA' -RunProfilePath profiles\\vendor.json`
  - `Invoke-DriftBusterRemoteScan -UseWinRM -ComputerName hq-core -RemotePath 'C:\\ProgramData\\VendorA' -RunProfilePath profiles\\vendor.json -RemoteWorkingDirectory '$env:ProgramData\\DriftBusterRemote'`
  The cmdlet mounts admin shares when available, falls back to WinRM staging when `-UseWinRM` is specified, and copies the capture manifest back into `<output>/<host>/` for audit trails.

Profile Scheduler (Preview)
---------------------------
- Schedules let you trigger profile runs on a cadence without wiring a full job
  runner. Add a `schedules` block alongside your `profile` definition when you
  want DriftBuster to orchestrate recurring captures.

  ```json
  {
    "profile": { "name": "collect-config-and-registry", "sources": ["C:/App"] },
    "schedules": [
      {
        "name": "nightly-backup",
        "profile": "profiles/nightly.json",
        "every": "24h",
        "start_at": "2025-01-01T02:00:00Z",
        "window": { "start": "01:00", "end": "05:00", "timezone": "UTC" },
        "tags": ["env:prod"],
        "metadata": { "contact": "oncall@example.com" }
      }
    ]
  }
  ```
- The `every` field accepts shorthand strings understood by
  `driftbuster.run_profiles.parse_interval` (`15m`, `1h30m`, `PT45M`, etc.). Use
  `start_at` to anchor the first run and optionally constrain execution to a
  quiet window with `window.start`/`window.end` in local time.
- Feed parsed entries into `driftbuster.scheduler.ProfileScheduler` to poll for
  due work and hand the resulting `ScheduledRun` objects to your execution
  harness.

GUI Focus (\.NET 8)
-------------------
- The GUI consumes the same packaged output and surfaces registry scan results
  beside file-based findings. It remains the primary workflow; the Python
  engine powers the collection and detection.
