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


class SlackWebhookAdapter:
    """Send notifications to Slack via incoming webhooks."""

    def __init__(
        self,
        webhook_url: str,
        *,
        timeout: float | None = 10.0,
        post: Callable[[str, Mapping[str, object], float | None], _Response] | None = None,
    ) -> None:
        if not webhook_url.strip():
            raise NotificationError("Slack webhook URL must not be empty")
        self._url = webhook_url
        self._timeout = timeout
        self._post = post or _post_json

    def _build_payload(self, message: NotificationMessage) -> dict[str, object]:
        text = message.body
        if message.subject:
            text = f"*{message.subject}*\n{text}" if text else f"*{message.subject}*"
        payload: dict[str, object] = {"text": text, "mrkdwn": True}
        if message.metadata:
            fields = [
                {"title": str(key), "value": str(value), "short": True}
                for key, value in sorted(message.metadata.items())
            ]
            if fields:
                payload["attachments"] = [{"fields": fields}]
        return payload

    def send(self, message: NotificationMessage) -> None:
        payload = self._build_payload(message)
        try:
            status, body = self._post(self._url, payload, self._timeout)
        except URLError as exc:
            raise NotificationError("Failed to contact Slack webhook") from exc
        if status != 200 or (body and body.strip().lower() != "ok"):
            raise NotificationError(f"Slack webhook rejected payload: status={status} body={body!r}")


__all__ = ["SlackWebhookAdapter"]
