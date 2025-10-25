from __future__ import annotations

import json

import pytest
from urllib.error import URLError

from driftbuster.notifications import SlackWebhookAdapter
from driftbuster.notifications.base import NotificationError, NotificationMessage


def test_slack_adapter_formats_payload() -> None:
    captured: dict[str, object] = {}

    def fake_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        captured["url"] = url
        captured["payload"] = payload
        captured["timeout"] = timeout
        return 200, "ok"

    adapter = SlackWebhookAdapter(
        "https://hooks.slack.com/services/T000/B000/XXX",
        timeout=20.0,
        post=fake_post,
    )

    message = NotificationMessage(
        subject="Profile Drift",
        body="Nightly scans detected configuration drift.",
        metadata={"profile": "nightly", "severity": "high"},
    )

    adapter.send(message)

    payload = captured["payload"]
    assert payload["mrkdwn"] is True
    assert payload["text"].startswith("*Profile Drift*")
    fields = payload["attachments"][0]["fields"]  # type: ignore[index]
    titles = {field["title"] for field in fields}
    assert {"profile", "severity"} <= titles
    assert captured["timeout"] == 20.0


def test_slack_adapter_rejects_error_response() -> None:
    adapter = SlackWebhookAdapter(
        "https://hooks.slack.com/services/T000/B000/XXX",
        post=lambda *_: (500, "error"),
    )

    with pytest.raises(NotificationError):
        adapter.send(NotificationMessage(subject="Alert", body="Failure"))


def test_slack_adapter_requires_url() -> None:
    with pytest.raises(NotificationError):
        SlackWebhookAdapter("  ")


def test_slack_adapter_without_metadata() -> None:
    captured: dict[str, object] = {}

    def fake_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        captured["payload"] = payload
        return 200, "ok"

    adapter = SlackWebhookAdapter("https://hooks.slack.com/services/T000/B000/XXX", post=fake_post)

    adapter.send(NotificationMessage(subject="Alert", body=""))

    payload = captured["payload"]
    assert payload["text"].startswith("*Alert*")
    assert "attachments" not in payload


def test_slack_adapter_wraps_url_errors() -> None:
    def failing_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        raise URLError("boom")

    adapter = SlackWebhookAdapter(
        "https://hooks.slack.com/services/T000/B000/XXX",
        post=failing_post,
    )

    with pytest.raises(NotificationError):
        adapter.send(NotificationMessage(subject="Alert", body="body"))


def test_slack_adapter_without_subject() -> None:
    captured: dict[str, object] = {}

    def fake_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        captured["payload"] = payload
        return 200, "ok"

    adapter = SlackWebhookAdapter("https://hooks.slack.com/services/T000/B000/XXX", post=fake_post)

    adapter.send(NotificationMessage(subject="", body="Plain message"))

    assert captured["payload"]["text"] == "Plain message"


def test_slack_adapter_default_post(monkeypatch: pytest.MonkeyPatch) -> None:
    captured: dict[str, object] = {}

    class DummyResponse:
        def __enter__(self) -> "DummyResponse":
            return self

        def __exit__(self, exc_type, exc, tb) -> bool:  # type: ignore[override]
            return False

        def getcode(self) -> int:
            return 200

        def read(self) -> bytes:
            return b"ok"

    def fake_urlopen(request, timeout=None):  # type: ignore[no-untyped-def]
        captured["url"] = request.full_url
        captured["data"] = request.data
        captured["timeout"] = timeout
        return DummyResponse()

    monkeypatch.setattr("driftbuster.notifications.slack.urlopen", fake_urlopen)

    adapter = SlackWebhookAdapter("https://hooks.slack.com/services/T000/B000/DEFAULT")

    adapter.send(NotificationMessage(subject="Alert", body="Body"))

    assert captured["url"].endswith("DEFAULT")
    payload = json.loads(captured["data"].decode("utf-8"))
    assert payload["text"].startswith("*Alert*")
