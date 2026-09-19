# XML Config Diff Snippets

Use this file to track sanitised before/after metadata snapshots for XML config
runs. Paste JSON fragments only—no raw secrets or proprietary content.

## Web-Config

```json
{
  "logged_at": "2025-10-14T11:24:33Z",
  "variant": "web-config",
  "config_role": "web",
  "config_original_filename": "web.config",
  "config_ingested_at": "2025-10-14T11:10:05Z",
  "config_sanitised_at": "2025-10-14T11:18:12Z",
  "config_verified_at": "2025-10-14T11:24:12Z",
  "catalog_format": "structured-config-xml",
  "catalog_variant": "web-config",
  "catalog_version": "0.0.4",
  "bytes_sampled": 240,
  "encoding": "utf-8",
  "xml_declaration": {
    "encoding": "utf-8",
    "version": "1.0"
  },
  "root_local_name": "configuration",
  "root_tag": "configuration"
}
```

## App-Config

```json
{
  "logged_at": "2025-10-14T11:24:55Z",
  "variant": "app-config",
  "config_role": "app",
  "config_original_filename": "App.config",
  "config_ingested_at": "2025-10-14T11:09:41Z",
  "config_sanitised_at": "2025-10-14T11:17:46Z",
  "config_verified_at": "2025-10-14T11:23:58Z",
  "catalog_format": "structured-config-xml",
  "catalog_variant": "app-config",
  "catalog_version": "0.0.4",
  "bytes_sampled": 246,
  "encoding": "utf-8",
  "xml_declaration": {
    "encoding": "utf-8",
    "version": "1.0"
  },
  "root_local_name": "configuration",
  "root_tag": "configuration"
}
```

## Machine-Config

```json
{
  "logged_at": "2025-10-14T11:25:12Z",
  "variant": "machine-config",
  "config_role": "machine",
  "config_original_filename": "machine.config",
  "config_ingested_at": "2025-10-14T11:09:59Z",
  "config_sanitised_at": "2025-10-14T11:17:59Z",
  "config_verified_at": "2025-10-14T11:24:40Z",
  "catalog_format": "structured-config-xml",
  "catalog_variant": "machine-config",
  "catalog_version": "0.0.4",
  "bytes_sampled": 203,
  "encoding": "utf-8",
  "xml_declaration": {
    "encoding": "utf-8",
    "version": "1.0"
  },
  "root_local_name": "configuration",
  "root_tag": "configuration"
}
```

## Web-Config-Transform

```json
{
  "logged_at": "2025-10-14T11:25:37Z",
  "variant": "web-config-transform",
  "config_role": "web",
  "config_transform": true,
  "config_transform_scope": "web",
  "config_original_filename": "web.Release.config",
  "config_ingested_at": "2025-10-14T11:10:28Z",
  "config_sanitised_at": "2025-10-14T11:18:34Z",
  "config_verified_at": "2025-10-14T11:25:21Z",
  "catalog_format": "structured-config-xml",
  "catalog_variant": "web-config-transform",
  "catalog_version": "0.0.4",
  "bytes_sampled": 239,
  "encoding": "utf-8",
  "namespaces": {
    "xdt": "http://schemas.microsoft.com/XML-Document-Transform"
  },
  "root_attributes": {
    "xmlns:xdt": "http://schemas.microsoft.com/XML-Document-Transform"
  },
  "xml_declaration": {
    "encoding": "utf-8",
    "version": "1.0"
  },
  "root_local_name": "configuration",
  "root_tag": "configuration"
}
```

Update each block with timestamped before/after notes as manual runs occur.

Last refresh: 2025-10-13 (structured-config metadata verified via Detector scan on temporary samples).

## Diff plan blueprint

```bash
driftbuster diff fixtures/config/web.config fixtures/config/web.Release.config \
  --content-type xml --mask-token example.com --context-lines 5
```

The output is the unified diff after XML canonicalisation and masking, ending with a
`Summary: added=… removed=… changed=…` line. Add `--output-dir <dir>` to write the patch.
