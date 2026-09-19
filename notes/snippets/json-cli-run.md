# JSON CLI run

Command:

```bash
driftbuster scan fixtures/config/appsettings.json
```

Output:

```
Path  Format  Variant                   Confidence  Severity  Severity hint                                                             Metadata keys
----  ------  ------------------------  ----------  --------  ------------------------------------------------------------------------  ------------------------------------------------
.     json    structured-settings-json  0.95        medium    JSON configuration files reveal feature flags, API endpoints, and secre…  bytes_sampled, catalog_format, catalog_referenc…
```

- Metadata included `top_level_type`, `top_level_keys`, `settings_hint`, and
  `bytes_sampled=377`.
