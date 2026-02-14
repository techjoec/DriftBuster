# Contributing to DriftBuster

DriftBuster is an open-source configuration and diff utility licensed under the **Apache License 2.0**.  
We welcome community contributions while maintaining strict legal integrity and provenance controls to prevent any intellectual property (IP) contamination.

---

## 1. Code of Conduct

All participants must follow the [Contributor Covenant v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).  
Harassment, hostility, or illegal behavior will result in permanent exclusion from the project.

---

## 2. Contribution Overview

- **All contributions** are accepted under **Apache 2.0**.
- **No exceptions.** If you cannot legally relicense your contribution under Apache 2.0, you **must not submit** it.
- **All submissions are subject to provenance review** for third-party material, including text, code, schemas, and binary assets.

---

## 3. Provenance & Legal Integrity

To maintain zero IP creep, all contributors must ensure:

### 3.1 You Own or Can License All Code You Submit
- Only contribute **original code you authored**, or
- Code from **third-party permissive sources** (MIT, BSD, Apache 2.0, CC0, MPL 2.0).

### 3.2 Explicitly Forbidden Sources
Do **not** submit code, snippets, or data from:
- GPL or AGPL licensed projects (copyleft risk).
- Closed-source SDKs, decompiled binaries, or reverse-engineered materials.
- Any vendor, partner, or employer’s internal or proprietary repositories.
- Any AI-generated code that includes **copied or verbatim excerpts** from copyrighted works (check provenance via tool logs if applicable).

### 3.3 Documentation & Configuration
- When describing vendor formats (e.g., `.ini`, `.xml`, `.config`), base on **publicly observable behavior** only.
- Do **not** reproduce, quote, or include vendor documentation verbatim.
- Include a provenance comment such as:
  ```csharp
  // Derived from publicly documented behavior, not vendor source.
  ```

---

## 4. Licensing Basics

- The repository’s root `LICENSE` (Apache 2.0) covers all contributions by default.
- File-level SPDX headers are optional; include them only when you believe it aids clarity.
- When incorporating permissively licensed material, cite the original source in the file and update `NOTICE` if attribution is required.

---

## 5. Dependency Hygiene

### ✅ Allowed
- Apache 2.0, MIT, BSD, MPL 2.0, CC0, or Public Domain.

### ❌ Disallowed
- GPL, AGPL, SSPL, Elastic License, or any code that restricts linking or distribution.

### Enforcement
Pull requests adding disallowed licenses **will be rejected**.  
Use `licensecheck .` locally before submitting to catch unexpected copyleft code.

---

## 6. Modules & Formatters

- External modules and formatters **must include their own LICENSE** file.
- Modules interacting through DriftBuster’s public API may use permissive licenses only.
- Closed-source modules may exist but **cannot statically link** or embed DriftBuster code.
- All module metadata must declare:
  ```json
  {
    "name": "MyFormatter",
    "license": "Apache-2.0",
    "provenance": "authored-original"
  }
  ```

---

## 7. Contribution Workflow

| Step | Description |
|------|--------------|
| 1️⃣ | Fork and clone the repo |
| 2️⃣ | Create a feature branch (`feature/<topic>`) |
| 3️⃣ | Run `dotnet format`, all unit tests, and coverage checks (`./scripts/verify_coverage.sh` or `python -m scripts.verify_coverage`) |
| 4️⃣ | Review licensing notes and update `NOTICE` if needed |
| 5️⃣ | Submit PR with detailed provenance statement |
| 6️⃣ | Maintainers review your build output and legal scan notes |

Each PR **must** include a short provenance note, e.g.:
> “All changes are original or derived from Apache-2.0 sources.  
> No third-party proprietary material included.”

---

## 7.1 Coverage Baseline (90%+)

Keep line coverage at 90% or higher for:

- All modified Python files under `src/driftbuster`
- All modified .NET GUI/Backend files under `gui/`
- Any new format plugin(s) and helpers

Enforce locally (no CI hooks). Suggested commands:

- Python
  - `coverage run --source=src/driftbuster -m pytest -q`
  - `coverage report --fail-under=90`
  - Optional: `coverage json -o coverage.json` and `coverage html`
- .NET
  - `dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --collect:"XPlat Code Coverage" --results-directory artifacts/coverage-dotnet`
  - Threshold (local): `dotnet test -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total`
- Repo‑wide summary
  - `python -m scripts.coverage_report`

Shortcut: run `./scripts/verify_coverage.sh` (POSIX) or `python -m scripts.verify_coverage` to execute both suites with thresholds and print the summary.

When adding a new format:

- Add `tests/formats/test_<format>_plugin.py` mirroring existing detectors.
- Cover primary variant(s), negative cases, and edge heuristics to keep the
  plugin at ≥90% coverage.
- Follow `docs/plugin-test-checklist.md` to ensure consistent coverage and cases.
- Update docs in `docs/detection-types.md` and `docs/format-support.md` as needed.

---

## 8. Security & Privacy

- No telemetry or analytics submissions are permitted without user opt-in.
- Do not include any private keys, credentials, or internal URLs.
- Report vulnerabilities privately to **security@techjoe.work**.

---

## 9. Local Compliance Checks

Run locally before submitting:
- `dotnet test` — executes functional suite.
- `gitleaks dir . -v` — scans for credentials or API keys.

Submissions failing these local checks will be sent back for fixes.

### Tooling Guardrails

- No GitHub Actions/Runners for this repository. Do not add workflows, runners,
  or pipeline descriptors. Keep all checks local and documented in the PR.

---

## 10. Enforcement

Violations trigger escalating actions:
1. PR rejection with remediation guidance.
2. Maintainer review for deliberate or repeated infringement.
3. Permanent ban for intentional IP or license violations.

---

## 11. Contact

- 📧 Legal & IP: **legal@techjoe.work**
- 📧 Security: **security@techjoe.work**
- 📧 Maintainers: **maintainers@techjoe.work**

---

## 12. Summary

By contributing, you certify:
> “I have the right to submit this code under Apache 2.0.  
> It contains no proprietary, confidential, or infringing material.”

See also:
- [`LICENSE`](./LICENSE)
- [`NOTICE`](./NOTICE)
- [`LEGAL_ENFORCEMENT.md`](./LEGAL_ENFORCEMENT.md)
