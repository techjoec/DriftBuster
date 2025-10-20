# Extensions-As-Hints Experiment and Hardening

Objective: Ensure file extensions are treated as hints in detection confidence, not gates for detection decisions. Validate resilience when files are mislabeled with incorrect extensions.

Dataset
- Clean set: a representative configuration corpus (≈200 files)
- Messy set: same files with random wrong extensions appended

Commands
```bash
# Clean (256 KiB)
python -m driftbuster.cli <CLEAN_DIR> --json --sample-size 262144 > driftbuster_clean_rescan_256k.jsonl

# Messy (256 KiB)
python -m driftbuster.cli <MESSY_DIR> --json --sample-size 262144 > driftbuster_exts_mess_scan_256k.jsonl
```

Initial Observation (before hardening)
- Messy vs clean (by filename stem): detection improved from 190/203 → 191/203; average confidence ~0.783 → ~0.783.
- Root cause for the lone improvement: YAML plugin permitted extension-only detection (e.g., a `.yaml` extension on a Java-style properties file).

Changes Implemented
- JSON: Extension is a confidence hint only. Non-JSON extensions require content signals (key marker or parse success) to detect.
- TOML: Extension removed from gating; used as a small confidence boost only.
- INI: Do not let INI-like extensions relax gating; require at least one key/value or other substantive signal (dotenv/export/preferences exemptions remain).
- YAML: Do not allow extension-only detection. Require at least one structural YAML signal (key:, list marker, indentation, or doc start) even if the extension is `.yaml`/`.yml`. Keep the special heavily-commented `minion` case.

Post-Hardening Results
- Messy vs messy (pre vs post-fix): total detected 191 → 190. The previously “improved” detection was removed (as desired).
- Clean vs messy (by stem) after fix:
  - Clean detected: 190/203 (93.60%)
  - Messy detected: 190/203 (93.60%)
  - Avg confidence: clean ~0.7833 vs messy ~0.7826 (effectively unchanged)

Case Study (generalized)
- Comment-heavy, dotted-property style config mislabeled as `.yaml` was previously detected based on extension alone.
- After gating change, extension-only no longer triggers detection; structural YAML is required.

Conclusion
- Extensions are now strictly hints. Mislabeling no longer increases detections.
- Coverage and confidence remain stable on clean data; mislabeled data no longer causes artificial increases.

Artifacts
- Saved locally next to the commands above (filenames shown in examples).
