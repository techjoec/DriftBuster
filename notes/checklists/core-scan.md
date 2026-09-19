# Core detector scan checklist

Use this when changing sampling, detection or error handling. Record results in the change's commit message or release
evidence, not in this file.

## Fixture runs

| Sample type | Command | Expect |
|-------------|---------|--------|
| Text/XML | `driftbuster scan fixtures/config --glob "*.config"` | Every `.config` detected as `structured-config-xml` with its variant (`web-config`, `app-config`, `machine-config`, `web-config-transform`). |
| JSON | `driftbuster scan fixtures/config/appsettings.json --json` | `format: json`, `bytes_sampled` equal to the file size, `sample_truncated: false`. |
| Truncated sample | `driftbuster scan fixtures/config/web.config --sample-size 64 --json` | `bytes_sampled: 64`, `sample_truncated: true`, no exception. |
| Directory walk | `driftbuster scan fixtures --json` | One JSON line per file; binary and undetected files reported without failing the run. |

In the `--json` output check `metadata.bytes_sampled`, `encoding`, `sample_truncated`, the catalog keys, and that `reasons`
has no duplicate, padded or multi-line entries.

## Profiles

`Detector.ScanWithProfiles` is covered by `gui/DriftBuster.Backend.Tests/Detection/` (profile matching, ignore review flags,
store exceptions). For a manual pass, write a detection profile store and summarise it:

```bash
driftbuster detection-profile summary profiles.json --output profile-summary.json
```

See `notes/checklists/profile-summary.md` for the summary and diff workflow.

## Unreadable file

1. Create a scratch copy of a fixture and remove read access (`chmod 000 <file>`, or deny read in its Windows ACL).
2. `driftbuster scan <file>` prints `DetectorIOException: <path>: Access to the path '<path>' is denied.` and
   exits 1.
3. Restore permissions and delete the copy.

A multi-server or GUI scan reports the same file as unreadable for that server and carries on with the rest.

## Build and lint

- `dotnet build DriftBuster.sln` finishes with zero warnings.
- `scripts/lint_all.sh` passes (`dotnet format --verify-no-changes` and PSScriptAnalyzer).
