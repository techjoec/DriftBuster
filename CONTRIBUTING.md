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

## 4. File Headers & Licensing

Every file **must** begin with:
```csharp
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 TechJoe and contributors
```

If adapted from permissive code:
```csharp
// Portions derived from ProjectName (MIT License, © YYYY OriginalAuthor)
// SPDX-License-Identifier: Apache-2.0
```

---

## 5. Dependency Hygiene

### ✅ Allowed
- Apache 2.0, MIT, BSD, MPL 2.0, CC0, or Public Domain.

### ❌ Disallowed
- GPL, AGPL, SSPL, Elastic License, or any code that restricts linking or distribution.

### Enforcement
Pull requests adding disallowed licenses **will be rejected automatically**.  
Use:
```bash
reuse lint
licensecheck .
```
before submitting.

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
| 3️⃣ | Run `dotnet format` and all unit tests |
| 4️⃣ | Verify SPDX headers and licenses |
| 5️⃣ | Submit PR with detailed provenance statement |
| 6️⃣ | Maintainers review build + legal scan results |

Each PR **must** include a short provenance note, e.g.:
> “All changes are original or derived from Apache-2.0 sources.  
> No third-party proprietary material included.”

---

## 8. Security & Privacy

- No telemetry or analytics submissions are permitted without user opt-in.
- Do not include any private keys, credentials, or internal URLs.
- Report vulnerabilities privately to **security@techjoe.work**.

---

## 9. Compliance Automation

Continuous Integration (CI) runs:
- `reuse lint` — verifies SPDX headers and license compliance.
- `dotnet test` — executes functional suite.
- `detect-secrets` — scans for credentials or API keys.
- `pip-licenses` / `npm ls --json` — audits dependency trees (where applicable).

PRs failing any compliance step will **not** be merged.

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
