## Day 0 Baseline Creation

New to DriftBuster with 10+ servers and no baseline yet? Use the Day 0 flow to
scan all servers, identify common configs by relative path and format, and
propose a baseline snapshot + profile store for review.

What it does
- Scans N server directories and detects config formats.
- Groups files by relative path (e.g., `app/appsettings.json`).
- Canonicalises content (XML/JSON/text) and chooses the most common content as
  the proposed baseline per group (tie-breaker: minimal total edit distance).
- Writes a baseline snapshot with chosen content, a `ProfileStore` JSON, and an
  optional HTML report showing diffs and hunt highlights.

Quick start
1. Put server snapshots under a common root (e.g., `servers/server01`, …, `server10`).
2. Demo with included samples:
   - `./scripts/demo_day0_baseline.sh`
   - Outputs under `artifacts/day0-demo/` (baseline snapshot, profile store, audit, HTML report)
3. Run the baseline builder on your own servers:
   - `python -m scripts.create_day0_baseline --root servers --output artifacts/day0 --report`
4. Review outputs:
   - `artifacts/day0/baseline_snapshot/` — canonical baseline files by relative path
   - `artifacts/day0/profile_store.day0.json` — proposed `ProfileStore` payload
   - `artifacts/day0/baseline.decisions.json` — audit for each file (chosen server, counts)
   - `artifacts/day0/day0-baseline.html` — unified diffs + hunt hits across servers
5. Edit `baseline.decisions.json` and/or replace files under `baseline_snapshot/` if you
   want to override automatic choices, then re-run your scans using the `profile_store`.

Heuristics
- Choose the baseline content that appears most frequently across servers.
- On ties, pick the content with the smallest overall edit distance to others.
- Canonicalisation:
  - XML: structural normalisation (preserve prolog + doctype; collapse whitespace-only text/tails; sort attributes).
  - JSON: parse + write with sorted keys (fallback to text if parse fails).
  - Other: newline + trailing space normalisation.

Next steps
- Use `driftbuster.profile_cli summary` to snapshot the profile store.
- Run profile-aware scans on future captures to highlight drift automatically.
- Pair with `hunt_path` rules to flag dynamic values (hosts, paths, versions) and
  enrich profiles over time.
