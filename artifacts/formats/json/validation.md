# JSON detector validation snapshot

- **Detector**: `driftbuster.formats.json.plugin.JsonPlugin` (v0.0.3)
- **Sample**: 280 kB appsettings payload with inline `//` comments
- **Result**:
  - Variant: `structured-settings-json`
  - Metadata highlights:
    - `analysis_window_truncated`: `true` (200000 chars cap)
    - `has_comments`: `true`
    - `parsed_with_comment_stripping`: `true`
    - `top_level_keys`: `Logging`, `ConnectionStrings`
- **Notes**: Comment stripping retained strings containing `//` and avoided
  emitting cleaned content to disk. Only metadata was captured for audit.
