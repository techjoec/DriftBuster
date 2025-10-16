Windows Registry Live Scans
===========================

Overview
--------
- Enumerates installed applications from the Uninstall registry keys.
- Guesses likely registry roots for a selected app (HKCU/HKLM software trees and Wow6432Node).
- Searches registry values under those roots using user‑provided keywords and/or regex patterns.

Usage (Python API)
------------------
- List apps: `from driftbuster.registry import enumerate_installed_apps`
- Pick targets: `find_app_registry_roots("My App", installed=apps)`
- Search: `search_registry(roots, SearchSpec(keywords=("server",), patterns=(re.compile("https://"),)))`

Notes
-----
- Read‑only; writes are not supported.
- Windows only. On non‑Windows platforms, construct a custom backend or skip.
- Traversal enforces limits: max depth, max hits, and a time budget.

Offline Runner
--------------
- The offline collection tool can execute registry scans alongside file copies.
- Add a `registry_scan` source to `profile.sources` in your JSON config:

```
{
  "profile": {
    "name": "collect-config-and-registry",
    "sources": [
      { "path": "C:/ProgramData/VendorA/AppA" },
      {
        "alias": "vendorA-registry",
        "registry_scan": {
          "token": "VendorA AppA",
          "keywords": ["server", "api"],
          "patterns": ["https://", "api\\.internal\\.local"],
          "max_depth": 12,
          "max_hits": 200,
          "time_budget_s": 10.0
        }
      }
    ]
  }
}
```

- Results are written to `data/<alias>/registry_scan.json` and summarised in the manifest.
- Non‑Windows hosts skip registry sources, recording a clear reason in the manifest/logs.

GUI Focus (\.NET 8)
-------------------
- The GUI consumes the same packaged output and surfaces registry scan results
  beside file-based findings. It remains the primary workflow; the Python
  engine powers the collection and detection.
