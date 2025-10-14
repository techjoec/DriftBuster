# XML Config Verification Checklist

Track manual runs that validate the XML detector plus hunt token coverage.

## Run Matrix

| Variant | Fixture Path | Detector Step | Hunt Step | Notes |
|---------|--------------|---------------|-----------|-------|
| web-config | `fixtures/config/web.config` | ☐ `Detector().scan_file` recorded | ☐ `hunt_path` tokens logged | |
| app-config | `fixtures/config/app.config` | ☐ `Detector().scan_file` recorded | ☐ `hunt_path` tokens logged | |
| machine-config | `fixtures/config/machine.config` | ☐ `Detector().scan_file` recorded | ☐ `hunt_path` tokens logged | |
| web-config-transform | `fixtures/config/web.Release.config` | ☐ `Detector().scan_file` recorded | ☐ `hunt_path` tokens logged | |

## Manual Steps

1. Load each fixture with the detector:

   ```python
   from pathlib import Path

   from driftbuster.core.detector import Detector
   from driftbuster.hunt import default_rules, hunt_path

   fixture = Path("fixtures/config/web.config")
   detector = Detector()
   match = detector.scan_file(fixture)
   print(match.format_name, match.variant)

   results = list(hunt_path(fixture.parent, rules=default_rules(), exclude_patterns=["*.json"]))
   for hit in results:
       if hit.path == fixture:
           print(hit.rule.name, hit.value)
   ```

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
