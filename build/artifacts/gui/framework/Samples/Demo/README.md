## DriftBuster Demo Set

A compact, shippable sample tree showing multiple config formats (JSON, INI, XML)
and intentional drift across “baseline”, “drift-small”, and “drift-major”. Use it
for quick demos with the Python CLI, the .NET backend, or the GUI.

## Layout

```
samples/demo/
├─ baseline/
│  ├─ app/appsettings.json
│  ├─ app/app.ini
│  ├─ web/web.config
│  ├─ msbuild/Project.csproj
│  └─ localization/Strings.resx
├─ drift-small/
│  ├─ app/appsettings.json
│  └─ app/app.ini
├─ drift-major/
│  ├─ web/web.config
│  ├─ msbuild/Project.csproj
│  └─ localization/Strings.resx
└─ profile_store.json
```

- Baseline represents the “known good” snapshot.
- Drift-small tweaks feature flags, log levels, and endpoints.
- Drift-major alters web runtime settings, project targets, and resources.

## Quick Demos

- Detect formats (table):
  - `python -m driftbuster.cli samples/demo/baseline`
- Detect formats (JSON):
  - `python -m driftbuster.cli samples/demo/baseline --json`
- Compare baseline and a drift folder (Python):
  - `python - << 'PY'
from pathlib import Path
from driftbuster.reporting.diff import render_unified_diff
base = Path('samples/demo/baseline/app/appsettings.json').read_text()
chng = Path('samples/demo/drift-small/app/appsettings.json').read_text()
print(render_unified_diff(base, chng, from_label='baseline', to_label='drift-small'))
PY`
- .NET diff via PowerShell module:
  - `dotnet build gui/DriftBuster.Backend/DriftBuster.Backend.csproj`
  - `pwsh -c "Import-Module ./cli/DriftBuster.PowerShell/DriftBuster.psm1; \
               Invoke-DriftBusterDiff -Left samples/demo/baseline/app/appsettings.json \
                                    -Right samples/demo/drift-small/app/appsettings.json | ConvertTo-Json -Depth 6"`
- Profile summary (align paths using the included store):
  - `python -m driftbuster.profile_cli summary samples/demo/profile_store.json --sort-keys`

Notes
- Paths in the profile store are relative to the `samples/demo` root.
- Contents use neutral placeholders (corp.local, example.com) for safe sharing.

