# DriftBuster Core Changelog

## 0.2.0
- Engine runs on .NET in `DriftBuster.Backend`: detection, catalog, diff, hunt, secrets, multi-server, profiles, scheduling, registry scan, SQL export, reporting and capture.
- Config identities stay distinct across applications sharing a file name; unreadable files are skipped and reported.
- XML canonicalisation keeps namespace prefixes; diff content type comes from detection; secret redaction always terminates.
- Notification adapters removed.

## 0.0.3
- Add `registry-live` format plugin for registry scan definition files.
- Windows Registry live scan utilities (enumerate apps, suggest roots, search).
- Offline runner support for `registry_scan` sources.
- Realtime secret masking with DPAPI/AES encryption flow.
- Scheduler orchestration for recurring profile runs.

## 0.0.2
- Core detector with sampling + metadata normalisation.
- Catalog/survey (v0.0.2) for XML/JSON/INI.
- Offline runner: file collection, secret scrubbing, manifest packaging.
- Profiles + hunt helpers.

## 0.0.0
- Bootstrap entry.
