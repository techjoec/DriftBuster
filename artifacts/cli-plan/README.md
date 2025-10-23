# CLI Plan Transcripts

These transcripts capture the current behaviour of the paused CLI entry points using
fixtures from `fixtures/` plus an ad-hoc SQLite sample. They anchor the manual
plan until packaging resumes.

## Detector scan (table)

```bash
python -m driftbuster.cli fixtures/config --glob "*.config"
```

```
Path                Format                 Variant               Confidence  Metadata keys
------------------  ---------------------  --------------------  ----------  ------------------------------------------------
App.config          structured-config-xml  app-config            0.95        bytes_sampled, catalog_format, catalog_variant,…
machine.config      structured-config-xml  machine-config        0.95        bytes_sampled, catalog_format, catalog_variant,…
web.Release.config  structured-config-xml  web-config-transform  0.95        bytes_sampled, catalog_format, catalog_variant,…
web.config          structured-config-xml  web-config            0.95        bytes_sampled, catalog_format, catalog_variant,…
```

## Detector scan (JSON)

```bash
python -m driftbuster.cli fixtures/config --glob "*.config" --json
```

```
{"confidence": 0.95, "detected": true, "format": "structured-config-xml", "metadata": {"bytes_sampled": 246, "catalog_format": "structured-config-xml", "catalog_variant": "app-config", "catalog_version": "0.0.2", "config_original_filename": "App.config", "config_role": "app", "encoding": "utf-8", "root_local_name": "configuration", "root_tag": "configuration", "xml_declaration": {"encoding": "utf-8", "version": "1.0"}}, "path": "App.config", "variant": "app-config"}
{"confidence": 0.95, "detected": true, "format": "structured-config-xml", "metadata": {"bytes_sampled": 203, "catalog_format": "structured-config-xml", "catalog_variant": "machine-config", "catalog_version": "0.0.2", "config_original_filename": "machine.config", "config_role": "machine", "encoding": "utf-8", "root_local_name": "configuration", "root_tag": "configuration", "xml_declaration": {"encoding": "utf-8", "version": "1.0"}}, "path": "machine.config", "variant": "machine-config"}
{"confidence": 0.95, "detected": true, "format": "structured-config-xml", "metadata": {"bytes_sampled": 239, "catalog_format": "structured-config-xml", "catalog_variant": "web-config-transform", "catalog_version": "0.0.2", "config_original_filename": "web.Release.config", "config_role": "web", "config_transform": true, "config_transform_primary_stage": "Release", "config_transform_scope": "web", "config_transform_stage_count": 1, "config_transform_stages": ["Release"], "encoding": "utf-8", "namespaces": {"xdt": "http://schemas.microsoft.com/XML-Document-Transform"}, "root_attributes": {"xmlns:xdt": "http://schemas.microsoft.com/XML-Document-Transform"}, "root_local_name": "configuration", "root_tag": "configuration", "xml_declaration": {"encoding": "utf-8", "version": "1.0"}}, "path": "web.Release.config", "variant": "web-config-transform"}
{"confidence": 0.95, "detected": true, "format": "structured-config-xml", "metadata": {"bytes_sampled": 240, "catalog_format": "structured-config-xml", "catalog_variant": "web-config", "catalog_version": "0.0.2", "config_original_filename": "web.config", "config_role": "web", "encoding": "utf-8", "root_local_name": "configuration", "root_tag": "configuration", "xml_declaration": {"encoding": "utf-8", "version": "1.0"}}, "path": "web.config", "variant": "web-config"}
```

## SQL snapshot export

A minimal SQLite database was created under `/tmp/cli_accounts.sqlite` to mirror the
fixture schema described in `fixtures/sql/README.md`.

```bash
python -m driftbuster.cli export-sql /tmp/cli_accounts.sqlite \
  --output-dir /tmp/cli_sql \
  --mask-column accounts.secret \
  --hash-column accounts.email \
  --hash-salt sample \
  --placeholder "[MASK]"
```

```
Exported SQL snapshot to /tmp/cli_sql/cli_accounts-sql-snapshot.json
Manifest written to /tmp/cli_sql/sql-manifest.json
```

Manifest excerpt:

```
{
  "captured_at": "<timestamp>",
  "exports": [
    {
      "dialect": "sqlite",
      "hashed_columns": {"accounts": ["email"]},
      "masked_columns": {"accounts": ["secret"]},
      "output": "cli_accounts-sql-snapshot.json",
      "row_counts": {"accounts": 1},
      "source": "/tmp/cli_accounts.sqlite",
      "tables": ["accounts"]
    }
  ],
  "options": {
    "hash_salt": "sample",
    "placeholder": "[MASK]"
  }
}
```

## Diff expectation (XML transform)

Generated with `driftbuster.reporting.diff.render_unified_diff` using
`fixtures/config/web.config` as the baseline and
`fixtures/config/web.Release.config` as the transformed payload.

```
--- web.config
+++ web.Release.config
@@ -1,2 +1,2 @@
 <?xml version="1.0" encoding="utf-8"?>
-<configuration><system.web><compilation debug="false" targetFramework="4.7.2" /></system.web><appSettings><add key="ServiceMode" value="Primary" /></appSettings></configuration>
+<configuration xmlns:ns0="http://schemas.microsoft.com/XML-Document-Transform"><appSettings><add key="ServiceMode" value="Release" ns0:Transform="Replace" /></appSettings></configuration>
```

## HTML snapshot expectation

`driftbuster.reporting.html.render_html_report` consumed the same detection matches
from `fixtures/config`. The generated document includes the dark-themed summary,
per-match sections, and a redaction summary with the default placeholder warning.
The header of the sample output reads:

```
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>CLI Sample Report</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 2rem; background: #111; color: #eee; }
```

Use this snippet to verify the renderer stays aligned when the CLI surfaces the
HTML/report exporters again.
