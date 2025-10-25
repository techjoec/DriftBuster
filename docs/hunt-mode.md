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

### Collecting token candidates for approval

```python
from pathlib import Path

import json

from driftbuster import collect_token_candidates, TokenApprovalStore

hunts_payload = Path("hunt-results.json").read_text(encoding="utf-8")
hunts = json.loads(hunts_payload)
store = TokenApprovalStore.load(Path("token-approvals.json"))

candidates = collect_token_candidates(hunts, approvals=store)

for pending in candidates.pending:
    print(pending.token_name, pending.placeholder, pending.relative_path)

for approved in candidates.approved:
    print("approved", approved.token_name, approved.approval.approved_by)
```

- `collect_token_candidates` understands the JSON dictionaries returned by
  `hunt_path(..., return_json=True)` and aligns them with the approval store.
- `TokenApprovalStore` persists JSON payloads mirroring the schema documented
  in `notes/checklists/token-approval.md`. Use `TokenApprovalStore.dump` to
  write updates back to disk once reviewers capture new approvals.
- Prefer `TokenApprovalStore.dump_sqlite` / `load_sqlite` when a locked SQLite
  file is required; it mirrors the JSON schema while providing transactional
  storage for reviewers working from shared network locations.
- Approved entries surface `TokenApproval` metadata directly on the candidate
  so tooling can record reviewer IDs, timestamps, or secure storage locations
  alongside the pending queue.

### CLI surfacing plan for pending tokens

To make unresolved tokens visible without burying reviewers in raw excerpts we
will extend `driftbuster.profile_cli` with a `pending-tokens` subcommand that
wraps `collect_token_candidates` and the approval store helpers.

- **Inputs:**
  - Hunt payload JSON (`hunt_path(..., return_json=True)`).
  - Approval log path (JSON or SQLite) passed via `--approvals`.
  - Optional `--limit` (default `10`) constraining how many individual tokens
    are rendered in the default view.
  - Optional `--rules` filter repeating per token name (e.g.,
    `--rules database_server --rules hostname`).
- **Default output:**
  - Single-line summary showing total hunts inspected, number of approved
    tokens matched, and pending token count.
  - Top unresolved tokens table aggregated by `(token_name, relative_path)`
    with placeholder, last-seen timestamp, and excerpt hash. Entries are sorted
    by most recent detection so reviewers see fresh gaps first.
- **Noise controls:**
  - `--detail all` toggles the full pending list for auditors. Without this
    flag the CLI keeps output to the summary + limited table.
  - `--snooze-before <UTC>` hides tokens whose most recent detection predates a
    cutoff so historical drift does not dominate every run.
  - `--json` emits machine-readable payloads mirroring
    `TokenApprovalStore.dump` structure for automated pipelines.
- **Next steps:**
  - Wire helpers into `src/driftbuster/profile_cli.py` alongside the existing
    `summary`, `diff`, and `hunt-bridge` handlers.
  - Add regression coverage under `tests/cli/test_profile_cli.py` to lock the
    summary string, aggregation order, and noise filters.
  - Document reviewer workflows in `notes/checklists/token-approval.md` once
    the command ships.

## Realtime secret scanner telemetry

Run profile captures now mirror the offline encryption pipeline by running the
secret scrubber in-place before copying any source file. The flow is wired
through `run_profiles.execute_profile`, which hydrates a
`SecretDetectionContext` and streams log messages into the metadata payload
written alongside every run. Key behaviours to keep in mind:

- Each copied file is passed through `secret_scanning.copy_with_secret_filter`
  when textual data is detected. Matching rules replace the sensitive span with
  `[SECRET]`, append a `SecretFinding` entry, and emit messages such as
  `secret candidate redacted (PasswordAssignment) from ...` for audit trails.
  These logs persist in `metadata.json → secrets.messages` and are surfaced via
  the `ProfileRunResult.secrets` dictionary returned to callers.
- Ignore lists are honoured at two layers: profile options may specify
  `secret_ignore_rules` / `secret_ignore_patterns`, while the GUI and future CLI
  feed structured `secret_scanner` overrides. Both paths normalise values into
  sorted lists before the scan begins so deterministic manifests and hashes are
  produced.
- When no matches trigger, files are copied byte-for-byte and `rules_loaded`
  stays `True`, proving the ruleset executed without falling back to a noop.
  Binary files skip redaction automatically based on the lightweight
  `looks_binary` probe documented in `secret_scanning.py`.
- The resulting manifest enumerates rule version, ignored entries, and every
  finding (path, rule name, line, snippet). Use this metadata as the single
  source of truth when curating approvals or verifying realtime scrubber runs
  inside automated pipelines.

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

### Reporting metadata bridge

- Pair hunt output with detection matches before emitting reports. Build a
  ``DetectionMatch`` per format run, then iterate through
  :func:`driftbuster.reporting._metadata.iter_detection_payloads` to keep
  reporting payloads uniform across JSON, HTML, and diff adapters.
- Ensure every detection map exposes the canonical keys (`plugin`, `format`,
  `variant`, `confidence`, `reasons`, `metadata`). Populate hunt-derived fields
  inside the nested `metadata` map (for example, `hunts.approved_tokens` or
  `hunts.pending_reviews`) so downstream tooling can merge them with detector
  metadata without schema drift.
- Use ``extra_metadata`` when invoking ``iter_detection_payloads`` to append
  run-level context such as the hunt manifest hash, operator ID, or approval log
  reference. The helper returns new dictionaries, keeping the cached hunt
  payload intact for repeat renders and follow-up audits.

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
