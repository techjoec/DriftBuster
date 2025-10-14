# JSON CLI run — 2025-10-17

Command:

```bash
PYTHONPATH=src python -m driftbuster.cli fixtures/config/appsettings.json
```

Output:

```
Path  Format  Variant                   Confidence  Metadata keys                                   
----  ------  ------------------------  ----------  ------------------------------------------------
.     json    structured-settings-json  0.95        bytes_sampled, catalog_format, catalog_variant,…
```

- `reasons` captured JSON structure, key/value detection, balanced delimiters,
  and structured settings hints.
- Metadata included `top_level_type`, `top_level_keys`, `settings_hint`, and
  `bytes_sampled=377`.
