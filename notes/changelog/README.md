# DriftBuster Component Changelogs

Each component maintains its own Markdown changelog. Keep entries ordered with
the newest release first and match headings against the release version used in
Velopack builds.

## Files

- `core.md` — backend detector and Python APIs.
- `gui.md` — Avalonia desktop front-end.
- `installer.md` — Velopack installer packaging specifics.
- `tooling.md` — scripts, dev tooling, automation helpers.
- `formats/<name>.md` — one file per formatter (e.g. `json.md`, `xml.md`).

## Entry Template

```
## <version>
- Concise bullet per change.
- Use `None` when the component has no changes for that release.
```

When a formatter is renamed, create a new file for the new name and cross-link
the previous file in its final entry.
