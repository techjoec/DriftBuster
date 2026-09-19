# Profile summary checklist

Use this when detection profiles change, to see which profiles and configs were added, removed or changed.

- **Summarise** the stored profiles (a detection profile store JSON file):

  ```bash
  driftbuster detection-profile summary profiles.json --output profile-summary.json
  ```

  The summary lists the total profile and config counts and, per profile, its config identifiers in order. A missing or
  unreadable file prints `error: Unable to read JSON payload from <path>: …` and exits 1.

- **Diff** two summaries (for example the last release's and the current one):

  ```bash
  driftbuster detection-profile diff baseline-summary.json current-summary.json
  ```

  The result lists added and removed profiles and, for changed profiles, the added and removed config identifiers. A
  non-zero exit code means an input could not be read; fix it before going on.

- **Bridge hunt hits to profiles** when reviewing dynamic values against what a profile expects:

  ```bash
  driftbuster hunt <path> > hunt.json
  driftbuster detection-profile hunt-bridge profiles.json hunt.json --tag env:prod --root <path>
  ```

- **What to keep:** the summary and diff JSON with the change or release evidence, so the next review can diff against them.
- **If a config disappears unexpectedly:** find which profile held it in the previous summary, restore or rename it, and
  run the summary again. The detection profile store and summary code lives in
  `gui/DriftBuster.Backend/Profiles/Detection/`, with tests beside it in `gui/DriftBuster.Backend.Tests/`.
