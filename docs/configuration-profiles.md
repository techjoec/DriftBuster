# Configuration Profiles

Configuration profiles let you describe expected configuration files (paths,
formats, metadata) and activate those expectations using tags such as server
IDs, environments, application names, versions, or branches. Use them to compare
detector output against the presets you maintain for each deployment.

Need a quick refresher? Start with [`profile-usage.md`](profile-usage.md) and
return here for data model details.

## Data Model

### `ProfileConfig`

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `identifier` | `str` | — | Stable key used for lookups and diffs. |
| `path` | `str` or `None` | `None` | Exact POSIX-style relative path. Normalised on instantiation. |
| `path_glob` | `str` or `None` | `None` | POSIX glob alternative when multiple files apply. Normalised like `path`. |
| `application` / `version` / `branch` | `str` or `None` | `None` | Convenience helpers that match tags such as `application:<value>`. |
| `tags` | `frozenset[str]` | `frozenset()` | Additional tag requirements beyond helper shortcuts. Whitespace is trimmed. |
| `expected_format` / `expected_variant` | `str` or `None` | `None` | Hints aligning with entries in `driftbuster.catalog`. |
| `metadata` | `Mapping[str, Any]` | empty mapping | Immutable mapping (wrapping a shallow copy) for owner/context notes. |

### `ConfigurationProfile`

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `name` | `str` | — | Unique profile identifier required during registration. |
| `description` | `str` or `None` | `None` | Optional free-form text clarifying purpose. |
| `tags` | `frozenset[str]` | `frozenset()` | Activation tags; profile applies when they are a subset of provided tags. |
| `configs` | `tuple[ProfileConfig, ...]` | `()` | Ordered, immutable list of expectations. |
| `metadata` | `Mapping[str, Any]` | empty mapping | Immutable metadata for profile-wide annotations. |

### `ProfileStore`

- Manages immutable profile/config instances, rejecting duplicate profile names
  or config identifiers at registration or update time.
- Supports serialisation via `to_dict()` / `from_dict()`.
- `find_config(identifier)` returns the `(profile, config)` pairing for a stored
  identifier, or an empty tuple when it does not exist.
- `applicable_profiles(tags)` returns the profiles activated by the supplied tag set while normalising tags internally.
- `matching_configs(tags, relative_path=...)` reuses `applicable_profiles` and yields `(profile, config)` pairs matching the behaviour used by `Detector.scan_with_profiles`.
- `summary()` / `diff_summary_snapshots()` offer immutable snapshots for manual
  audits.
- `python -m driftbuster.profile_cli summary profiles.json --output summary.json` builds the summary payload from a stored ProfileStore JSON file, and
  `python -m driftbuster.profile_cli diff baseline-summary.json current-summary.json`
  diffs two saved summaries without writing throwaway scripts.
  When these helpers print an `error:` message, check the referenced file path
  before retrying; the CLI exits cleanly instead of surfacing a traceback.
- `update_profile(name, mutator)` clones the stored profile, applies the
  provided mutator, validates it, and reindexes the result without mutating the
  original instance.
- `remove_config(profile_name, config_id)` removes a specific config while
  raising descriptive errors when the profile or identifier is missing.

### `ProfiledDetection`

- Result returned by `Detector.scan_with_profiles` combining detection output
  with the matching profile/config pairs.
- Store dynamic expectations (e.g., `{ "expected_dynamic": ["certificate-thumbprint"] }`) in the `metadata` field so hunt
  results can cross-reference profiles.
- When you confirm a hunt token, record it using the rule's ``token_name`` so
  manual tooling can align detections, hunts, and profiles. Keep the mapping in
  profile-level metadata and treat it as the single source of truth for dynamic
  expectations.
- Use `notes/snippets/token-catalog.py` to generate hashed summaries of hunt
  hits (`catalog_variant`, `sample_hash`) before updating
  `notes/checklists/token-approval.md`; this keeps JSON profile additions
  aligned with the new detector metadata.

    ```python
    from driftbuster import default_rules, hunt_path

    hits = hunt_path(
        "./deployments/prod-web-01",
        rules=default_rules(),
        return_json=True,
    )

    dynamic_tokens = sorted(
        {payload["rule"]["token_name"] for payload in hits if payload["rule"]["token_name"]}
    )

    profile_metadata = {
        "expected_dynamic": dynamic_tokens,
        "last_hunt_sample": "deployments/prod-web-01",
    }
    ```

  The structured output keeps the excerpts available for manual review without
  mutating stored profiles. Reference the excerpts from the hunt checklist
  rather than embedding sensitive data in the metadata itself.

## Defining Profiles

```python
from driftbuster import ConfigurationProfile, ProfileConfig, ProfileStore

store = ProfileStore([
    ConfigurationProfile(
        name="prod-web",
        description="Production web tier baseline",
        tags={"env:prod", "tier:web"},
        configs=(
            ProfileConfig(
                identifier="web-config",
                path="web/App.config",
                application="inventory-service",
                expected_format="structured-config-xml",
                metadata={"owner": "configs@corp"},
            ),
            ProfileConfig(
                identifier="iis-settings",
                path_glob="iis/*.config",
                tags={"server:us-east-1"},
                expected_variant="web-config",
            ),
        ),
    ),
])
```

You can also hydrate from dictionaries (useful for YAML/JSON input):

```python
store = ProfileStore.from_dict({
    "profiles": [
        {
            "name": "staging",
            "tags": ["env:staging"],
            "configs": [
                {"id": "logging", "path": "logging/appsettings.json", "expected_format": "json"}
            ],
        }
    ]
})
```

## Applying Profiles During Scans

```python
from driftbuster import Detector

detector = Detector()
results = detector.scan_with_profiles(
    "./deployments/prod-web-01",
    profile_store=store,
    tags=["env:prod", "tier:web", "server:us-east-1", "application:inventory-service"],
)

for entry in results:
    print(entry.path)
    if entry.detection:
        print("  detected:", entry.detection.format_name, entry.detection.variant)
    for prof in entry.profiles:
        print("  profile:", prof.profile.name, "->", prof.config.identifier)

untagged = store.applicable_profiles(["tier:web"])
print("profiles ready for tier:web:", [profile.name for profile in untagged])

matched = store.matching_configs(
    ["env:prod", "tier:web", "application:inventory-service"],
    relative_path="web/App.config",
)
print("config hits:", [(entry.profile.name, entry.config.identifier) for entry in matched])
```

- Tags supplied to `scan_with_profiles` determine which profiles and configs
  activate. A profile applies when all its tags are present. A config applies
  when its own tags (and `application`/`version`/`branch` helpers) match the
  tag set.
- Path matching prefers exact `path` equality, falling back to glob checks via
  `path_glob` (both normalised to POSIX-style separators).
- If no config matches a file, `profiles` will be an empty tuple.
- When profiles and hunts run together, capture the current hunt summary and
  store the token names you expect in the related profile metadata. Doing so
  keeps drift reviews focused on mismatches instead of rediscovering approved
  dynamic values.
- The `hunt-bridge` subcommand in `driftbuster.profile_cli` aligns hunt hits
  with profile configs. See `docs/hunt-mode.md#bridging-hunts-with-profiles`
  for the command workflow and manual logging guidance.

## Combined profile + hunt quickstart

Use this flow to register a profile, run a hunt, and log dynamic tokens for the
next review cycle.

```python
from driftbuster import (
    ConfigurationProfile,
    ProfileConfig,
    ProfileStore,
    default_rules,
    hunt_path,
)

store = ProfileStore([
    ConfigurationProfile(
        name="prod-web",
        configs=(
            ProfileConfig(
                identifier="web-config",
                path="web/App.config",
                metadata={"expected_dynamic": ["server_name", "certificate_thumbprint"]},
            ),
        ),
    ),
])

hits = hunt_path(
    "./deployments/prod-web-01",
    rules=default_rules(),
    exclude_patterns=("**/logs/*", "**/*.bak"),
    return_json=True,
)

for payload in hits:
    token = payload["rule"]["token_name"] or payload["rule"]["name"]
    print(payload["relative_path"], token, payload["excerpt"])
```

- Filter noisy paths with `exclude_patterns` before recording approvals.
- Use the structured payload to update profile metadata or external runbooks
  without serialising the entire hunt output.
- Log approvals and masking decisions in
  `notes/checklists/hunt-profile-review.md` immediately after running the
  script.

## Serialisation & Export

- `ProfileStore.to_dict()` yields a serialisable snapshot suitable for writing
  to JSON/YAML.
- Use `ProfileStore.register_profile()` / `remove_profile()` to maintain the
  store programmatically.

## Lookup & Validation

- `register_profile()` raises `ValueError` if a profile name or config ID has
  already been registered. This prevents accidental overwrites when multiple
  maintainers manage profiles in parallel.
- `ProfileStore.find_config()` is handy when you need to trace where a config
  lives. For example:

  ```python
  match = store.find_config("web-config")
  if match:
      applied = match[0]
      print(applied.profile.name, "->", applied.config.path)
  ```
- Pair the lookup helper with `ProfileStore.summary()` to audit counts whenever
  profiles change. The snapshot is immutable and sorted by profile name so it
  stays stable for diffs.

    ```python
    summary = store.summary()
    print("profiles:", summary["total_profiles"], "configs:", summary["total_configs"])
    for entry in summary["profiles"]:
        print(entry["name"], "configs:", entry["config_count"], "ids:", ", ".join(entry["config_ids"]))
    ```
    To compare two snapshots without exporting the full payload, feed the
    summaries to `diff_summary_snapshots`:
    ```python
    from driftbuster.core import diff_summary_snapshots

    previous = store.summary()
    # ... mutate the store or load a new payload ...
    current = store.summary()
    diff = diff_summary_snapshots(previous, current)
    print("added profiles", diff["added_profiles"])
    for entry in diff["changed_profiles"]:
        print(
            entry["name"],
            "+",
            entry["added_config_ids"],
            "-",
            entry["removed_config_ids"],
        )
    ```
    The helper is also re-exported from the root package, so quick scripts can
    `from driftbuster import diff_summary_snapshots` without drilling into
    submodules.
- When the totals or identifiers differ from the previous run, chase down the
  delta using `find_config()` for the affected IDs before promoting the change.
- When editing profiles manually, instantiate the store and call
  `ProfileStore.find_config()` for the identifiers you touched. Follow the
  existing manual lint guidance (`python -m compileall src`) to ensure
  everything still imports cleanly.

## Best Practices

- Use explicit tags (`env:prod`, `server:host01`, `branch:main`) so profile
  matching stays deterministic.
- Keep identifiers stable—they’re the primary link between scan results,
  documentation, and runbooks.
- Treat the profile store as configuration data; keep it alongside your infra
  definitions or deployment manifests.
