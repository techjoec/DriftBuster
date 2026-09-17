> Historical note: written when DriftBuster had a Python engine; the product is .NET only.

# Hunt + profile review checklist

## Context
- [ ] Environment:
- [ ] Reviewer:
- [ ] Date:
- [ ] Profile snapshot reference (path or commit):
- [ ] Hunt command (include `exclude_patterns` / `return_json` args):

## Token decisions
| token_name | file / relative path | line | excerpt / masking notes | decision (approved/reject) | placeholder |
| --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |

- Log excerpts without exposing secrets; use deterministic placeholders when the
  snippet contains sensitive material.
- Tie each decision back to the matching configuration profile metadata entry.
- Use `notes/snippets/token-catalog.py` to generate hashed token entries before
  closing the review.

### Sample plan transforms (automation reference)

| token_name | placeholder | sample value | source (relative) |
| --- | --- | --- | --- |
| `server_name` | `{{ server_name }}` | `app.local` | `deployments/prod-web-01/config.txt` |
| `database_server` | `<<database_server>>` | `db.internal.local` | `deployments/prod-web-01/settings.config` |

## Follow-up
- [ ] Profile metadata updated (commit hash / file reference):
- [ ] Hunt output archived (location outside repo):
- [ ] Masking verified against `docs/legal-safeguards.md`:
- [ ] Token catalog updated (hash location):

### Automation backlog
- Capture future automation ideas (CLI flags, token replacement helpers,
  structured diff tooling) here without committing scripts yet.
- [ ] New automation idea recorded:
- Notes:
