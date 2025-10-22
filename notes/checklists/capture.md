# Capture pipeline checklist

- **Prepare environment:**
  - Collect redaction tokens before running the capture script. Store them in a
    secure shell history-free session.
  - Confirm the target directory is accessible and does not contain prior
    snapshots that should be retained.
- **Run snapshot:**
  - Execute the manual helper with explicit metadata:

    ```bash
    python scripts/capture.py run /path/to/configs \
      --profiles profiles.json \
      --environment staging \
      --operator "analyst@example" \
      --reason "weekly review" \
      --mask-token "secret-token" \
      --mask-token "api-key"
    ```
  - If profiles are not required, omit `--profiles` and the script will still
    record detections + hunts.
  - Runs that truly cannot use masking must append `--allow-unmasked` to bypass
    the guard (log the justification in the legal notes).
- **Inspect manifest:**
  - Open `<capture-id>-manifest.json` and log the detection count, hunt hit
    count, and the recorded durations.
  - Verify that `mask_token_count` reflects the number of tokens supplied and
    that `total_redactions` is non-zero when masking is expected.
  - Record the manifest path plus capture ID in the legal review notes.
- **Review snapshot:**
  - Confirm `relative_path` entries resolve correctly from the capture root.
  - Ensure hunt hits that should map to approved tokens list the expected
    `token_name`; investigate any unexpected entries before proceeding.
  - Keep the snapshot in the restricted storage location defined in
    `CLOUDTASKS.md` (see areas A10-A12).
  - Capture any tooling gaps or repetitive manual steps and log them in the
    automation backlog tracker for future improvements.
- **Comparison step:**
  - Use the same capture ID when diffing successive runs:

    ```bash
    python scripts/capture.py compare captures/prev-snapshot.json captures/current-snapshot.json
    ```
  - Log added, removed, and changed detection keys along with token deltas in
    the manual audit notes.
- **Cleanup:**
  - When the review closes, purge the snapshot and manifest together and note
    the deletion time in the legal log.
