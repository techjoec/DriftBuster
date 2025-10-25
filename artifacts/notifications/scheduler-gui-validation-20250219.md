# Scheduler notification payload samples — 2025-02-19

Saved after exercising the GUI schedule editor for the `nightly` profile. All tokens, URLs, and inboxes are redacted. Pair this
file with the GUI evidence referenced in `notes/status/gui-research.md` when reviewers need deterministic samples.

## Slack webhook (QA channel)

```json
{
  "text": "[REDACTED] nightly profile scheduled for 2025-02-20T02:00:00Z",
  "blocks": [
    {
      "type": "section",
      "text": {
        "type": "mrkdwn",
        "text": "*DriftBuster schedule pending*: nightly\nWindow: 01:00-05:00 UTC\nTags: env:prod, nightly"
      }
    }
  ]
}
```

## Microsoft Teams adaptive card

```json
{
  "type": "AdaptiveCard",
  "version": "1.5",
  "body": [
    {
      "type": "TextBlock",
      "text": "Nightly schedule queued",
      "weight": "Bolder"
    },
    {
      "type": "FactSet",
      "facts": [
        { "title": "Profile", "value": "nightly" },
        { "title": "Next run", "value": "2025-02-20T02:00:00Z" },
        { "title": "Window", "value": "01:00-05:00 UTC" }
      ]
    }
  ],
  "msteams": {
    "entities": [
      { "type": "mention", "text": "@oncall", "mentioned": { "id": "[REDACTED]", "name": "Night Ops" } }
    ]
  }
}
```

## SMTP summary (relay)

```text
Subject: [DriftBuster] nightly schedule pending — 2025-02-20T02:00Z
To: [REDACTED]@example.com
Cc: [REDACTED]@example.com

Schedule nightly will run at 2025-02-20T02:00:00Z (UTC).
Quiet window: 01:00-05:00 UTC
Tags: env:prod, nightly
Metadata:
  contact: oncall@example.com
  change-ticket: CHG-[REDACTED]
```

## CLI parity evidence

```sh
python -m driftbuster.run_profiles_cli schedule due --at 2025-02-20T02:30:00Z
```

The command above returns the same entry recorded by the GUI, confirming manifest parity.
