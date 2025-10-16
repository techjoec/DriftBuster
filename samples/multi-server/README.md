## Multi-Server Sampling Demo

Ten simulated servers under varied configuration states to demonstrate scanning,
drift comparisons, and hunt hits at small scale.

Layout
- `samples/multi-server/server01` … `server10`
- Per-server files (some may be missing by design to simulate drift):
  - `app/appsettings.json` (JSON)
  - `app/app.ini` (INI)
  - `web/web.config` (structured-config XML)
  - `msbuild/Project.csproj` (MSBuild XML)
  - `localization/Strings.resx` (Resource XML)

Quick tour
- Detect formats for all servers (table):
  - `python -m driftbuster.cli samples/multi-server`
- Hunt likely dynamic values across all servers:
  - `python - << 'PY'
from pathlib import Path
from driftbuster.hunt import default_rules, hunt_path
hits = hunt_path(Path('samples/multi-server'), rules=default_rules(), glob='**/*.json', return_json=True)
print(len(hits), 'hunt hits')
for h in hits[:10]: print(h['relative_path'], '->', h['rule']['name'], '::', h['excerpt'])
PY`
- Diff appsettings vs. baseline (server01) and render a report:
  - `python -m scripts.demo_multi_server --root samples/multi-server --baseline server01 --html artifacts/demo-multi-server.html`

Day 0 baseline demo
- Build a proposed baseline snapshot + profile store from these samples in one command:
  - `./scripts/demo_day0_baseline.sh`
  - See outputs under `artifacts/day0-demo/` and open `day0-baseline.html` to review diffs.

Notes
- Hosts use neutral domains (corp.local) and generic paths for safe sharing.
- Add or edit per-server files to simulate additional drift.
