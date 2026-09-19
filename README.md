# DriftBuster

DriftBuster inspects configuration trees, recognises familiar formats, and
describes the differences so you can rein in infrastructure drift before it
becomes an outage.

DriftBuster is a .NET 10 product: a desktop GUI, a `driftbuster` console tool
and a PowerShell module, all built on the shared `DriftBuster.Backend` library,
plus a standalone offline collector script for Windows hosts.

## Highlights

- **Precise format detection** – pluggable analyzers use bounded sampling to
  process large trees without overwhelming the collector.
- **Explainable results** – each hit includes format, variant, and the reason
  the detector fired so you can audit decisions instead of trusting a black box.
- **Profiles and hunt mode** – codify expectations, ignore volatile values, and
  flag snapshots that slip outside your guardrails.
- **Diff reporting** – unified diffs, HTML and JSON lines reports with optional
  redaction.
- **Multi-server comparison** – scan several hosts at once and see, setting by
  setting, what each server has that the baseline does not: one plain line per
  server, then a table per file with a column per server.
- **Windows Registry live scans** – enumerate apps, suggest likely registry
  roots, and search values by keyword/regex, locally or on remote hosts over
  WinRM from the offline runner (see `docs/registry.md`). Registry Editor
  exports (`.reg`) are detected and compared value by value.
- **Self-contained releases** – published builds carry the .NET runtime, so
  nothing has to be installed on the target machine.

## Requirements

| Use | Needs |
| --- | --- |
| Released GUI or console tool | Nothing; the publish is self-contained |
| Building from source | .NET 10 SDK |
| PowerShell module | PowerShell 7.6 or newer |
| Offline runner | Windows PowerShell 5.1 (or PowerShell 7); nothing to install |
| PowerShell lint and Pester suites | `PSScriptAnalyzer`, `Pester` 5 |

## Quick Start

Clone and build:

```sh
git clone https://github.com/techjoec/DriftBuster.git
cd DriftBuster
dotnet build DriftBuster.sln
```

Examples below run the console tool from source with
`dotnet run --project cli/DriftBuster.Cli -- <command>`. A published build runs
the same commands as `driftbuster <command>`; `--help` on any command lists its
options.

### Scan a directory

```sh
dotnet run --project cli/DriftBuster.Cli -- scan fixtures/config --glob "*.config"
```

- Add `--json` for JSON lines instead of the table.
- Use `--sample-size <bytes>` to override the default 128 KiB sampling window.

### Diff configuration snapshots

```sh
dotnet run --project cli/DriftBuster.Cli -- diff fixtures/config/web.config fixtures/config/web.Release.config \
  --mask-token Primary --context-lines 2
```

- `--content-type auto|text|xml` picks the canonicalisation (default `auto`).
- `--output-dir <dir>` writes one `.patch` per comparison.

### Hunt for dynamic values

```sh
dotnet run --project cli/DriftBuster.Cli -- hunt fixtures/multi-server/server01
```

Hits print as a JSON array with `rule`, `path`, `relative_path`, `line_number`,
`excerpt` and a `metadata.plan_transform` placeholder. See `docs/hunt-mode.md`.

### Render a report

```sh
dotnet run --project cli/DriftBuster.Cli -- report fixtures/config --format html --output report.html
```

`--format jsonl` emits JSON lines; `--skip-hunt` leaves hunt hits out;
`--mask-token` redacts values.

### Export SQL snapshots

```sh
dotnet run --project cli/DriftBuster.Cli -- sql-export fixtures/sql/sample.sqlite \
  --mask-column accounts.secret \
  --hash-column accounts.email \
  --placeholder "[MASK]" \
  --hash-salt pepper
```

- Multiple database paths are supported; each export is listed in `sql-manifest.json` with table counts and masking metadata.
- Use `--output-dir` to control the destination directory (defaults to `sql-exports/`).

### Launch the desktop GUI

```sh
dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj
```

Tips:
- Use the header **Theme** dropdown to switch between Dark+ and Light+.
- Click “Check core” to verify backend health (status dot shows green/red).
- The Profiles view includes schedule cards. Add a schedule name, profile reference, and interval (e.g. `24h`, `PT1H30M`) to persist cadence metadata alongside `Profiles/schedules.json` under the data root. Optional window start/end/timezone fields narrow execution windows, while metadata rows capture contacts or ticket IDs.

See `docs/windows-gui-guide.md` for the full walkthrough.

### Schedule recurring runs

- Build or load a profile, then add schedule entries in the GUI to define cadence, window, tags, and metadata. Saving the profile writes both `profile.json` and the consolidated `Profiles/schedules.json` manifest under the data root.
- Use the editable **Profile** dropdown on each schedule card to pick an existing profile name quickly.
- Inspect or act on the same schedules from the shell with `driftbuster schedule list`, `due`, `mark-complete --name <schedule>`, or `skip-until --name <schedule> --resume-at <iso8601>`. The console tool shares the GUI’s schedules when run with `--base-dir <data root>`.

### Multi-server quickstart

- Start the GUI; it opens on Multi-server **Setup**. Tick the hosts to include, pick one to set its label, scope and roots, drag hosts to change the order, and turn on **Remember session** to keep them next time (the snapshot is stored under your DriftBuster data root, e.g. `%LOCALAPPDATA%/DriftBuster/sessions/multi-server.json`).
- Click **Run all** to queue every active host. Use **Run missing** for retries; toasts and the activity timeline record progress, warnings, and exports.
- The run lands on **Compare**: a sentence per server ("prod: 6 settings differ in 2 files, 1 file missing"), a list of files with how many settings differ in each, and the selected file's settings as a table with a column per server and the differing values highlighted. **Next** / **Previous** (F8 / Shift+F8) walk every difference across the files. Click a server to show only what differs there, search settings and values, or turn off **Only show differences**. Secrets are masked but still compared. **Save report** writes the comparison as HTML and CSV under `exports/` in the data root.
- Right-click any setting, value or file to shape future runs: put it in a **group**, cover it with a **rule** (names the application and file, and can ignore, mask or group whatever it matches), **mark** it for now, **copy** it as JSON, TSV, text or hex, view the file **as a tree** or as **raw data**, see its **history** (where else the setting is set, where else the value appears), **add it to the review report**, **ignore** or **mask** it for this run or always, or **report a bug** about it (a prefilled GitHub issue, after you check the payload). **Manage choices…** edits everything saved and imports or exports it. Choices live in `curation.json` and every scan's settings in `history.db`, both in the data root.
- **Files** lists every scanned configuration with filters; double-click a row for **File details**: the baseline and any other server's copy side by side (or as a unified diff), unchanged lines folded away, **Next change** / **Previous change** (F7 / Shift+F7), and HTML/JSON exports (`exports/<config>-<timestamp>.{html,json}` under the data root).
- The **Diff planner** tab compares files you pick the same way: after **Build plan** the inputs fold away and the results open on the settings table, with the line-by-line diff and the JSON in their own tabs.
- The **Hunt explorer** tab scans a folder for values that change between machines: rule chips with counts, a findings grid beside the selected finding, and a right-click menu (copy, filter to a rule or file, open the folder, report a false positive).

Run the same plan from the shell; the request is read from stdin and progress
plus the final result are written as JSON lines:

```sh
dotnet run --project cli/DriftBuster.Cli -- multi-server <<'JSON'
{
  "plans": [
    {
      "host_id": "server01",
      "label": "Baseline",
      "roots": ["fixtures/multi-server/server01"]
    },
    {
      "host_id": "server02",
      "label": "Drift sample",
      "roots": ["fixtures/multi-server/server02"]
    }
  ]
}
JSON
```

The console tool and GUI share an OS-specific data root (`%LOCALAPPDATA%/DriftBuster` on Windows, `$XDG_DATA_HOME/DriftBuster` or `~/.local/share/DriftBuster` on Linux, `~/Library/Application Support/DriftBuster` on macOS); set `DRIFTBUSTER_DATA_ROOT` to move the whole data root.

### Console commands

| Command | Purpose |
| --- | --- |
| `scan <path>` | Detect formats in a file or tree (`--glob`, `--sample-size`, `--json`). |
| `diff <baseline> <comparisons>...` | Unified diffs with canonicalisation and masking. |
| `hunt <path>` | Dynamic value hunt as JSON (`--glob`, `--exclude`, `--placeholder-template`). |
| `multi-server` | Multi-host scan request on stdin, JSON lines on stdout. |
| `profile create\|list\|show\|run` | Manage and execute run profiles under `<base-dir>/Profiles` (`--base-dir` defaults to the current directory). |
| `detection-profile summary\|diff\|hunt-bridge` | Summarise and diff detection profile stores; attach profiles to hunt hits. |
| `schedule list\|due\|mark-complete\|skip-until` | Inspect and advance run profile schedules. |
| `registry-scan list-apps\|suggest-roots\|search\|emit-config` | Windows Registry live scan helpers (Windows only). |
| `sql-export <database>...` | Anonymised SQLite snapshots with masking and hashing. |
| `report [<root>]` | HTML or JSON lines report of detections and hunt hits. |
| `capture run\|compare\|export-sql` | Redacted capture snapshots with manifests, and their comparison. |
| `version` | Propagate `versions.json` into the build files (see `docs/versioning.md`). |
| `release` | Tests, self-contained publishes and the Velopack installer. |
| `maint selfcheck-multi-server-paths\|purge-reporting-retention` | Maintenance checks and retention purges. |

### Windows PowerShell module

The module needs PowerShell 7.6. It loads `DriftBuster.Backend.dll` from beside
`DriftBuster.psm1` or, in a checkout, from `gui/DriftBuster.Backend/bin`:

```powershell
dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Debug -o gui/DriftBuster.Backend/bin/Debug/published
Import-Module ./cli/DriftBuster.PowerShell/DriftBuster.psd1
Test-DriftBusterPing
Invoke-DriftBusterDiff -Versions 'fixtures/config/web.config','fixtures/config/web.Release.config'
Export-DriftBusterSqlSnapshot -Database fixtures/sql/sample.sqlite -MaskColumn accounts.secret -HashColumn accounts.email
```

Exported cmdlets cover diff, hunt, run profiles, schedules, SQL export and
remote capture (`Invoke-DriftBusterRemoteScan`); see `Get-Command -Module DriftBuster`.

To publish a redistributable archive, publish the backend for the configuration
and run the packaging script:

```powershell
dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj -c Release -o gui/DriftBuster.Backend/bin/Release/published
pwsh ./scripts/package_powershell_module.ps1 -Configuration Release -SkipAnalyzer
Get-Content artifacts/powershell/releases/DriftBuster.PowerShell-<version>.zip.sha256
```

The script emits `DriftBuster.PowerShell-<version>.zip` and an accompanying `.sha256` file under `artifacts/powershell/releases/`; verify the checksum before distributing the module.

If importing the module reports `DriftBusterBackendMissing`, publish the backend
as above and re-import with `Import-Module ./cli/DriftBuster.PowerShell/DriftBuster.psd1 -Force`.

### Offline runner

`scripts/driftbuster-offline-runner.ps1` collects files, registry scans (local, or
remote hosts over WinRM) and SQLite snapshots with nothing installed. It runs
on Windows PowerShell 5.1 (and PowerShell 7), scrubs secret candidates, writes a
manifest and log, zips the result and can encrypt it (`docs/encryption.md`).

```powershell
.\driftbuster-offline-runner.ps1 -ConfigPath .\config.json -OutputDirectory C:\Collections
```

Sample configs live in `samples/offline_runner/`.

### Release build

Run from the repository root:

```sh
dotnet run --project cli/DriftBuster.Cli -- release --runtime win-x64 \
  --release-notes notes/releases/<semver>.md --installer-rid win-x64
```

- Runs the test projects (skip with `--skip-tests`), publishes the console tool
  and GUI to `build/artifacts/{cli,gui}/<rid>`, and builds the Velopack
  installer into `artifacts/velopack/releases/<rid>`.
- A publish with `--runtime` is self-contained; `--framework-dependent` drops the runtime.
- `--no-installer` skips the installer and the release notes requirement.

Release notes follow `docs/release-notes.md`; versions follow `docs/versioning.md`.

## Key Concepts

- **Catalog (`gui/DriftBuster.Backend/Detection/Catalog/`)** – central listing
  of detection capabilities, metadata, and sampling rules.
- **Plugins (`gui/DriftBuster.Backend/Detection/Plugins/`)** – individual
  format detectors, registered through `DefaultPlugins`.
- **Profiles (`docs/configuration-profiles.md`)** – run profiles describe what
  to collect; detection profiles describe the configuration files you expect.
- **Hunt rules (`gui/DriftBuster.Backend/Hunt/`)** – skim snapshots for
  dynamic values such as hostnames, thumbprints, and connection strings.

Check `docs/` for deeper dives:

- `docs/profile-usage.md` – practical walkthrough of profiles and hunts.
- `docs/format-support.md` – current detector coverage.
- `docs/customization.md` – sampling and plugin ordering.
- `docs/testing-strategy.md` – how detectors and reporting are validated.
- `docs/versioning.md` – component version workflow.
- `docs/registry.md` – Windows Registry live scan overview.

## Running Tests

```sh
./scripts/verify_coverage.sh
```

The script runs the Backend, CLI and GUI test projects with coverlet, merges
their reports and fails below 83% total line coverage (override with
`DOTNET_THRESHOLD`). When `pwsh` is on `PATH` it also runs the Pester suites
for the PowerShell module and the offline runner.

Lint and format checks:

```sh
./scripts/lint_all.sh
```

This runs `dotnet format DriftBuster.sln --verify-no-changes` and
`scripts/lint_powershell.ps1` (PSScriptAnalyzer).

Optional local check: secret scanning with `gitleaks dir . -v`.

### New Format Plugins

- Follow `docs/format-addition-guide.md` and the checklist in `docs/plugin-test-checklist.md`.
- Add plugin tests under `gui/DriftBuster.Backend.Tests/Detection/Plugins/`.

## Project Layout

```
gui/DriftBuster.Backend/        # Engine: detection, diff, hunt, profiles, scheduling, registry, SQL, reporting
gui/DriftBuster.Gui/            # Avalonia desktop app
gui/DriftBuster.Backend.Tests/  # Backend tests
gui/DriftBuster.Gui.Tests/      # Headless GUI tests
cli/DriftBuster.Cli/            # driftbuster console tool
cli/DriftBuster.Cli.Tests/      # Console tool tests
cli/DriftBuster.PowerShell/     # PowerShell module (+ Pester tests in DriftBuster.PowerShell.Tests)
scripts/                        # Offline runner, coverage, lint and packaging scripts
fixtures/, samples/             # Sanitised test fixtures and sample configs
docs/                           # Guides and references
```

## Contributing

1. Fork and branch from `main`.
2. Run `./scripts/verify_coverage.sh` and `./scripts/lint_all.sh` before opening a pull request.
3. Document provenance in the PR template and update relevant guides.

See `CONTRIBUTING.md`, `docs/legal-safeguards.md`, and
`docs/reviewer-checklist.md` for detailed expectations.

## License

DriftBuster is licensed under the Apache License 2.0 (`LICENSE`). Related legal
documents live in `NOTICE`, `CLA/INDIVIDUAL.md`, and `CLA/ENTITY.md`.
