from __future__ import annotations

from driftbuster.notifications.base import NotificationMessage


def test_notification_message_with_metadata_merges() -> None:
    original = NotificationMessage(subject="Alert", body="body", metadata={"env": "prod"})

    updated = original.with_metadata({"profile": "nightly"})

    assert updated is not original
    assert updated.metadata == {"env": "prod", "profile": "nightly"}


def test_notification_message_with_metadata_noop() -> None:
    original = NotificationMessage(subject="Alert", body="body", metadata={"env": "prod"})

    assert original.with_metadata(None) is original
