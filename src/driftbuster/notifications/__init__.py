"""Notification adapters for scheduler alerts."""

from .base import NotificationAdapter, NotificationError, NotificationMessage
from .slack import SlackWebhookAdapter
from .smtp import SMTPNotificationAdapter
from .teams import TeamsWebhookAdapter

__all__ = [
    "NotificationAdapter",
    "NotificationError",
    "NotificationMessage",
    "SlackWebhookAdapter",
    "SMTPNotificationAdapter",
    "TeamsWebhookAdapter",
]
