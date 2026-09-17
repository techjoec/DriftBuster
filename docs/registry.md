Windows Registry Live Scans
===========================

Overview
--------
- Enumerates installed applications from the Uninstall registry keys.
- Guesses likely registry roots for a selected app (HKCU/HKLM software trees and Wow6432Node).
- Searches registry values under those roots using user‑provided keywords and/or regex patterns. Patterns use .NET regular
  expression syntax (culture-invariant, case-sensitive unless the pattern sets `(?i)`); a pattern that does not parse fails with
  `PatternError` and the .NET parser message.

Notes
-----
- Read‑only; writes are not supported.
- Windows only. On other platforms `driftbuster registry-scan` exits 1 with `Registry scanning requires Windows.`
- Traversal enforces limits: max depth, max hits, and a time budget.
- The implementation lives in `gui/DriftBuster.Backend/Registry/` (`RegistryScan`, `SearchSpec`, `IRegistryBackend`).

Console Tool
------------
- List apps: `driftbuster registry-scan list-apps`
- Suggest roots: `driftbuster registry-scan suggest-roots "Vendor App"`
- Search: `driftbuster registry-scan search "Vendor App" --keyword server --pattern "api\.internal\.local"`
  - `--max-depth` (default 12), `--max-hits` (default 200), `--time-budget` seconds (default 10)
  - `--root "HKLM\Software\Vendor,view=64"` searches explicit hives instead of the suggested roots
- Emit config with pinned hive roots: `driftbuster registry-scan emit-config "VendorA" --root "HKLM\Software\VendorA,view=64"`

SQL Snapshot Exports
--------------------
- Capture masked SQLite exports from the console tool:
  - `driftbuster sql-export fixtures/sql/sample.sqlite --mask-column accounts.secret --hash-column accounts.email`
- Run the same workflow from PowerShell:
  - `Export-DriftBusterSqlSnapshot -Database fixtures/sql/sample.sqlite -MaskColumn accounts.secret -HashColumn accounts.email`
- Exports land in `sql-exports/` by default with a `sql-manifest.json` rollup containing table lists, row counts, and column policies.
- Record which columns were masked or hashed so auditors can retrace the anonymisation steps.
- Store the masked database, manifest, and checksum bundle under a restricted directory until the retention deadline recorded in `notes/checklists/legal-review.md`.
- Follow the retention guidance in `docs/legal-safeguards.md#retention` and document purge completion when artefacts are deleted.

Offline Runner
--------------
- `scripts/driftbuster-offline-runner.ps1` can execute registry scans alongside file copies.
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
- Use `--root` when generating snippets to capture the exact hive paths and views you intend to traverse (`HKLM\Software\VendorA` or `HKLM\Software\VendorA,view=32`).
- Parsed roots populate the `registry_scan.roots` array; when present, the offline runner skips heuristic discovery and walks only the supplied descriptors.
- Manifest entries mirror both `roots` and `requested_roots` so auditors can reconcile requested scope with the resolved traversal.
- Embed collected outputs in a capture manifest with `driftbuster capture run --registry-scan data/vendorA/registry_scan.json ...` so token, roots and hit metadata sit alongside the filesystem capture.

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

- The `remote` entry is used for the primary connection; `remote_batch` entries
  feed secondary hosts. Each target accepts these keys: `host` (required),
  `username`, `password_env`, `credential_profile`, `transport`, `port`,
  `use_ssl`, and `alias`. Inline `password` fields are rejected to prevent
  accidental leaks. When only a batch is required, skip the `remote` block and
  populate `remote_batch` with mappings or host strings.
- Generate JSON snippets instead of hand-editing:
  `driftbuster registry-scan emit-config "VendorA" --remote-target "hq-gateway.internal,username=DOMAIN\collector,password-env=DRIFTBUSTER_REMOTE_PASS" --remote-target branch-02.internal`.
  Supported keys: `username`, `password-env`, `credential-profile`, `transport`, `port`, `use-ssl`, `alias`.
- PowerShell operators capture remote hosts with `Invoke-DriftBusterRemoteScan`:
  - `Invoke-DriftBusterRemoteScan -ComputerName branch-01 -RemotePath 'ProgramData\VendorA' -RunProfilePath profiles\vendor.json -Environment prod -Reason audit -MaskToken <token>`
  - `Invoke-DriftBusterRemoteScan -UseWinRM -ComputerName hq-core -RemotePath 'C:\ProgramData\VendorA' -RunProfilePath profiles\vendor.json -Environment prod -Reason audit -AllowUnmasked`
  Administrative share mode runs the capture in the local process against the UNC path. WinRM mode stages the module and backend on the remote host (which needs PowerShell 7.6, registered as the `PowerShell.7` session configuration by `Enable-PSRemoting`), runs the capture there, and copies the snapshot and manifest back into `<output>/<host>/`.

Profile Scheduler
-----------------
- Schedules trigger profile runs on a cadence. They live in
  `Profiles/schedules.json`; see `docs/configuration-profiles.md#schedules` for
  the fields.
- `every` accepts compact intervals (`15m`, `1h30m`), ISO 8601 time durations
  (`PT45M`) or seconds. Use `start_at` to anchor the first run and optionally
  constrain execution to a quiet window with `window.start`/`window.end` in
  local time.
- `driftbuster schedule due` lists the runs that are due and marks them pending;
  `mark-complete` advances the schedule after the run.

GUI
---
- The GUI surfaces `registry_scan.json` results collected by the offline runner
  beside file-based findings.
