# Hunt Mode & Dynamic Content Detection

Hunt mode supplements format detection by looking for dynamic values that vary
per server, environment, or installation (hostnames, certificate thumbprints,
version numbers, paths, connection strings, endpoints, feature flags). Use it to
audit drift-prone settings and to prepare data for config templating.

## Quick Start

```sh
driftbuster hunt ./deployments/prod-web-01 \
  --exclude "**/logs/*" --exclude "**/*.bak" > hunt-results.json
```

- The default rules cover server names, certificate thumbprints, version
  numbers, installation paths, connection strings, service endpoints and
  feature flags (`gui/DriftBuster.Backend/Hunt/HuntRules.cs`). Each rule exposes
  a `token_name` that you can store inside configuration profile metadata.
- `--exclude` patterns use `*` (any run of characters, `/` included) and `?`
  (one character); every other character — brackets and backslash included — is
  literal. A file is excluded when a pattern
  matches its scan-relative path, its file name or its full path, so
  `**/logs/*` excludes everything under any `logs` directory. Matching ignores
  case on Windows only.
- `--glob` narrows the walk (default `**/*`): segments split on `/`, `**` is
  zero or more directory levels, other segments match entry names with the
  same wildcards. Symlinked directories are listed but not descended into.
- The GUI Hunt tab and `Invoke-DriftBusterHunt` run the same engine.

### Structured output

Each hit is a JSON object:

```json
{
  "excerpt": "\"Version\": \"1.0.0\",",
  "line_number": 3,
  "metadata": {
    "plan_transform": {
      "placeholder": "{{ version }}",
      "rule_name": "version-number",
      "token_name": "version",
      "value": "1.0.0"
    }
  },
  "path": "deployments/prod-web-01/app/appsettings.json",
  "relative_path": "app/appsettings.json",
  "rule": {
    "description": "Version identifiers (semver style)",
    "keywords": ["version"],
    "name": "version-number",
    "patterns": ["\\b\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?\\b"],
    "token_name": "version"
  }
}
```

- The payload retains rule metadata, excerpts, and relative paths so you can
  cross-link output with manual checklists without re-reading files.
- Feed the `token_name` values into configuration profile metadata (see
  `docs/configuration-profiles.md`) so drift reviews know which values are
  expected.
- When a rule has a `token_name`, `metadata.plan_transform` carries the detected
  value and a templated placeholder (`{{ token_name }}` by default).

### Plan transforms & placeholders

- `--placeholder-template` changes the placeholder style. The template uses
  `{token_name}` for the name and doubled braces for literal braces; the default
  `{{{{ {token_name} }}}}` renders `{{ version }}`, and `<<{token_name}>>`
  renders `<<version>>`.
- The template is a .NET composite format string with one field,
  `token_name`. `{token_name,12}` right-aligns the name in 12 characters and
  `{token_name,-12}` left-aligns it. Format specifiers (`{token_name:...}`)
  are rejected, as are unbalanced braces. Any other field (`{name}`, `{0}`,
  `{}`) fails with "placeholder_template must include {token_name}
  placeholder".
- In code, `HuntEngine.BuildPlanTransforms(hits, placeholderTemplate)`
  emits one transform per distinct token, value, file and line, pairing each
  `token_name` with the matched value.
- Feed the resulting placeholders into diff masking (`driftbuster diff
  --mask-token <value>`) or token catalogs without re-parsing hunt excerpts.

## Run profile secret scanner

Run profile captures run the secret scrubber on each source file before it is
copied. Key behaviours to keep in mind:

- Each textual file is copied through the secret filter
  (`gui/DriftBuster.Backend/Secrets/`). Matching rules replace the sensitive
  span with `[SECRET]`, record a finding, and log messages such as
  `secret candidate redacted (PasswordAssignment) from ...`. These messages
  persist in `metadata.json → secrets.messages`.
- Ignore lists are honoured at two layers: profile options may specify
  `secret_ignore_rules` / `secret_ignore_patterns`, while the GUI and
  `driftbuster profile run --secret-ignore-rule/--secret-ignore-pattern` feed
  structured `secret_scanner` overrides. Both paths normalise values into
  sorted lists before the scan begins so manifests and hashes are
  deterministic.
- When no matches trigger, files are copied byte-for-byte and `rules_loaded`
  stays `true`, proving the ruleset executed. Binary files skip redaction.
- The resulting manifest enumerates rule version, ignored entries, and every
  finding (path, rule name, line, snippet). Use this metadata as the single
  source of truth when curating approvals or verifying scrubber runs.

## Bridging hunts with profiles

Line up hunt hits with the configuration expectations stored in a detection
profile store:

```sh
driftbuster detection-profile hunt-bridge profiles.json hunt-results.json \
  --tag env:prod --tag tier:web --root deployments/prod-web-01 \
  --output hunt-profile-bridge.json
```

- `profiles.json` is a detection profile store (`{"profiles": [...]}`).
- `hunt-results.json` is the JSON array printed by `driftbuster hunt`.
- Repeat `--tag` for every activation tag required by the relevant profile.
- Use `--root` when hunt output recorded paths outside the profile layout; the
  command converts them into POSIX-style relatives before matching configs.
- The resulting JSON (`items`) lists each hunt hit, the resolved relative path,
  and any matching profile/config pairs plus expected format/variant hints.

## Custom Rules

Custom rules are available through the backend library:

```csharp
using DriftBuster.Backend.Hunt;

var dbRule = new HuntRule(
    name: "database-connection",
    description: "Connection strings referencing SQL hosts",
    tokenName: "database_server",
    keywords: ["connection", "server"],
    patterns: [@"Server=([^;]+)"]);

var result = HuntEngine.HuntPath("./deployments", [dbRule], glob: "**/*.config");
var json = HuntEngine.ToJson(result);
```

- `keywords` are case-insensitive substring gates: every keyword must occur in
  the file and at least one on the hit's line.
- `patterns` are .NET regular expressions (`System.Text.RegularExpressions`
  syntax), compiled case-insensitive, multiline and culture-invariant, used to
  flag lines for review. Each pattern runs on the linear-time `NonBacktracking`
  engine; a pattern that engine cannot run (backreferences, lookarounds, atomic
  groups, conditionals) falls back to the default backtracking engine, where one
  match attempt is capped at two seconds: an attempt that passes the cap is
  abandoned (the file is still readable and other rules still run) and
  cancellation is therefore observed within that cap.
  Secret-scanner rules and ignore patterns are built the same way (secret rules
  are case-insensitive only when their `flags` contain `i`).
- `tokenName` keeps downstream metadata predictable. Reuse the same token names
  inside configuration profile metadata and checklists.

## Workflow Suggestions

1. Run the detector first to identify formats and profile mismatches.
2. Use hunt mode on the same tree to highlight dynamic values.
3. Move confirmed entries into configuration profile metadata using the matching
   `token_name`. Keep the authoritative mapping in source control.
4. Capture approvals in `notes/checklists/hunt-profile-review.md`, recording the
   reviewer, date, excerpts, and masking decisions.
5. When capturing snapshots (`driftbuster capture run`), keep hunt results
   alongside detection output so later comparisons (`driftbuster capture
   compare`) highlight changes without re-running the scans.

### Reporting metadata

- `driftbuster report` renders detections and hunt hits together as HTML or
  JSON lines; `--mask-token` redacts values in both.
- Every detection payload exposes the canonical keys (`plugin`, `format`,
  `variant`, `confidence`, `reasons`, `metadata`). Keep hunt-derived fields
  inside the nested `metadata` map (for example, `hunts.approved_tokens`) so
  downstream tooling can merge them with detector metadata without schema
  drift.

## Token Mapping & Approval Flow

Link hunt rules to placeholder names before any token substitution work. The
default rules already expose `token_name`; record the mapping explicitly so
profiles, diffs, and templates agree on wording.

| Hunt rule | Token | Notes |
| --- | --- | --- |
| `connection-string` | `connection_string` | Connection string attribute assignments. |
| `certificate-thumbprint` | `certificate_thumbprint` | Preserve uppercase formatting for approval diffs. |
| `service-endpoint` | `service_endpoint` | Normalise scheme + host only; ignore query parameters. |

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

1. Run `driftbuster hunt` to capture candidate values as JSON.
2. Compare each hit against the relevant configuration profile entry and record
   the decision in `notes/checklists/hunt-profile-review.md`.
3. Replace the raw value with the placeholder in your working copy, then rerun
   the detector and hunt scans to confirm no new hits appear.
4. Archive the structured hunt output alongside the approval log so future
   reviews can confirm the placeholder still matches the rule definition.
