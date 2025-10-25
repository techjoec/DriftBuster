from __future__ import annotations

from dataclasses import dataclass
from typing import Mapping, MutableMapping, Protocol


class NotificationError(RuntimeError):
    """Raised when an adapter fails to deliver a notification."""


@dataclass(frozen=True, slots=True)
class NotificationMessage:
    """Simple structure describing a notification payload."""

    subject: str
    body: str
    metadata: Mapping[str, object] | None = None

    def with_metadata(self, metadata: Mapping[str, object] | None) -> "NotificationMessage":
        if not metadata:
            return self
        merged: MutableMapping[str, object] = {}
        if self.metadata:
            merged.update(self.metadata)
        merged.update(metadata)
        return NotificationMessage(subject=self.subject, body=self.body, metadata=merged)


class NotificationAdapter(Protocol):
    """Common interface implemented by notification transports."""

    def send(self, message: NotificationMessage) -> None:
        """Deliver ``message`` to the configured channel."""


__all__ = [
    "NotificationAdapter",
    "NotificationError",
    "NotificationMessage",
]
