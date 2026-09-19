# XML Config Verification Checklist

Track manual runs that validate the XML detector plus hunt token coverage.

## Run Matrix

| Variant | Fixture Path | Detector Step | Hunt Step | Notes |
|---------|--------------|---------------|-----------|-------|
| web-config | `fixtures/config/web.config` | ☐ `scan --json` recorded | ☐ `hunt` tokens logged | |
| app-config | `fixtures/config/App.config` | ☐ `scan --json` recorded | ☐ `hunt` tokens logged | |
| machine-config | `fixtures/config/machine.config` | ☐ `scan --json` recorded | ☐ `hunt` tokens logged | |
| web-config-transform | `fixtures/config/web.Release.config` | ☐ `scan --json` recorded | ☐ `hunt` tokens logged | |

## Manual Steps

1. Scan each fixture and hunt its folder:

   ```bash
   driftbuster scan fixtures/config/web.config --json
   driftbuster hunt fixtures/config --exclude "*.json"
   ```

   The scan prints the format, variant and metadata; the hunt prints its hits as a JSON array (an empty array when
   nothing matched).

2. Save the before/after metadata JSON (baseline vs. current detector output)
   alongside manual notes in `notes/snippets/xml-config-diffs.md` under the
   appropriate heading.
3. Store raw XML snapshots and hunt output logs outside the repository (e.g.
   `~/driftbuster-samples/xml/runs/<date>/`) so sensitive data never lands in
   Git. Reference that location in the notes column of the run matrix.
4. When transforms are involved, ensure `config_transform_scope` and the
   referenced hunt tokens capture the expected scope (web/app). Document any
   mismatches directly below the relevant heading in
   `notes/snippets/xml-config-diffs.md`.

## Outstanding Edge Cases

List XML files that still require manual investigation.

| Fixture | Issue | Follow-up |
|---------|-------|-----------|
| | | |
