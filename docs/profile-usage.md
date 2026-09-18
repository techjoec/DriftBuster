# Profile Usage Quick Start

DriftBuster has two kinds of profile:

- **Run profiles** say what to collect (sources, baseline, options, secret
  scanner settings). The GUI Profiles tab, `driftbuster profile` and the
  PowerShell run profile cmdlets manage them, and the offline runner consumes
  them.
- **Detection profiles** describe the configuration files you expect to see and
  activate by tags. `driftbuster detection-profile` summarises, diffs and
  bridges them with hunt output. See `docs/configuration-profiles.md` for the
  data model.

## 1. Save and run a run profile

```sh
driftbuster profile create --name prod-web \
  --source deployments/prod-web-01 --source deployments/prod-web-02 \
  --baseline deployments/prod-web-01
driftbuster profile list
driftbuster profile show prod-web
driftbuster profile run --name prod-web
```

- Profiles are stored as `Profiles/<name>/profile.json` under `--base-dir`
  (the current directory by default); the GUI keeps its profiles under the data root.
- `--option key=value` adds custom options; `--secret-ignore-rule` and
  `--secret-ignore-pattern` tune the secret scanner.
- `profile run --profile <file.json> --save` runs a profile file and stores it.

## 2. Define a detection profile store

Detection profile stores are JSON:

```json
{
  "profiles": [
    {
      "name": "prod-web",
      "tags": ["env:prod", "tier:web"],
      "configs": [
        {"id": "web-config", "path": "web/web.config", "expected_format": "structured-config-xml"},
        {"id": "appsettings", "path": "app/appsettings.json", "expected_format": "json"}
      ]
    }
  ]
}
```

- Use `id` for stable diffs.
- Store additional tags in `tags` or helper fields such as `application`.

## 3. Summarise and diff

```sh
driftbuster detection-profile summary profiles.json --output baseline-summary.json
# ... edit the store ...
driftbuster detection-profile summary profiles.json --output current-summary.json
driftbuster detection-profile diff baseline-summary.json current-summary.json
```

The summary lists profile and config counts plus config IDs per profile; the
diff reports `added_profiles`, `removed_profiles` and `changed_profiles` with
added and removed config IDs.

## 4. Bridge hunts with profiles

```sh
driftbuster hunt deployments/prod-web-01 > hunt-results.json
driftbuster detection-profile hunt-bridge profiles.json hunt-results.json \
  --tag env:prod --tag tier:web --root deployments/prod-web-01
```

Each hunt hit is listed with the profile configs that apply to its path.

## Next Steps

- Represent dynamic values alongside profiles with hunt metadata (see
  `docs/hunt-mode.md`).
- Explore the data model in `docs/configuration-profiles.md`.
