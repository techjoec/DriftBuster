## Multi-Server Sampling Demo

This walkthrough shows how to scan 10 servers in varied states, hunt for
dynamic values, and compare drift against a baseline — all locally with the
repo’s sample data.

Sample layout
- Root: `samples/multi-server/`
- Servers: `server01` … `server10`
- Configs: `app/appsettings.json` per server (with minor variations)

Goals
- See format detection at scale on a small tree.
- Surface common drift (log levels, feature flags, endpoints, versions).
- Highlight dynamic values with hunt rules (hostnames, versions, paths).
- Produce an HTML report for sharing in demos.

Step 1 — Detect formats (and optional registry definitions)
- Table output (all servers):
  - `python -m driftbuster.cli samples/multi-server`
- JSON (for programmatic inspection):
  - `python -m driftbuster.cli samples/multi-server --json`
- If you include a small `registry_scan` manifest under `samples/multi-server`,
  the detector will classify it as `registry-live` so the GUI can surface it;
  use the offline runner on Windows to execute those scans.

Step 2 — Hunt for dynamic values
- Use the built-in rules (server names, versions, install paths):
  - `python - << 'PY'
from pathlib import Path
from driftbuster.hunt import default_rules, hunt_path
hits = hunt_path(Path('samples/multi-server'), rules=default_rules(), glob='**/*.json', return_json=True)
print('Total hits:', len(hits))
print('Examples:')
for h in hits[:12]:
    print(h['relative_path'], '->', h['rule']['name'], '::', h['excerpt'])
PY`

Step 3 — Diff against a baseline
- Quick diff script (server01 baseline by default):
  - `python -m scripts.demo_multi_server --root samples/multi-server --baseline server01 --html artifacts/demo-multi-server.html`
- Outputs:
  - Text summary to stdout with server count, diff count, hunt hits.
  - `artifacts/demo-multi-server.html` with unified diffs and hunt highlights.

Step 4 — Exercise the GUI/backend orchestrator
- The Avalonia GUI now shells out to `python -m driftbuster.multi_server` for real
  multi-host runs. You can invoke the same pipeline from the .NET tests or from
  a REPL by instantiating `DriftbusterBackend` and calling
  `RunServerScansAsync(...)` with plans that point at `samples/multi-server`.
- Results are cached per `(host, config, root)` signature under
  `artifacts/cache/diffs/`. Delete that directory to force a cold run when
  you change sample content.
- Progress messages stream over stdout; when troubleshooting, set
  `PYTHONUNBUFFERED=0` and watch the console for per-host status updates.
- Review the activity timeline in the Multi-server tab to audit root changes,
  run outcomes, and exports. Use the copy action beside any entry to grab a
  shareable summary when triaging drift.

Tips
- Change the baseline: `--baseline server05`.
- Limit diff scope by editing the script to include other files (e.g., `.config`).
- Redaction: pass `mask_tokens` to `render_html_report` if adding sensitive tokens.
