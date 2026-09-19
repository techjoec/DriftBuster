# Windows acceptance run — 2026-09-18

Acceptance run of the .NET-only product (0.2.0 line) on two Windows Server 2022 lab guests: an application host that ran the self-contained win-x64 GUI and CLI as a domain user, and a remote target reached over the admin share and WinRM. A deterministic estate (baseline, staging and prod variants of IIS, service and agent configuration) supplied known drift: reordered JSON keys, UTF-16 and LF-only files, a change past the 128 KiB sample, missing and extra files, a file whose ACL denies the user, planted secrets and a non-ASCII path.

## Covered

- GUI: first launch and theme, multi-server over three hosts (one over UNC) with catalog, drill-down, exports and session restore, Diff planner, Hunt explorer over UNC, Profiles (sources with alias, exclude and optional, run, schedule card, offline collector package), and negative paths (missing folder, unreachable share, empty folder).
- CLI: `scan`, `diff`, `hunt`, `sql-export`, `registry-scan`, `capture run`/`compare`, `report`, `schedule list`, and parse errors.
- PowerShell module on pwsh 7.6: diff, hunt, SQL export, and remote scan over the admin share and over WinRM.
- Offline runner on Windows PowerShell 5.1: files, registry scan, SQLite snapshot through the Windows SQLite library, secret redaction, and a DPAPI-wrapped AES package.
- Backend, CLI and GUI test suites on Windows.

## Findings fixed

Blockers and majors: the results catalog rendered empty (DataGrid theme missing); the drill-down view was never hosted; Run profile crashed the GUI (view models touched UI state off the UI thread); the offline runner packaged secrets unredacted when run as a single file; tab switches discarded results and edits; the GUI wrote exports, logs and profiles relative to the working directory; the collector package lacked the runner script; one unreadable file aborted `driftbuster scan`; UTF-16 files were read as UTF-8 by the CLI diff and multi-server; reordered JSON keys counted as drift; XML diffs were a single line; the Secrets tag meant any hunt hit; the diff cache kept stale canonical forms across a canonicaliser change; the drill-down diff pane showed the baseline against itself; JSON written by Windows PowerShell 5.1 (with a byte order mark) was refused.

Minors: combo boxes and list items exposed type names to UI Automation; identical diff labels for the same file on two hosts; placeholder host names; the filtered count shown as the catalog size; a Baseline filter that filtered nothing; truncated catalog columns and bare XML file names; escaped JSON exports; "Remember session" never saved without a separate click; `--bogus` reported as a missing path; a missing registry root reported as "no hits"; the saved profile not listed after a run.

Six tests assumed Linux (symlink kind, case-sensitive names, path separators, XDG) and now hold on Windows. The commits from "Diff: line-per-element XML, JSON diffed as JSON, distinct same-name labels" through the Windows test fixes carry the detail of each fix.

## Result

All findings above are fixed and re-checked on the guests. On Windows the Backend, CLI and GUI suites pass, with the Windows-only registry tests running; on Linux the merged coverage gate, lint and gitleaks pass. No stray DriftBuster or .NET processes were left on either guest.
