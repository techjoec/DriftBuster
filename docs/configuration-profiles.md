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
| `id` | string | — | Stable key used for lookups and diffs (`Identifier`). |
| `path` | string or null | null | Exact POSIX-style relative path. Normalised on load. |
| `path_glob` | string or null | null | Wildcard alternative when multiple files apply: `*` matches any run of characters (`/` included), `?` one character. Normalised like `path`. |
| `application` / `version` / `branch` | string or null | null | Convenience helpers that match tags such as `application:<value>`. |
| `tags` | array of strings | `[]` | Additional tag requirements beyond helper shortcuts. Whitespace is trimmed. |
| `expected_format` / `expected_variant` | string or null | null | Hints aligning with catalog formats and variants. |
| `metadata` | object | `{}` | Read-only copy for owner/context notes. |

### Profile (`DetectionProfile`)

| JSON key | Type | Default | Notes |
| --- | --- | --- | --- |
| `name` | string | — | Unique profile identifier required during registration. |
| `description` | string or null | null | Optional free-form text clarifying purpose. |
| `tags` | array of strings | `[]` | Activation tags; the profile applies when they are a subset of the provided tags. |
| `configs` | array | `[]` | Ordered list of config entries. |
| `metadata` | object | `{}` | Profile-wide annotations. |

A store is `{"profiles": [ ... ]}`.

### `DetectionProfileStore`

- Rejects duplicate profile names or config identifiers at registration or
  update time.
- `FromDict(payload)` / `ToDict()` load and save the JSON payload.
- `FindConfig(identifier)` returns the applied profile/config pairing for a
  stored identifier, or an empty list when it does not exist.
- `ApplicableProfiles(tags)` returns the profiles activated by the supplied
  tag set, normalising tags internally.
- `MatchingConfigs(tags, relativePath)` returns the profile/config pairs that
  apply to a path, the same matching `Detector.ScanWithProfiles` uses.
- `Summary()` and `DiffSummarySnapshots(baseline, current)` produce snapshots
  for manual audits; `driftbuster detection-profile summary|diff` runs them
  over JSON files.
- `UpdateProfile(name, mutator)` applies a mutator to a copy of the stored
  profile, validates it, and reindexes the result.
- `RemoveConfig(profileName, configId)` removes a specific config and reports
  a missing profile or identifier.

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

var store = DetectionProfileStore.FromDict(DetectionProfileCommands.LoadJson("profiles.json"));
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
        Console.WriteLine($"  profile: {applied.Profile.Name} -> {applied.Config.Identifier}");
    }
}
```

- Tags determine which profiles and configs activate. A profile applies when
  all its tags are present. A config applies when its own tags (and
  `application`/`version`/`branch` helpers) match the tag set.
- Path matching prefers exact `path` equality, falling back to `path_glob`
  against the whole relative path (both normalised to POSIX-style
  separators). Because `*` crosses `/`, `configs/*.json` also matches
  `configs/sub/app.json`. Matching ignores case on Windows only.
- If no config matches a file, `Profiles` is empty.
- When an applied config's metadata sets `ignore_review_flags` to true, a
  detection flagged `needs_review` is marked `review_ignored` instead.
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
  `total_configs`, and per profile `config_count` and `config_ids`.
- The diff reports `totals`, `added_profiles`, `removed_profiles` and
  `changed_profiles` (with `added_config_ids` and `removed_config_ids`).
- `--indent 0` writes compact JSON; `--sort-keys` sorts keys.
- When the totals or identifiers differ from the previous run, look up the
  affected IDs before promoting the change.

## Schedules

Run profile schedules live in `Profiles/schedules.json` as
`{"schedules": [ ... ]}`; the GUI schedule cards and `driftbuster schedule`
read and write the same manifest, with runtime state in
`Profiles/scheduler-state.json`.

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `name` | string | — | Unique identifier for the scheduled run. |
| `profile` | string | — | Profile name or path the schedule runs. |
| `every` | string or number | — | Interval such as `"15m"`, `"1h30m"`, `"PT1H"`, or seconds (`900`). |
| `start_at` | ISO 8601 string or null | null | Optional first-run anchor (`yyyy-MM-dd[THH:mm[:ss[.fraction]][Z\|±HH:MM]]`; no offset means UTC). Defaults to now when omitted. |
| `window.start` / `window.end` | `H:MM[:SS]` | — | Optional run window in local time, bounds inclusive. When start > end the window is treated as overnight. |
| `window.timezone` | IANA zone string | `"UTC"` | Time zone used to evaluate the window, resolved through the operating system's time zone data. |
| `tags` | array of strings | `[]` | Free-form labels surfaced on due runs. |
| `metadata` | object | `{}` | Arbitrary JSON metadata mirrored into schedule payloads. |

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
six-digit fraction only when it is not zero.

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
  configs. Supply a token plus optional keyword/regex filters. Remote capture
  targets are configured via `remote` (single host) and `remote_batch`
  (additional hosts) objects.
- Supported remote keys: `host` (required), `username`, `password_env`,
  `credential_profile`, `transport`, `port`, `use_ssl`, and `alias`. Raw
  `password` values are rejected – reference environment variables instead.
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
              "username": "DOMAIN\\\\collector",
              "password_env": "DRIFTBUSTER_REMOTE_PASS"
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
