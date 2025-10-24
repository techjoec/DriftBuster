# Format Addition Standard

This guide keeps new format detectors consistent with the JSON, XML, and INI
plugins already shipping in `driftbuster.formats`. Treat it as the baseline
checklist whenever you introduce a new format or refresh an existing one.

## 1. Inventory Snapshot

Current registry order example (`driftbuster.formats.registry_summary()`; actual order may change as plugins evolve):

| Order | Plugin         | Module                                                        | Priority | Version |
|-------|----------------|---------------------------------------------------------------|----------|---------|
| 0     | `registry-live`| `driftbuster.formats.registry_live.plugin.RegistryLivePlugin`| 30       | 0.0.1   |
| 1     | `xml`          | `driftbuster.formats.xml.plugin.XmlPlugin`                    | 100      | 0.0.4   |
| 2     | `dockerfile`   | `driftbuster.formats.dockerfile.plugin.DockerfilePlugin`      | 120      | 0.0.1   |
| 3     | `conf`         | `driftbuster.formats.conf.plugin.ConfPlugin`                  | 150      | 0.0.1   |
| 4     | `hcl`          | `driftbuster.formats.hcl.plugin.HclPlugin`                    | 158      | 0.0.1   |
| 5     | `yaml`         | `driftbuster.formats.yaml.plugin.YamlPlugin`                  | 160      | 0.0.1   |
| 6     | `toml`         | `driftbuster.formats.toml.plugin.TomlPlugin`                  | 165      | 0.0.1   |
| 7     | `ini`          | `driftbuster.formats.ini.plugin.IniPlugin`                    | 170      | 0.0.1   |
| 8     | `json`         | `driftbuster.formats.json.plugin.JsonPlugin`                  | 200      | 0.0.3   |
| 9     | `text`         | `driftbuster.formats.text.plugin.TextPlugin`                  | 1000     | 0.0.1   |

Use the same structure for new plugins so the registry report stays predictable.
When you update or add a plugin, bump only its entry in `versions.json` and run
`python scripts/sync_versions.py` so the version string propagates to the docs
and manifests automatically.

## 2. Prep Work

1. **Catalog review** – Confirm the format exists in
   `src/driftbuster/catalog.py` (`DETECTION_CATALOG` and `FORMAT_SURVEY`). Update
   the dataclasses before writing detector code so priorities, extensions, and
   variant names match the shipped metadata.
2. **Sample collection** – Gather anonymised fixtures locally. Do not commit
   them unless the roadmap explicitly calls for new repo fixtures.
3. **Task tracking** – If the work stems from a `CLOUDTASKS.md` item, mirror the
   subtasks you plan to complete and log any follow-up gates there.

## 3. Package Layout

Follow the XML module as the template:

```
src/driftbuster/formats/
    <format_slug>/
        __init__.py
        plugin.py
```

* `__init__.py` should import and re-export the plugin class (`from .plugin
  import <PluginClass>`).
* `plugin.py` must expose a dataclass (or simple class) implementing the
  `FormatPlugin` protocol defined in `src/driftbuster/formats/format_registry.py`.
* Keep module-level helpers private to the format package. Shared helpers belong
  in `registry.py` so other detectors can reuse them.

Once the package exists, import it in `src/driftbuster/formats/__init__.py` so
registration happens on module import alongside the built-ins.

## 4. Detector Implementation Rules

1. **Registration** – Call `register(<PluginClass>())` exactly once at import
   time (`register(Plugin())`). Use a unique `name`, monotonic `priority`, and a
   semantic `version` string.
2. **Sampling discipline** – Accept `(path, sample, text)` like the existing
   detectors. Derive `text` via `decode_text` only when you need it; the caller
   already performs best-effort decoding for you. Clamp expensive heuristics to
   a bounded analysis window (the JSON plugin stops at 200 kB) so vendor-sized
   payloads do not blow past the detector sampling budgets.
3. **Signals** – Combine filename/extension cues with bounded structural checks.
   The JSON plugin demonstrates how to accumulate multiple weak signals before
   returning a positive match.
4. **Metadata** – Populate `DetectionMatch.metadata` with catalog-aligned keys
   (e.g., `variant`, `top_level_type`). Reuse existing key names when extending a
   family to keep downstream tooling stable.
5. **Confidence** – Start with a conservative baseline (≈0.5) and add small
   increments per independent signal. Clamp the final value at `0.95`.
6. **Error handling** – Return `None` on uncertainty. Never raise for expected
   conditions (truncated sample, undecodable bytes, missing markers). When
   stripping comments or other auxiliary content for metadata extraction,
   always fall back to the original sample if sanitisation fails.

Review the shipped JSON and INI detectors to keep heuristics consistent with the
existing style.

## 5. Tests

1. Create `tests/formats/test_<format>_plugin.py` mirroring the JSON test
   layout. Include at least:
   * One positive test covering the primary variant.
   * One variant-specific test (if applicable).
   * One negative test proving the detector declines unrelated content.
2. Use small inline payloads when possible. Larger fixtures should live under
   `fixtures/<area>/` and be loaded during the test.
3. Maintain a minimal coverage baseline of 90%+ for the new plugin file(s).
   - Quick check:
     - `coverage run --source=src/driftbuster -m pytest -q tests/formats/test_<format>_plugin.py`
     - `coverage report --fail-under=90`
   - Add edge-case tests (negative samples, variant boundaries) until the
     detector stays above the threshold.
4. Run `pytest tests/formats/test_<format>_plugin.py` before sending the patch.

## 6. Documentation and Notes

1. Update `docs/detection-types.md` with the new catalog entry, variant notes,
   and metadata guidance.
2. Add any detector-specific workflow notes to `docs/format-playbook.md` or a
   dedicated appendix if the heuristics introduce new manual review steps.
3. Refresh `notes/checklists/` entries referenced by the playbook (registry
   snapshot, manual diff log, hunt review) after running the detector locally.
4. If the detector introduces a definition format (e.g., registry live scan
   manifests), update `docs/registry.md` with usage guidance.

## 7. Validation Checklist

Before marking the work complete:

- [ ] `registry_summary()` shows the new plugin with correct order, priority,
      and version.
- [ ] Tests covering the detector pass locally.
- [ ] Catalog entries and docs reference the same variant names as the plugin.
- [ ] Manual verification commands are noted in the relevant checklist files.
- [ ] Follow-up tasks (automation, extended fixtures) are captured in
      `CLOUDTASKS.md` (see relevant area) if they fall outside the current change.

Keeping each format change aligned with this guide will make detector expansion
predictable for reviewers and downstream tooling.
