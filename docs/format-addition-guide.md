# Format Addition Standard

This guide keeps new format detectors consistent with the JSON, XML, and INI
plugins already shipping in `gui/DriftBuster.Backend/Detection/Plugins/`. Treat
it as the baseline checklist whenever you introduce a new format or refresh an
existing one.

## 1. Inventory Snapshot

Built-in plugins in registration order (`DefaultPlugins.CreateBuiltIns()`);
each class declares its `Name`, `Priority` and `Version`:

| Order | Plugin          | Class                  | Priority |
|-------|-----------------|------------------------|----------|
| 0     | `registry-export` | `RegistryExportPlugin` | 20     |
| 1     | `registry-live` | `RegistryLivePlugin`   | 30       |
| 2     | `xml`           | `XmlPlugin`            | 100      |
| 3     | `dockerfile`    | `DockerfilePlugin`     | 120      |
| 4     | `conf`          | `ConfPlugin`           | 150      |
| 5     | `hcl`           | `HclPlugin`            | 158      |
| 6     | `yaml`          | `YamlPlugin`           | 160      |
| 7     | `toml`          | `TomlPlugin`           | 165      |
| 8     | `ini`           | `IniPlugin`            | 170      |
| 9     | `json`          | `JsonPlugin`           | 200      |
| 10    | `binary-hybrid` | `BinaryHybridPlugin`   | 210      |
| 11    | `text`          | `TextPlugin`           | 1000     |

`FormatRegistry.RegistrySummary()` reports the live order, priority and version.
When you update or add a plugin, bump its `Version` property and the matrix in
`docs/format-support.md`.

## 2. Prep Work

1. **Catalog review** – Confirm the format exists in the catalog
   (`gui/DriftBuster.Backend/Detection/Catalog/DetectionCatalogData.cs`). Update
   the catalog before writing detector code so priorities, extensions, and
   variant names match the shipped metadata.
2. **Sample collection** – Gather anonymised fixtures locally. Do not commit
   them unless the change explicitly calls for new repo fixtures.
3. **Task tracking** – If the work stems from an issue, mirror the subtasks you
   plan to complete and log any follow-up gates there.

## 3. Code Layout

Follow the existing plugins as the template:

```
gui/DriftBuster.Backend/Detection/Plugins/
    <Name>Plugin.cs            # add partial files (<Name>Plugin.<Part>.cs) when it grows
```

* The class implements `IFormatPlugin` (`Name`, `Version`, `Priority`,
  `Detect(path, sample, text)`).
* Keep helpers private to the plugin class. Shared helpers belong in
  `FormatRegistry` or `Infrastructure/` so other detectors can reuse them.

Add the plugin to `DefaultPlugins.CreateBuiltIns()` at its priority position so
the default registry and `Detector` pick it up.

## 4. Detector Implementation Rules

1. **Registration** – Use a unique `Name`, a priority that places the plugin
   before any broader detector it must win against, and a semantic `Version`
   string. `FormatRegistry.Register` rejects a second plugin with the same name.
2. **Sampling discipline** – Accept `(path, sample, text)` like the existing
   detectors. `text` is already decoded when the sample looks like text and null
   otherwise. Clamp expensive heuristics to a bounded analysis window (the JSON
   plugin stops at 200,000 characters) so vendor-sized payloads do not blow past
   the detector sampling budgets.
3. **Signals** – Combine filename/extension cues with bounded structural checks.
   Extensions are hints, never gates. The JSON plugin demonstrates how to
   accumulate multiple weak signals before returning a positive match.
4. **Metadata** – Populate `DetectionMatch.Metadata` with catalog-aligned keys
   (e.g., `variant`, `top_level_type`). Reuse existing key names when extending a
   family to keep downstream tooling stable. When you introduce new
   severity/remediation hints, update the catalog and `Reporting/DetectionSummary`
   so reports surface the additional fields.
5. **Confidence** – Start with a conservative baseline (≈0.5) and add small
   increments per independent signal. Clamp the final value at `0.95`.
6. **Error handling** – Return `null` on uncertainty. Never throw for expected
   conditions (truncated sample, undecodable bytes, missing markers). When
   stripping comments or other auxiliary content for metadata extraction,
   always fall back to the original sample if sanitisation fails.
7. **Whitespace tolerances** – Structured text detectors (YAML/TOML) must emit
   indentation/spacing metadata outlining the tolerated ranges so review tools
   can flag drift. When heuristics change, update `docs/format-playbook.md` and
   the plugin tests to lock in the revised tolerances.

Review the shipped JSON and INI detectors to keep heuristics consistent with the
existing style.

## 5. Tests

1. Create `gui/DriftBuster.Backend.Tests/Detection/Plugins/<Name>PluginTests.cs`
   mirroring the JSON test layout. Include at least:
   * One positive test covering the primary variant.
   * One variant-specific test (if applicable).
   * One negative test proving the detector declines unrelated content.
2. Use small inline payloads when possible. Larger fixtures should live under
   `fixtures/<area>/` and be loaded through `RepoPaths`.
3. Keep `scripts/verify_coverage.sh` green (merged total line coverage ≥ 83%).
4. Run the plugin tests before sending the patch:
   `dotnet test gui/DriftBuster.Backend.Tests/DriftBuster.Backend.Tests.csproj --filter "FullyQualifiedName~<Name>Plugin"`.

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

- [ ] `FormatRegistry.RegistrySummary()` shows the new plugin with correct order,
      priority, and version.
- [ ] Tests covering the detector pass locally.
- [ ] Catalog entries and docs reference the same variant names as the plugin.
- [ ] Manual verification commands are noted in the relevant checklist files.
- [ ] Follow-up tasks (automation, extended fixtures) are captured in the issue
      tracker if they fall outside the current change.

Keeping each format change aligned with this guide will make detector expansion
predictable for reviewers and downstream tooling.
