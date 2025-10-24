# Token approval checklist

## Context
- [ ] Environment / scope:
- [ ] Reviewer:
- [ ] Date:
- [ ] Hunt payload reference (path outside repo):
- [ ] Profile metadata reference (file + commit):

## Tokens under review
| token_name | hunt rule | placeholder written | source file / path | excerpt hash | catalog_variant | sample_hash (JSON) | last confirmed (UTC) | expiry / next check | secure storage location | notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |  |  |  |  |  |

- Hash excerpts instead of copying secrets. Store the hash and placeholder here;
  keep the actual value in an approved secret store (password manager, vault,
  encrypted share).
- Ensure placeholders match the format used in configuration templates (e.g.,
  `{{ token_name }}`) so diffs can identify expected dynamic values.
- Record the catalog variant (`structured-settings-json`, etc.) so reviewers can
  cross-check profile expectations without re-opening the sample payload.
- When the source payload is JSON, compute a deterministic sample hash (e.g.,
  SHA256 of the sanitised JSON) and store it in the new column instead of
  keeping the raw excerpt in-repo.

## Review steps
- [ ] Compare each entry against the structured hunt payload and confirm the
      `token_name` matches the rule `token_name`.
- [ ] Verify the placeholder is recorded in configuration profile metadata.
- [ ] Confirm secure storage location is documented and accessible to the
      reviewer group.
- [ ] Re-run detector + hunt scans after substitution and confirm no unexpected
      hits remain.
- [ ] Update expiry / next check dates for tokens that require periodic renewal.

## Follow-up
- [ ] Archive updated hunt payload + checklist outside the repository.
- [ ] Notify the profile maintainer if metadata needs an additional field.
- [ ] Record any unresolved tokens or questions for user review in
      `notes/status/hold-log.md`.

## JSON approval log schema

Token approvals now live alongside this checklist in a machine-readable JSON
log managed by `TokenApprovalStore`. Each entry is a dictionary containing:

- `token_name` (string): Rule token identifier (`server_name`, `feature_flag`).
- `placeholder` (string): Placeholder written into config templates
  (`{{ server_name }}`).
- `excerpt_hash` (string, optional): SHA256 of the excerpt or matched value.
- `source_path` (string, optional): Relative path pointing to the reviewed
  artifact.
- `catalog_variant` (string, optional): Configuration variant label used when
  generating token catalogues.
- `sample_hash` (string, optional): Hash of a sanitised hunt payload when the
  source was JSON.
- `approved_by` (string, optional): Reviewer identifier.
- `approved_at_utc` (string, optional): ISO8601 UTC timestamp of approval.
- `expires_at_utc` (string, optional): Renewal or re-review deadline.
- `secure_location` (string, optional): Reference to the vault or secret store
  holding the raw value.
- `notes` (string, optional): Free-form audit context.

Persist approvals with `TokenApprovalStore.dump(path)` and load them back with
`TokenApprovalStore.load(path)` when updating this checklist.
