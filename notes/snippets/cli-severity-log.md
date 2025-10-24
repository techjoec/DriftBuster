# CLI severity hint validation log

- Timestamp: 2025-10-24T20:20:30Z (UTC)
- Command: `python -m driftbuster.cli samples/configs`
- Notes: CLI emitted catalog severity levels and severity hints for each detection. Regex SyntaxWarnings were observed on stderr and tracked separately.

```
Path                   Format                 Variant                   Confidence  Severity  Severity hint
                                        Metadata keys
---------------------  ---------------------  ------------------------  ----------  --------  ----------------------------------
--------------------------------------  ------------------------------------------------
appsettings.base.json  json                   structured-settings-json  0.95        medium    JSON configuration files reveal fe
ature flags, API endpoints, and secre…  bytes_sampled, catalog_format, catalog_referenc…
appsettings.dev.json   json                   structured-settings-json  0.95        medium    JSON configuration files reveal fe
ature flags, API endpoints, and secre…  bytes_sampled, catalog_format, catalog_referenc…
appsettings.prod.json  json                   structured-settings-json  0.95        medium    JSON configuration files reveal fe
ature flags, API endpoints, and secre…  bytes_sampled, catalog_format, catalog_referenc…
web.release.config     structured-config-xml  web-config-transform      0.95        high      Application configuration files ex
pose secrets, connection strings, and…  bytes_sampled, catalog_format, catalog_referenc…
```
