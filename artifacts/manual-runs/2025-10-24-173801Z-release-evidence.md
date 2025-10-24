# Multi-server walkthrough summary (2025-10-24-173801Z-release-evidence)

- Captured: 2025-10-24T17:38:01.743493+00:00
- Data root: `/root/.driftbuster-walkthrough-tmp/multi-server`
- Cold run hosts: server01, server02, server03, server04, server05, server06, server07, server08, server09, server10
- Hot run cache reuse: server01=true, server02=true, server03=true, server04=true, server05=true, server06=true, server07=true, server08=true, server09=true, server10=true
- Cache entries written: 37

## Cache file preview

  - 01e0be1281a815a0048b4ee872776c20622347e1.json
  - 1ca3420b834558c25e86b71ce043c10cd3a94d48.json
  - 20826087788c80525a7dc205661b31ddf40d91c2.json
  - 28fbd1b74ac8208ba9f40cfba99bd14adcc8614f.json
  - 31eda74b1748f8ddae1f97df3e54194e0f7f65c4.json
  - 327e03bbfe0722cac940c089a8578b6e8cbcd1c5.json
  - 33c645a5254381068e9f9a8d835287ebd030f41c.json
  - 36cd3c34ac8abc14aaa999cac0ddbfb95676bf89.json
  - 440a1b5899ce8ae0751870b8aa279bf2f9636e93.json
  - 5ad67fa6bd7c0f95b7d1ea073eaf58f078dc6eb0.json
  - ...

## Diff planner verification

- Drilldown sample: `json/structured-settings-json/object` (1 comparison(s))
  - Flags: drift_count=9, has_secrets=True, has_masked_tokens=False, has_validation_issues=False
  - Metadata: content_type=text, baseline_name=app/appsettings.json, comparison_name=app/appsettings.json
  - Comparison stats: added_lines=0, removed_lines=0, changed_lines=0, diff_digest=sha256:1c4c0006671fd71d7ac0f48967ef487d8f147f70c715d4824c2529d73f87f926
  - Unified diff preview:
  ```diff
  <no diff lines>
  ```

## Artefacts

- Console transcript: `/workspace/DriftBuster/artifacts/manual-runs/2025-10-24-173801Z-release-evidence-console.txt`
- Source samples: `/workspace/DriftBuster/samples/multi-server`
