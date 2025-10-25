from __future__ import annotations

import json
from typing import Callable, Mapping
from urllib.error import URLError
from urllib.request import Request, urlopen

from .base import NotificationError, NotificationMessage

_Response = tuple[int, str]


def _post_json(url: str, payload: Mapping[str, object], timeout: float | None) -> _Response:
    data = json.dumps(payload).encode("utf-8")
    request = Request(url, data=data, headers={"Content-Type": "application/json"})
    with urlopen(request, timeout=timeout) as response:  # type: ignore[no-untyped-call]
        status = response.getcode()
        body = response.read().decode("utf-8", "ignore")
    return status, body


def _render_metadata(metadata: Mapping[str, object]) -> list[dict[str, str]]:
    return [
        {"name": str(key), "value": str(value)}
        for key, value in sorted(metadata.items())
    ]


class TeamsWebhookAdapter:
    """Send notifications to Microsoft Teams via incoming webhooks."""

    def __init__(
        self,
        webhook_url: str,
        *,
        timeout: float | None = 10.0,
        post: Callable[[str, Mapping[str, object], float | None], _Response] | None = None,
    ) -> None:
        if not webhook_url.strip():
            raise NotificationError("Teams webhook URL must not be empty")
        self._url = webhook_url
        self._timeout = timeout
        self._post = post or _post_json

    def _build_payload(self, message: NotificationMessage) -> dict[str, object]:
        text = message.body or message.subject
        summary = message.subject or (message.body[:60] if message.body else "Notification")
        payload: dict[str, object] = {
            "@type": "MessageCard",
            "@context": "https://schema.org/extensions",
            "summary": summary,
            "text": text,
        }
        if message.metadata:
            payload.setdefault("sections", [])
            payload["sections"].append({"facts": _render_metadata(message.metadata)})
        return payload

    def send(self, message: NotificationMessage) -> None:
        payload = self._build_payload(message)
        try:
            status, body = self._post(self._url, payload, self._timeout)
        except URLError as exc:
            raise NotificationError("Failed to contact Teams webhook") from exc
        if status >= 300:
            raise NotificationError(f"Teams webhook rejected payload: status={status} body={body!r}")


__all__ = ["TeamsWebhookAdapter"]
