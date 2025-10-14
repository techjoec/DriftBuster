# Profile summary checklist

- **Snapshot command:**
  - Run within the repository root after updating profiles:

    ```bash
    PYTHONPATH=src python - <<'PY'
    from driftbuster.core import ProfileStore
    from pathlib import Path
    import json

    payload = json.loads(Path("profiles.json").read_text())
    store = ProfileStore.from_dict(payload)
    summary = store.summary()
    print("profiles", summary["total_profiles"], "configs", summary["total_configs"])
    for entry in summary["profiles"]:
        print(entry["name"], entry["config_count"], ", ".join(entry["config_ids"]))
    PY
    ```
  - Or capture the summary in a file with the CLI helper when you already
    have a stored JSON payload:

    ```bash
    python -m driftbuster.profile_cli summary profiles.json --output profile-summary.json
    ```
  - If the command prints `error: unable to read JSON payload`, verify the path
    and rerun once the payload is accessible.
- **What to log:** Record the total profile/config counts and keep the ordered
  identifier list for diffing against previous runs.
- **If counts drift:** Use `ProfileStore.find_config()` on each unexpected ID to
  confirm which profile changed or was removed before shipping the update.
- **Diff snapshots:**
  - Capture the previous and current summaries in-memory and compare them using
    `diff_summary_snapshots` to list profile/config deltas without exporting the
    entire store.

    ```bash
    # Use notes/snippets/profile-summary-diff.py for a reusable script.
    PYTHONPATH=src python - <<'PY'
    import json
    from pathlib import Path
    from driftbuster.core import ProfileStore, diff_summary_snapshots

    baseline = ProfileStore.from_dict(json.loads(Path("baseline-profiles.json").read_text()))
    current = ProfileStore.from_dict(json.loads(Path("current-profiles.json").read_text()))
    diff = diff_summary_snapshots(baseline.summary(), current.summary())
    print("added profiles", diff["added_profiles"])
    for entry in diff["changed_profiles"]:
        print(entry["name"], "added", entry["added_config_ids"], "removed", entry["removed_config_ids"])
    PY
    ```
  - Prefer the CLI when stored summaries already exist for comparison:

    ```bash
    python -m driftbuster.profile_cli diff baseline-summary.json current-summary.json
    ```
  - A non-zero exit code indicates the CLI handled an error (missing file,
    invalid JSON); fix the input before proceeding.

- **Import verification:** Capture the helper availability before closing the
  diff:

  ```bash
  PYTHONPATH=src python - <<'PY'
  from driftbuster.core import ProfileStore, diff_summary_snapshots
  from driftbuster import diff_summary_snapshots as root_helper

  store = ProfileStore()
  assert diff_summary_snapshots is root_helper
  print("core export:", diff_summary_snapshots)
  print("applicable_profiles", store.applicable_profiles(["env:test"]))
  PY
  ```
  Log the console output alongside the summary artifacts so future manual runs
  can confirm the callable is ready for use.
  - 2025-10-16: Confirmed `ProfileStore().applicable_profiles` import and call:

    ```bash
    PYTHONPATH=src python - <<'PY'
    from driftbuster.core.profiles import ProfileStore, ConfigurationProfile

    store = ProfileStore([
        ConfigurationProfile(name="empty"),
    ])

    print("callable:", ProfileStore.applicable_profiles)
    print("instance result:", store.applicable_profiles(["env:demo"]))
    PY
    ```

    Output logged:

    ```
    callable: <function ProfileStore.applicable_profiles at 0x7fada1048680>
    instance result: (ConfigurationProfile(name='empty', description=None, tags=frozenset(), configs=(), metadata=mappingproxy({})),)
    ```
- **Follow-up:** When a config disappears unexpectedly, re-run the manual lint
  steps (`python -m compileall src`) once the profile adjustments are settled.
