# DriftBuster – License & Compliance Enforcement

**License:** Apache License 2.0  
**Primary Copyright:** © 2025 TechJoe  
**SPDX Identifier:** Apache-2.0  

---

## 1. Purpose
To ensure all contributions, distributions, and community modules for DriftBuster
comply with the Apache 2.0 license while protecting users from liability.

---

## 2. Core Principles

- **Open & Permissive:** Anyone may use, modify, and distribute the code.
- **Attribution Required:** Every derivative must retain copyright + NOTICE files.
- **Patent Grant:** Contributors automatically grant a patent license.
- **No Warranty:** Distribution is strictly “AS IS”.

---

## 3. Contributor Rules

1. All contributions automatically fall under Apache 2.0 via the root `LICENSE`.
2. Update `NOTICE` when third-party attribution is required.
3. No proprietary or third-party confidential material may be submitted.
4. Dependencies must use OSI-approved licenses compatible with Apache 2.0
   (MIT, BSD, MPL 2.0, etc.).

---

## 4. Module & Formatter Policy

- Community-supplied modules may declare their own permissive license
  (MIT, BSD, Apache 2.0, or Public Domain).
- Modules **must** include a license header and README attribution.
- Any closed-source formatter interacting via public APIs is allowed,
  provided it does **not** link or embed core binaries.

---

## 5. Enforcement Workflow

| Stage | Action |
|-------|---------|
| **Detection** | Automated CI runs security scans and build/test validation. |
| **Violation** | Contributor notified and PR blocked until fixed. |
| **Escalation** | Repeat violations → contributor removal per governance policy. |
| **Third-Party Infringement** | File issue → remove artifact until cleared. |

---

## 6. Legal Contact

For license questions or potential violations:  
📧 **legal@techjoe.work**  
(Use only for IP and license concerns, not technical support.)

---

*This document is informational and non-binding. The full legal terms are
defined solely by the LICENSE file (Apache 2.0).*
