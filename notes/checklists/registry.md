# Registry smoke-test checklist

- **Fixture mix:** XML, JSON, binary sample set from `notes/checklists/core-scan.md` reused for registry verification.
- **Stub plugin:** `debug_registry_plugin.py` (kept outside the repo) exposes ``name = "debug-registry"`` and logs invocation; registered via ``register(DebugPlugin())`` before running scans.
- **Ordering check:**
  - ``get_plugins()`` returned ``("debug-registry", "xml", ...)`` once the stub plugin registered.
  - ``Detector(sort_plugins=False, plugins=get_plugins())`` respected the tuple order; the debug plugin executed first and short-circuited on targeted files.
- **Metadata spot-check:** XML plugin still populated ``bytes_sampled``/``encoding`` metadata after the stub plugin declined to match; registry ordering did not strip existing fields.
- **Error handling:** Forced a failure by pointing the detector at an unreadable file; the ``on_error`` hook received ``DetectorIOError`` while the stub plugin stayed untouched.
- **Performance note:** Additional plugin increased the mixed-fixture run by ~40 ms over 25 files (manual timing). Acceptable for diagnostics, but remove the stub before longer scans.
- **Telemetry snapshot:**

  ```bash
  PYTHONPATH=src python - <<'PY'
  import json
  from driftbuster import registry_summary

  summary = registry_summary()
  print(json.dumps(summary, indent=2))
  PY
  ```
  Store the JSON output path here (redacted if needed) so future runs can
  compare plugin orderings without rerunning the scan.
