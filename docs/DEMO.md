## DriftBuster GUI Demo Walkthrough

This demo ships with the GUI and includes a realistic, multi-server sample set.
Use it to explore format detection, diffs, and hunts without any setup.

Prerequisites
- .NET 8 SDK installed.

Launch the GUI
- `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`

Where to find the demo data
- The app bundles samples under `Samples/` next to the application binary:
  - `Samples/Demo/` — single-project baseline/drift set (JSON, XML, RESX, MSBuild)
  - `Samples/MultiServer/` — 10 simulated servers with varied configs and drift

Try it: Diff view
1. Open Diff in the sidebar.
2. Pick files using the file buttons:
   - JSON: `Samples/MultiServer/server01/app/appsettings.json` vs `server07/app/appsettings.json`
   - INI: `Samples/MultiServer/server01/app/app.ini` vs `server02/app/app.ini`
   - XML (web.config): `Samples/MultiServer/server01/web/web.config` vs `server07/web/web.config`
   - XML (MSBuild): `Samples/MultiServer/server01/msbuild/Project.csproj` vs `server02/msbuild/Project.csproj`
   - XML (RESX): `Samples/MultiServer/server01/localization/Strings.resx` vs `server07/localization/Strings.resx`
3. Review the unified diff and the raw JSON payload if desired.

Try it: Hunt view
1. Open Hunt in the sidebar.
2. Browse to `Samples/MultiServer/` as the directory.
3. Run the hunt. You should see hits for:
   - server names (`*.corp.local`), versions (`1.2.0` etc.), install paths (`C:\\Program Files\\…` or `/opt/...`).

Optional: Single-project demo
- Under `Samples/Demo/`, explore:
  - `baseline/` vs `drift-small/` vs `drift-major/` for JSON, XML, MSBuild, and RESX.
  - Useful for quick before/after diff demonstrations.

Tips
- The sample content is neutral and safe for demos (corp.local/example paths).
- Use the file pickers’ last-used directory to move quickly between servers.
- For reporting, use the CLI helpers to render HTML/JSON outside the GUI.

