# Sample Cap Evaluation: 128 KiB vs 256 KiB

Scope: Blind scan of a representative configuration corpus using the Python CLI. Objective was to see if increasing the sampling cap from the default 128 KiB to 256 KiB materially improves detection coverage or confidence.

Dataset
- Files: ~200 (mixed configs; some large files present)

Commands Used
```bash
# Baseline (default 128 KiB)
python -m driftbuster.cli <DATASET_DIR> --json > driftbuster_blind_scan.jsonl

# Increased sample cap (256 KiB)
python -m driftbuster.cli <DATASET_DIR> --json --sample-size 262144 > driftbuster_blind_scan_256k.jsonl
```

Key Metrics
- Baseline (128 KiB)
  - Detected: 183 / 204 (89.71%)
  - Avg confidence: 0.7837
  - Truncation rate: 4.37%
  - Composite score: 84.73 / 100
- Increased cap (256 KiB)
  - Detected: 183 / 204 (89.71%)
  - Avg confidence: 0.7837
  - Truncation rate: 1.64%
  - Composite score: 85.01 / 100

Delta (256 KiB vs 128 KiB)
- Detection rate: unchanged (89.71% → 89.71%)
- Average confidence: unchanged (0.7837 → 0.7837)
- Newly detected files: 0
- Newly undetected files: 0
- Confidence shifts > ±0.05: 0 improved, 0 dropped
- Truncation indicator decreased by ~2.7 percentage points (fewer large files flagged as truncated)

Conclusion
- Increasing the sampling cap to 256 KiB showed no material benefit for detection coverage or confidence on this dataset.
- The only observed change was a lower `sample_truncated` rate in metadata for large files.
- Recommendation: keep the default 128 KiB sampling cap for general use to minimize I/O. Consider a higher cap only when investigating specific large files where truncation metadata is undesirable or when format-specific heuristics are known to require deeper samples.

Artifacts
- Saved locally next to the commands above (filenames shown in examples).
