# Configuration Profiles

Detection profiles let you describe expected configuration files (paths,
formats, metadata) and activate those expectations using tags such as server
IDs, environments, application names, versions, or branches. Use them to compare
detector output against the presets you maintain for each deployment.

Need a quick refresher? Start with [`profile-usage.md`](profile-usage.md) and
return here for data model details. The types live in
`gui/DriftBuster.Backend/Profiles/Detection/`.

## Data Model

### Config entry (`DetectionProfileConfig`)

| JSON key | Type | Default | Notes |
| --- | --- | --- | --- |
| `id` | string | — | Stable key used for lookups and diffs; unique across the store. |
| `path` | string or null | null | Exact relative path with forward slashes. |
| `path_glob` | string or null | null | Wildcard alternative when multiple files apply: `*` matches any run of characters (`/` included), `?` one character. |
| `application` / `version` / `branch` | string or null | null | Convenience helpers that match tags such as `application:<value>`. |
| `tags` | array of strings | `[]` | Additional tag requirements beyond helper shortcuts. |
| `expected_format` / `expected_variant` | string or null | null | Hints aligning with catalog formats and variants. |
| `ignore_review_flags` | bool | false | A matching detection flagged `needs_review` is marked `review_ignored` instead. |
| `metadata` | object of strings | `{}` | Owner/context notes. |

### Profile (`DetectionProfile`)

| JSON key | Type | Default | Notes |
| --- | --- | --- | --- |
| `name` | string | — | Unique profile identifier required during registration. |
| `description` | string or null | null | Optional free-form text clarifying purpose. |
| `tags` | array of strings | `[]` | Activation tags; the profile applies when they are a subset of the provided tags. |
| `configs` | array | `[]` | Ordered list of config entries. |
| `metadata` | object of strings | `{}` | Profile-wide annotations. |

A store is `{"profiles": [ ... ]}`, read strictly: an unknown or repeated key, a
missing `name` or `id`, or a value of the wrong type stops the command with the
file, the JSON path and the reason.

### `DetectionProfileStore`

- `DetectionProfileStore.Load(path)` reads a store file; the constructor
  refuses a repeated profile name or config id.
- `MatchingConfigs(tags, relativePath)` returns the profile/config pairs that
  apply to a path, the same matching `Detector.ScanWithProfiles` uses.
- `Summary()` lists the profiles; `DetectionProfileCommands.Diff` compares two
  summaries. `driftbuster detection-profile summary|diff` runs them over files.

### `ProfiledDetection`

- Result returned by `Detector.ScanWithProfiles` combining detection output
  with the matching profile/config pairs.
- Store dynamic expectations (e.g., `{ "expected_dynamic": ["certificate_thumbprint"] }`)
  in the `metadata` field so hunt results can cross-reference profiles.
- When you confirm a hunt token, record it using the rule's `token_name` so
  manual tooling can align detections, hunts, and profiles. Keep the mapping in
  profile-level metadata and treat it as the single source of truth for dynamic
  expectations.

## Applying Profiles During Scans

```csharp
using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Profiles.Detection;

var store = DetectionProfileStore.Load("profiles.json");
var detector = new Detector();
var results = detector.ScanWithProfiles(
    "./deployments/prod-web-01",
    store,
    tags: ["env:prod", "tier:web", "server:us-east-1", "application:inventory-service"]);

foreach (var entry in results)
{
    Console.WriteLine($"{entry.Path} {entry.Detection?.FormatName} {entry.Detection?.Variant}");
    foreach (var applied in entry.Profiles)
    {
        Console.WriteLine($"  profile: {applied.Profile.Name} -> {applied.Config.Id}");
    }
}
```

- Tags determine which profiles and configs activate. A profile applies when
  all its tags are present. A config applies when its own tags (and
  `application`/`version`/`branch` helpers) match the tag set.
- Path matching prefers exact `path` equality (case-sensitive), falling back to `path_glob`
  against the whole relative path (with forward slashes). Because `*` crosses
  `/`, `configs/*.json` also matches `configs/sub/app.json`. `path_glob`
  matching ignores case on Windows only.
- If no config matches a file, `Profiles` is empty.
- When an applied config sets `ignore_review_flags`, a detection flagged
  `needs_review` is marked `review_ignored` instead.
- When profiles and hunts run together, store the token names you expect in the
  related profile metadata so drift reviews focus on mismatches instead of
  rediscovering approved dynamic values.
- `driftbuster detection-profile hunt-bridge` aligns hunt hits with profile
  configs. See `docs/hunt-mode.md#bridging-hunts-with-profiles`.

## Summaries and Diffs

```sh
driftbuster detection-profile summary profiles.json --output summary.json
driftbuster detection-profile diff baseline-summary.json current-summary.json
```

- The summary is sorted by profile name and lists `total_profiles`,
  `total_configs`, and per profile `tags` and `config_ids`.
- The diff reports the `baseline` and `current` totals, `added_profiles`,
  `removed_profiles` and `changed_profiles` (with both config counts,
  `added_config_ids` and `removed_config_ids`).
- When the totals or identifiers differ from the previous run, look up the
  affected IDs before promoting the change.

## Schedules

Run profile schedules live in `Profiles/schedules.json` as
`{"schedules": [ ... ]}`; the GUI schedule cards and `driftbuster schedule`
read and write the same manifest, with runtime state in
`Profiles/scheduler-state.json`. Both files are read strictly: an unknown or
repeated key, a missing required field or a value of the wrong type stops the
command with the file, the JSON path and the reason. A missing manifest holds no
schedules. The GUI validates every card before it saves.

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `name` | string | — | Unique identifier for the scheduled run. |
| `profile` | string | — | Profile name or path the schedule runs. |
| `every` | string | — | Interval such as `"15m"`, `"1h30m"`, `"1.5d"` or an ISO 8601 duration (`"PT1H"`, `"P1D"`). |
| `start_at` | ISO 8601 string or null | null | Optional first-run anchor (`yyyy-MM-dd[THH:mm[:ss[.fraction]][Z\|±HH:MM]]`; no offset means UTC). Defaults to now when omitted. |
| `window.start` / `window.end` | `H:MM[:SS]` | — | Optional run window in local time, bounds inclusive. When start > end the window is treated as overnight. |
| `window.timezone` | IANA zone string | `"UTC"` | Time zone used to evaluate the window, resolved through the operating system's time zone data. |
| `tags` | array of strings | `[]` | Free-form labels surfaced on due runs (trimmed, de-duplicated, sorted). |
| `metadata` | object of strings | `{}` | Text values mirrored into schedule payloads. |

```json
{
  "schedules": [
    {
      "name": "nightly-backup",
      "profile": "nightly",
      "every": "24h",
      "start_at": "2025-01-01T02:00:00Z",
      "window": { "start": "01:00", "end": "05:00", "timezone": "UTC" },
      "tags": ["env:prod"],
      "metadata": { "contact": "oncall@example.com" }
    }
  ]
}
```

```sh
driftbuster schedule list
driftbuster schedule due --at 2025-01-02T02:30:00Z
driftbuster schedule mark-complete --name nightly-backup
driftbuster schedule skip-until --name nightly-backup --resume-at 2025-01-10T02:00:00Z
```

Every stored and printed time is UTC (`2025-01-02T02:00:00+00:00`), with a
fraction only when it is not zero. `list` prints `{"schedules": [...]}`, `due`
prints `{"runs": [...]}`, and `mark-complete` / `skip-until` print the
schedule's `name`, `next_run` and `pending`.

A run that falls outside its window moves to the window start: the same local
day when it is before a daytime window (or inside an overnight window's closed
hours), the next local day when it is after a daytime window. Around daylight
saving changes the start time resolves as follows:

- **Skipped wall time** (spring forward): the offset in force before the change
  applies, so the run lands the length of the gap later on the clock: a window
  starting at `02:00` in `America/Chicago` starts at 03:00 CDT (08:00Z) on
  2025-03-09. Intervals are exact durations, so a daily run in a `02:15`-`02:45`
  window comes due at 03:15 CDT that day, past the window end, and moves to
  02:15 the next day.
- **Repeated wall time** (fall back): the earliest occurrence that is not before
  the run being aligned. A run already inside the window keeps its instant.

Due runs stay pending until marked complete. In code, `ProfileScheduler`
(`gui/DriftBuster.Backend/Scheduling/`) exposes the same `Due`, `MarkComplete`
and `SkipUntil` operations over `ScheduleSpec` entries.

## Registry Scan Sources

- `registry_scan` entries live under `profile.sources` in offline runner
  configs. Supply a token plus optional keyword/regex filters. `remote` (single
  host) and `remote_batch` (additional hosts) scan those hosts over WinRM
  instead of the local machine; see `docs/registry.md#remote-targets` for
  credentials, outputs and limits.
- Supported remote keys: `host` (required), `username`, `password_env`,
  `credential_profile`, `transport`, `port`, `use_ssl`, and `alias`. The runner
  reads its config strictly, so a raw `password` key stops the run as an
  unknown key – reference environment variables instead.
- Example profile snippet covering one headquarters host plus a branch batch:

  ```json
  {
    "profile": {
      "name": "remote-registry-collection",
      "sources": [
        {
          "alias": "hq-registry",
          "registry_scan": {
            "token": "VendorA",
            "keywords": ["server"],
            "remote": {
              "host": "hq-gateway.internal",
              "username": "DOMAIN\\collector",
              "password_env": "DRIFTBUSTER_REMOTE_PASS"
            },
            "remote_batch": [
              {"host": "branch-01.internal", "credential_profile": "creds/branch.xml"},
              "branch-02.internal"
            ]
          }
        }
      ]
    }
  }
  ```

- Generate ready-to-paste JSON with
  `driftbuster registry-scan emit-config "VendorA" --remote-target "hq-gateway.internal,username=DOMAIN\\collector,password-env=DRIFTBUSTER_REMOTE_PASS" --remote-target branch-02.internal`
  to keep schemas consistent.

## Best Practices

- Use explicit tags (`env:prod`, `server:host01`, `branch:main`) so profile
  matching stays deterministic.
- Keep identifiers stable—they’re the primary link between scan results,
  documentation, and runbooks.
- Treat the profile store as configuration data; keep it alongside your infra
  definitions or deployment manifests.
