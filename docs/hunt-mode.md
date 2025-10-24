# Hunt Mode & Dynamic Content Detection

Hunt mode supplements format detection by looking for dynamic values that vary
per server, environment, or installation (hostnames, certificate thumbprints,
version numbers, paths, etc.). Use it to audit drift-prone settings and to
prepare data for future config generation.

## Quick Start

```python
from driftbuster import default_rules, hunt_path

results = hunt_path(
    "./deployments/prod-web-01",
    rules=default_rules(),
    exclude_patterns=("**/logs/*", "**/*.bak"),
)

for hit in results:
    print(f"{hit.path}:{hit.line_number} [{hit.rule.name}] {hit.excerpt}")
```

- `default_rules()` returns baseline heuristics for server names, certificate
  thumbprints, version numbers, and installation paths. Each rule now exposes a
  ``token_name`` that you can store inside configuration profile metadata.
- Skip noisy paths by passing `exclude_patterns`. Patterns apply to the
  filesystem path and the scan-relative path.
- Each `HuntHit` includes the rule that fired, the file path, and a snippet to
  review manually.

### Structured output for notebooks and approvals

```python
from driftbuster import default_rules, hunt_path

payload = hunt_path(
    "./deployments/prod-web-01",
    rules=default_rules(),
    return_json=True,
)

for entry in payload:
    token = entry["rule"]["token_name"] or entry["rule"]["name"]
    print(entry["relative_path"], token, entry["excerpt"])
```

- `return_json=True` produces dictionaries ready for logging or serialisation.
- The structured payload retains rule metadata, excerpts, and relative paths so
  you can cross-link output with manual checklists without re-reading files.
- Feed the `token_name` column into configuration profile metadata (see
  `docs/configuration-profiles.md`) so drift reviews know which values are
  expected.
- When a rule exposes a `token_name`, the entry includes
  `metadata.plan_transform` containing the detected value and templated
  placeholder (defaults to `{{ token_name }}`) for diff plans or approval logs.

### Plan transforms & placeholders

```python
from driftbuster import build_plan_transforms, default_rules, hunt_path

hits = hunt_path("./deployments/prod-web-01", rules=default_rules())
transforms = build_plan_transforms(hits)

for transform in transforms:
    print(transform.token_name, "=>", transform.placeholder, transform.value)

# Custom placeholder style
transforms = build_plan_transforms(
    hits,
    placeholder_template="<<{token_name}>>",
)
```

- `build_plan_transforms` deduplicates hits per file/line and pairs each
  `token_name` with the matched value.
- Feed the resulting placeholders into diff plans
  (`driftbuster.core.diffing.build_diff_plan(mask_tokens=[...])`) or token
  catalog scripts without re-parsing hunt excerpts.
- Override `placeholder_template` to match your templating engine (e.g.,
  `<<token>>`, `%TOKEN%`).

## Bridging hunts with profiles

Use the profile CLI to line up hunt hits with the configuration expectations
stored in your `ProfileStore` payload.

```bash
python -m driftbuster.profile_cli hunt-bridge profiles.json hunt-results.json \
  --tag env:prod --tag tier:web --root deployments/prod-web-01 \
  --output hunt-profile-bridge.json
```

- `profiles.json` mirrors the payload accepted by `ProfileStore.from_dict`.
- `hunt-results.json` is the JSON array returned by `hunt_path(...,
  return_json=True)`.
- Repeat `--tag` for every activation tag required by the relevant profile.
- Use `--root` when hunt output recorded absolute paths; the CLI converts them
  into POSIX-style relatives before querying `ProfileStore.matching_configs`.
- The resulting JSON (`items`) lists each hunt hit, the resolved relative path,
  and any matching `(profile, config)` pairs plus expected format/variant hints.
- See `notes/snippets/profile-hunt-bridge.py` for a reusable script that wraps
  the same logic inside Python while HOLD keeps automation on pause.

## Custom Rules

```python
from driftbuster import HuntRule

db_rule = HuntRule(
    name="database-connection",
    description="Connection strings referencing SQL hosts",
    token_name="database_server",
    keywords=("connection", "server"),
    patterns=(r"Server=([^;]+)",),
)

results = hunt_path(
    "./deployments",
    rules=(db_rule,),
    glob="**/*.config",
)
```

- `keywords` provide cheap filters (case-insensitive substring matches).
- `patterns` are regexes (compiled automatically) used to flag lines for review.
- `token_name` keeps downstream metadata predictable. Reuse the same token names
  inside configuration profile metadata and checklists.

## Workflow Suggestions

1. Run the detector first to identify formats and profile mismatches.
2. Use hunt mode on the same tree to highlight dynamic values.
3. Move confirmed entries into configuration profile metadata using the matching
   `token_name`. Keep the authoritative mapping in source control.
4. Capture approvals in `notes/checklists/hunt-profile-review.md`, recording the
   reviewer, date, excerpts, and masking decisions.
5. When capturing snapshots/diffs, store hunt results alongside detection output
   so future comparisons can highlight changes without re-running the scans.

## Transformation Roadmap

- Integrate hunt findings into future diff/patch adapters so drift reports can
  separate "expected dynamic" vs. "unexpected" changes. The structured payload
  already exposes the rule `token_name`, so diff tooling can line up approvals
  without reverse-engineering regex strings.
- Feed confirmed dynamic values into config generation templates (e.g., token
  replacement) once automation pipelines are in place. Track approvals using the
  hunt/profile checklist so placeholders stay in sync.
- Expand the default rule set as new formats land (JSON appsettings, YAML
  manifests, PowerShell scripts). Always define a `token_name` for new rules so
  metadata consumers stay consistent.
- Track accepted dynamic values (e.g., approved certificate thumbprints) in a
  profile metadata map so future tooling can substitute them automatically. Use
  the JSON hunt output to populate those maps without copying raw excerpts.

## Token Mapping & Approval Flow

Link hunt rules to placeholder names before any token substitution work. The
default catalogue already exposes `token_name` for core rules; record the
mapping explicitly so profiles, diffs, and future templating agree on wording.

| Hunt rule | Suggested token | Notes |
| --- | --- | --- |
| `database-connection` | `database_server` | Matches hostname extracted from connection strings. |
| `tls-thumbprint` | `certificate_thumbprint` | Preserve uppercase formatting for approval diffs. |
| `service-endpoint` | `service_url` | Normalise scheme + host only; ignore query parameters. |

### Before/after example

```ini
; before approval
connectionString=Server=prod-db-01.internal;Database=main

; after approval with token substitution
connectionString=Server={{ database_server }};Database=main
```

- Keep raw values outside source control. Store the approved token placeholder
  (`{{ database_server }}`) in the configuration profile metadata and refer to
  the structured hunt output for the original excerpt when needed.
- When drafting templates, copy the placeholder notation exactly so diff output
  can detect tokenised replacements and suppress expected churn.

### Manual review steps

1. Run hunt mode with `return_json=True` to capture candidate values.
2. Compare each hit against the relevant configuration profile entry and record
   the decision in `notes/checklists/hunt-profile-review.md`.
3. Use `notes/snippets/token-catalog.py --hunts hunt-results.json` to build a
   hashed catalog skeleton, then replicate the rule → token mapping in
   `notes/checklists/token-approval.md` before editing any config files. Record
   the catalog variant (`structured-settings-json`, etc.) and the hashed JSON
   sample reference when applicable.
4. Replace the raw value with the placeholder in your working copy, then rerun
   the detector and hunt scans to confirm no new hits appear.
5. Archive the structured hunt output alongside the approval log so future
   reviews can confirm the placeholder still matches the rule definition.
