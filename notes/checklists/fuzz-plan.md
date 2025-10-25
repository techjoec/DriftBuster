# Configsample fuzz plan

- **Entry point:** `python -m scripts.score_configsamples --fuzz-output <dir> --fuzz-count 3`.
- **Sampling guardrail:** keep `--max-total-sample-bytes` at or below the default 16 MiB
  when refreshing corpora so Detector guardrails continue to trip before the
  library exhausts memory.
- **Variant size cap:** `--fuzz-max-bytes` defaults to 4096 bytes per sample and
  should only be raised when downstream fixtures explicitly require larger
  payloads. Smaller caps speed up mutation refreshes and keep coverage jobs
  deterministic.
- **Determinism:** Supply `--fuzz-seed <int>` to reproduce exact variant payloads.
  Seeds should be logged below whenever we cut a new catalogue.

## Current catalogue

| Seed | Source bucket | Variants | Mutation mix | Notes |
| ---- | ------------- | -------- | ------------- | ----- |
| 1234 | `configsamples/library/by-format/yaml` | `*.fuzz0-2.yaml` | byte flip, drop-region, duplicate-region, injected comments | Captures baseline fuzz set for regression seeding. |

## Refresh procedure

1. Run the scoring script with a dedicated output directory and the agreed
   seed:

   ```bash
   python -m scripts.score_configsamples \
     --fuzz-output artifacts/fuzz/configsamples \
     --fuzz-count 3 \
     --fuzz-seed 1234
   ```
2. Copy only the mutated payloads you intend to promote into fixtures. The
   generator mirrors the `configsamples` tree, preserving relative paths.
3. When raising the fuzz count or tweaking size caps, record the reasoning,
   seed, and destination paths in the table above so regression maintainers can
   recreate the corpus exactly.
