## Multi-Server Orchestration Walkthrough

The multi-server flow configures up to six hosts, shares progress with live toasts, and produces catalog/drilldown exports without leaving the GUI. This guide walks through the end-to-end experience using the bundled sample data plus equivalent CLI commands so you can automate the same orchestration.

### Scenario Overview

- Demo dataset: `samples/multi-server/`
- Hosts: `server01` … `server06` (feel free to add more copies)
- Roots: each server folder acts as an independent root
- Baseline: `server01` ships with the expected config snapshot

### 1. Configure Hosts in the GUI

Open the desktop preview (`dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`) and switch to the **Multi-server** tab. Each slot can point at a different root, baseline, and scope.

```
+-------------------------------------------------------------------+
| Multi-server Tab                                                  |
|                                                                   |
| [1] Host cards      [2] Scope chips         [3] Roots list        |
|     ┌─────────┐         ┌───────────────┐      ┌───────────────┐ |
|     │ Label   │         │ Baseline ▶︎  │      │ C:\programs  │ |
|     │ Toggle  │         │ Custom ▲     │      │ D:\configs    │ |
|     └─────────┘         └───────────────┘      └───────────────┘ |
|                                                                   |
| [4] Session cache toggle     [5] Guidance footer + Run buttons   |
+-------------------------------------------------------------------+
```

Callout notes:
- **[1] Host cards** – enable a slot, edit the label, and pick a baseline preference for reruns.
- **[2] Scope chips** – switch between predefined scopes (e.g., `Program Files`, `AppData`) or stay with custom roots.
- **[3] Roots list** – add, remove, or reorder roots. Inline badges show `pending`, `ok`, or `error` as validation completes, and you can drag any host card to reorder the execution priority; baseline preference updates automatically.
- **[4] Session cache toggle** – opt-in to save labels, scopes, root order, catalog filters, timeline state, and the last active tab into your DriftBuster data root (for example `%LOCALAPPDATA%/DriftBuster/sessions/multi-server.json`, `$XDG_DATA_HOME/DriftBuster/sessions/...`) when you click **Save session**.
- **[5] Guidance footer** – explains why a scan is blocked (missing roots, failed validation) and surfaces **Run all** and **Run missing only** actions.

### 2. Launch and Monitor a Scan

- Click **Run all** once every active host shows a green root badge. The inline **View drilldown** button in the execution summary lets you jump straight from a host status row to the latest drilldown entry for that host.
- Progress appears next to each host. Statuses cycle through `queued`, `running`, `succeeded`, `failed`, or `skipped`.
- Toasts summarize completed runs, permission warnings, and retry hints. Open the activity timeline on the right to view a durable log of root changes, exports, and reruns.
- Successful results are cached by `(host, root, config)` so rerunning only missing hosts is instant; the view reuses existing catalog entries while new work finishes.

### 3. Explore Results

1. **Results catalog** – shows coverage counts, drift totals, and color-tagged severity per config. Use the filter tray for coverage/severity/type, or search by config name.
2. **Drilldown** – select a catalog entry to open side-by-side and unified diffs. Toggle servers on/off from the checklist to compare subsets. Export HTML/JSON snapshots with the inline buttons.
3. **Selective reruns** – the catalog’s **Re-scan affected servers** button issues targeted plans so you can validate fresh drift without losing context.

### 3a. Persist and Restore a Session

1. Click **Save session** once every host has completed at least one run. The snapshot writes to `%LOCALAPPDATA%/DriftBuster/sessions/multi-server.json` (or `$XDG_DATA_HOME/DriftBuster/sessions/...` on non-Windows hosts) using the awaitable cache service introduced in `SessionCacheService`.
2. Close the GUI and relaunch it with `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`. The multi-server tab will repopulate host labels, scopes, root ordering, baseline preferences, catalog filters, the last selected drilldown host, and the active timeline filter.
3. Validate that Inter-font dependent controls (catalog headers, guidance footer text) render correctly. The headless bootstrapper now preloads `fonts:SystemFonts` so even Release builds hydrate the alias dictionary before multi-server views instantiate.
4. Trigger a rerun from the restored session. Cached hosts immediately report **succeeded** while the activity timeline records a **Loaded saved session** success entry ("Restored _n_ servers.") before fresh run telemetry lands for any hosts that require new work.

### 4. CLI Parity

The GUI invokes `python -m driftbuster.multi_server` with a JSON request over stdin. Try the same flow from the repo root:

```sh
python -m driftbuster.multi_server <<'JSON'
{
  "plans": [
    {
      "host_id": "server01",
      "label": "Baseline",
      "scope": "custom_roots",
      "roots": ["samples/multi-server/server01"],
      "baseline": {"is_preferred": true, "priority": 1},
      "export": {"include_catalog": true, "include_drilldown": true}
    },
    {
      "host_id": "server02",
      "label": "Drift sample",
      "scope": "custom_roots",
      "roots": ["samples/multi-server/server02"]
    }
  ]
}
JSON
```

- Progress messages stream as JSON objects with `type: "progress"`. Pipe the output through `jq` to watch updates: `python -m driftbuster.multi_server <<<"…" | jq`.
- Omit `cache_dir` to let the CLI use the same data root as the GUI. Set `DRIFTBUSTER_DATA_ROOT=/custom/path` to override where cached diffs and sessions are stored.
- To rerun only missing hosts, send a smaller plan containing the failed host IDs; cached hosts can be left out.
- Export helpers write HTML/JSON to `artifacts/exports/<config>-<timestamp>.{html,json}`. Combine with `python -m scripts.coverage_report` to summarise multi-host metrics after a batch.

### 5. Optional Single-Host CLI Recipes

- Detect and classify formats: `python -m driftbuster.cli samples/multi-server/server02` (add `--json` for machine output).
- Generate a diff report with a chosen baseline: `python -m scripts.demo_multi_server --root samples/multi-server --baseline server01 --html artifacts/demo-multi-server.html`.

### Troubleshooting

- **Root validation errors** – Hover the badge for the failing path. Fix permissions or edit the root, then click **Retry validation**; the toast system also reports the OS error text.
- **Permission denied hosts** – The activity timeline logs the failure with the host ID. Use **Run missing only** after adjusting credentials or running the CLI with elevated access.
- **Missing hosts** – Disable unused slots to silence warnings. If a host disconnects mid-run, the toast will point to **Re-scan affected servers** while leaving successful outputs intact.
- **Cache clean-up** – Remove the cached diffs or session file from your DriftBuster data root (e.g. `%LOCALAPPDATA%/DriftBuster/cache/diffs/`, `%LOCALAPPDATA%/DriftBuster/sessions/multi-server.json`) to start fresh. The GUI will prompt before writing a new snapshot.

### Next Steps

- Capture annotated exports from the drilldown for handoff using the HTML/JSON buttons.
- Check in a sanitized JSON plan under `artifacts/plans/` so you can rerun the same batch later.
- Keep an eye on the activity feed for upcoming toast enhancements that annotate remediation steps directly inside the feed.
