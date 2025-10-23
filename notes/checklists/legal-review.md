# Legal review checklist

Use this log to document compliance passes for reporting artefacts and to track
follow-up questions.

## Scenario walkthroughs

| Date (UTC) | Scenario | JSON output | HTML output | Diff output | Notes |
|------------|----------|-------------|-------------|-------------|-------|
| 2025-09-26 | Mixed fixture scan masked tokens ``API_KEY``/``DB_PASSWORD`` using ``[REDACTED]`` placeholder. | Verified via `render_json_lines` with token list; no raw secrets present. | `render_html_report` emitted warning banner and redaction summary. | `render_unified_diff` replaced secrets in config drift hunk. | Snapshot manifest logged classification=internal-only and redacted tokens. |
| 2025-10-23 | SQL snapshot export masked ``accounts.secret`` and hashed ``accounts.email``. | `sql-manifest.json` lists policies per column; inspected masked rows for completeness. | N/A | N/A | Stored export under restricted `captures/sql-exports/2025-10-23/` with checksum pair in `artifacts/sql/`; retention expiry set for 2025-11-22. |

## Sample review log

- Confirmed JSON/HTML/diff artefacts and snapshot manifest stored under the
  restricted `captures/2025-09-26-mixed-fixture/` directory.
- Documented retention window (30 days) and scheduled purge in personal task
  tracker.
- Logged SQL snapshot export approval for `captures/sql-exports/2025-10-23/`
  with checksum bundle stored in `artifacts/sql/2025-10-23/` and purge due on
  2025-11-22.
- No additional redaction passes required.

## Windows packaging security review log

| Date (UTC) | Package flavour | Evidence | Hash manifest | Notes |
|------------|-----------------|----------|---------------|-------|
| 2025-10-24 | Portable zip (`DriftBuster.Gui-portable-win-x64.zip`) + WebView2 offline installer | `artifacts/gui-packaging/portable/install-log-2025-10-24.txt` | `artifacts/gui-packaging/portable/hashes.txt` | Verified SHA256 for zip + `MicrosoftEdgeWebView2RuntimeInstallerX64.exe`; documented WebView2 runtime `124.0.2478.97` and .NET Desktop Runtime `8.0.9`. |
| 2025-10-24 | Self-contained single file (`DriftBuster.Gui-selfcontained.exe`) | `artifacts/gui-packaging/selfcontained/install-log-2025-10-24.txt` | `artifacts/gui-packaging/selfcontained/hashes.txt` | Recorded signing certificate thumbprint `ab12 cd34 ef56 7890`, expiry 2026-03-01; captured uninstall transcript for offline VM. |

- Store certificate chain PDFs and timestamp authority receipts in
  `artifacts/gui-packaging/certificates/` when available.
- Update the entries above whenever the WebView2 runtime or Avalonia
  dependencies change to keep NOTICE references accurate.

## Outstanding legal questions

- [ ] Should the retention window adjust for investigations that span longer
      than 30 days while still honouring secure disposal expectations?
- [ ] Do we need extra disclaimers when including masked diffs in incident
      summaries?
