## Multi-Server Test Fixtures

Ten simulated servers under varied configuration states used by
`tests/multi_server/test_multi_server.py`.

Layout
- `fixtures/multi-server/server01` … `server10`
- Per-server files (some may be missing by design to simulate drift):
  - `app/appsettings.json` (JSON)
  - `app/app.ini` (INI)
  - `web/web.config` (structured-config XML)
  - `msbuild/Project.csproj` (MSBuild XML)
  - `localization/Strings.resx` (Resource XML)

Notes
- Hosts use neutral domains (corp.local) and generic paths for safe sharing.
- Add or edit per-server files to simulate additional drift.
