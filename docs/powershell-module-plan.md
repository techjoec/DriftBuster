# PowerShell Module Plan

Purpose: Capture the plan for a future PowerShell wrapper once the Python core
and CLI work stabilize.

## Objectives

- Expose a `Get-DriftBusterScan` cmdlet that shells out to the Python CLI (or
  uses `python -m driftbuster`) and returns structured objects.
- Provide reporting-focused wrappers (`Export-DriftBusterJson`,
  `Export-DriftBusterHtml`, `Get-DriftBusterDiff`) that funnel into the JSON,
  HTML, and diff adapters while reusing the redaction token list.
- Keep module dependencies minimal—PowerShell 5.1+ on Windows, Python 3.10+
  available in `PATH`.
- Preserve manual validation workflow; no automated PS tests.

## Module Sketch

- Module name: `DriftBuster.Tools` (subject to change).
- Functions:
  - `Get-DriftBusterScan -Path <string> [-Glob <string>] [-AsJson] [-SampleSize <int>]`
  - `Invoke-DriftBusterRaw -Arguments <string[]>` (internal helper wrapping
    the Python CLI).
  - `Find-DriftBusterDynamic -Path <string> [-Rule <string>]` to surface hunt
    results (leveraging default rules or custom rule files).
  - `Export-DriftBusterHtml -Path <string> [-Output <string>] [-MaskToken <string[]>]`
    to call `--html` behind the scenes and record the generated file for manual
    review.
  - `Get-DriftBusterDiff -Baseline <string> -Current <string> [-MaskToken <string[]>]`
    to proxy the diff helper and emit stats alongside the unified diff text.
- Implementation options:
  1. **Preferred:** call the Python CLI command (`driftbuster`) once available.
  2. **Fallback:** run `python -m driftbuster` while we avoid packaging.
- Output: by default, emit custom objects with `Path`, `Format`, `Variant`,
  `Confidence`, `Reasons`, `Metadata`. When `-AsJson` is specified, write raw
  JSON lines (pass-through).

## Validation Checklist

- Ensure the module detects Python prerequisites and surfaces friendly errors
  if missing.
- Manual scenarios:
  - `Get-DriftBusterScan -Path C:\configs` — verify table output.
  - `Get-DriftBusterScan -Path . -Glob "**/*.config" -AsJson` — confirm JSON
    passthrough.
  - `Export-DriftBusterHtml -Path C:\configs -Output report.html` — open the
    HTML file and verify diff, summary, hunt sections display and redaction
    badges show tracked placeholders.
  - `Get-DriftBusterDiff -Baseline baseline.json -Current current.json` — check
    stats and placeholder substitution line up with manual diff expectations.
  - Error handling: unreadable path, missing Python executable.
- Record commands + outcomes in `CLOUDTASKS.md` once the module is built.

## Packaging & Distribution Notes

- Initially distribute as a script module in the repo (`powershell/DriftBuster.Tools/`).
- Later consider publishing to PSGallery after CLI stabilizes.
- Document installation steps in `README.md` (`Import-Module` instructions,
  prerequisites) plus references to the reporting cmdlets and masking
  prerequisites.

## Outstanding Questions

- Should the module allow inline Python overrides (custom interpreters)?
- How do we cache or reuse Python processes for large scans? (Default plan:
  spawn per invocation, revisit once CLI performance is proven.)
- Do we need digital signing for distribution? (likely yes before PSGallery).
