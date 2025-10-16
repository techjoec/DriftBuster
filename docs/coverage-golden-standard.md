# Coverage Golden Standard

This note captures the minimum expectations and required coverage baselines.
Treat it as the canonical reference when adding new heuristics, fixtures, or
documentation.

## Coverage Policy & Measurement

- Required thresholds:
  - Python (engine/detectors/reporting): ≥ 90% line coverage on `src/`.
  - .NET (GUI + backend): ≥ 90% total line coverage (coverlet). 
  - New/changed modules must land with ≥ 90% per-file coverage unless
    platform-only code is explicitly excluded (e.g., Windows registry P/Invoke).

- Python detectors/engine/reporting use `coverage.py` with the source root
  pinned to `src/driftbuster`. Generate reports with:

  ```sh
  coverage run --source=src/driftbuster -m pytest -q
  coverage report -m
  coverage json -o coverage.json
  ```

- The .NET GUI test project (`gui/DriftBuster.Gui.Tests`) collects coverage via
  the built‑in coverlet collector using:

  ```sh
  dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj \
    --collect:"XPlat Code Coverage" \
    --results-directory artifacts/coverage-dotnet
  ```

  This produces Cobertura XML under
  `artifacts/coverage-dotnet/<run-id>/coverage.cobertura.xml`.

- To summarise both surfaces, run:

  ```sh
  python -m scripts.coverage_report
  ```

  The script prints Python percentage, .NET percentage, and the top
  under‑covered GUI classes to focus further tests.

## INI Coverage Baseline
- **Variants:** Must classify `sectioned-ini`, `sectionless-ini`, `desktop-ini`,
  `java-properties`, `env-file`/`dotenv`, `ini-json-hybrid`, and
  `unix-conf` (including `apache-conf` and `nginx-conf`).
- **Metadata:** Each detection records encoding info, comment style, key/value
  counts, sensitive key hints, directive counts, and variant-specific signals
  such as export usage or hybrid brace hints.
- **Tests:** `tests/formats/test_ini_plugin.py` keeps fixture coverage for every
  variant plus negative samples (plain text, YAML). Any new heuristics require
  matching tests before shipping.
- **Docs:** `docs/detection-types.md` and `docs/format-support.md` must reflect
  the active variants and metadata fields.

## XML Coverage Baseline
- **Variants:** Must classify `.config` layouts (`web-config`, `app-config`,
  `machine-config`, `web-config-transform`, `app-config-transform`, generic
  `config-transform`, and `custom-config-xml`), vendor roots (`nlog-config`,
  `log4net-config`, `serilog-config`), namespace-driven families
  (`app-manifest-xml`, `resource-xml`, `interface-xml`, `xslt-xml`), MSBuild
  surfaces (`msbuild-project`, `msbuild-targets`, `msbuild-props`), and the
  generic XML fallback.
- **Metadata:** Matches capture XML declarations, root tags, namespace maps,
  schema locations, config transform scope/stages, MSBuild metadata, resource
  keys, attribute hints, and DOCTYPE presence.
- **Tests:** `tests/formats/test_xml_plugin.py` exercises each variant,
  transform scope, MSBuild kind, namespace detection, and plain-text rejection.
- **Docs:** `docs/detection-types.md`, `docs/format-support.md`, and
  `notes/checklists/xml-config-verification.md` stay aligned with the detector
  behaviour and metadata names.

## Validation Checklist
- New heuristics bump the plugin `version` attribute and update the format
  support matrix.
- Running `pytest tests/formats/test_ini_plugin.py` and
  `pytest tests/formats/test_xml_plugin.py` is mandatory after changes.
- Manual checklists under `notes/checklists/` capture any supplemental runs
  (e.g., XML transform verification) before accepting the detector updates.

Required command checks (enforced locally):

- Python: `coverage report --fail-under=90`
- .NET: `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total`

## Test Coverage Map

Use the following reference when modifying tests to keep every variant and
metadata path covered:

| Surface | Variant / Feature | Test Function |
|---------|-------------------|---------------|
| INI | Sectioned layout | `test_ini_plugin_detects_sections_and_keys` |
| INI | Sectionless colon pairs | `test_ini_plugin_classifies_sectionless_ini_variant` |
| INI | Desktop profile | `test_ini_plugin_detects_desktop_ini_variant` |
| INI | Dotenv/export handling | `test_ini_plugin_classifies_env_files` |
| INI | Java properties | `test_ini_plugin_preserves_java_properties_classification` |
| INI | Unix conf (Apache) | `test_ini_plugin_classifies_unix_conf_variants` |
| INI | Unix conf (nginx) | `test_ini_plugin_classifies_nginx_conf_variant` |
| INI | INI/JSON hybrid | `test_ini_plugin_detects_ini_json_hybrids` / `test_ini_plugin_detects_inline_closing_json_hybrid` |
| INI | Directive-only rejection | `test_ini_plugin_rejects_plain_text` / `test_ini_plugin_rejects_yaml_with_colons` |
| INI | Encoding & sensitive keys | `test_ini_plugin_records_bom_and_sensitive_hints` / `test_ini_plugin_reports_latin1_encoding` |
| XML | Web/app/machine configs | `test_xml_plugin_detects_framework_config`, `test_xml_plugin_detects_app_config_variant`, `test_xml_plugin_detects_machine_config_variant` |
| XML | Config transforms | `test_xml_plugin_detects_config_transform_scope`, `test_xml_plugin_records_multi_stage_transform_metadata`, `test_xml_plugin_detects_app_config_transform_variant`, `test_xml_plugin_detects_generic_config_transform_variant` |
| XML | Custom/generic configs | `test_xml_plugin_classifies_generic_web_or_app_config`, `test_xml_plugin_identifies_custom_config_xml` |
| XML | Namespace-driven variants | `test_xml_plugin_detects_manifest_variant`, `test_xml_plugin_detects_resx_variant_via_namespace`, `test_xml_plugin_detects_xaml_variant_via_namespace`, `test_xml_plugin_detects_xslt_variant` |
| XML | Vendor .config roots | `test_xml_plugin_detects_vendor_config_roots` |
| XML | Attribute/schema metadata | `test_xml_plugin_canonicalises_root_attributes`, `test_xml_plugin_extracts_schema_locations`, `test_xml_plugin_collects_attribute_hints` |
| XML | MSBuild surfaces | `test_xml_plugin_supports_targets_extension`, `test_xml_plugin_detects_msbuild_props_variant`, `test_xml_plugin_detects_msbuild_project_metadata` |
| XML | Generic fallback & rejection | `test_xml_plugin_detects_generic_xml_variant`, `test_xml_plugin_rejects_plain_text` |

## Registry Live Coverage Baseline
- **Format:** `registry-live` detects JSON/YAML manifests with a top-level
  `registry_scan` object describing a Windows Registry hunt (token, keywords,
  patterns, limits).
- **Tests:** `tests/formats/test_registry_live_plugin.py` must cover JSON and
  YAML matches, metadata extraction, and negative cases.
- **Docs:** `docs/detection-types.md` and `docs/format-support.md` include the
  catalog entry; `docs/registry.md` documents API and offline runner usage.

### Test Coverage Map (extended)
| Surface | Variant / Feature | Test Function |
|---------|-------------------|---------------|
| Registry | Live scan definition | `test_detects_json_manifest_with_metadata`, `test_detects_yaml_manifest_heuristically` |

When you add a new variant or metadata field, extend this table and introduce a
matching test so the map remains exhaustive.
