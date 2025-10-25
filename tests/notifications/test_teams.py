from __future__ import annotations

import json

import pytest
from urllib.error import URLError

from driftbuster.notifications import TeamsWebhookAdapter
from driftbuster.notifications.base import NotificationError, NotificationMessage


def test_teams_adapter_formats_payload() -> None:
    captured: dict[str, object] = {}

    def fake_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        captured["url"] = url
        captured["payload"] = payload
        captured["timeout"] = timeout
        return 200, ""

    adapter = TeamsWebhookAdapter(
        "https://outlook.office.com/webhook/abcd",
        timeout=12.0,
        post=fake_post,
    )

    message = NotificationMessage(
        subject="Profile Drift",
        body="Nightly scan detected configuration drift.",
        metadata={"profile": "nightly", "severity": "high"},
    )

    adapter.send(message)

    payload = captured["payload"]
    assert payload["@type"] == "MessageCard"
    assert payload["summary"] == "Profile Drift"
    section = payload["sections"][0]  # type: ignore[index]
    facts = {item["name"]: item["value"] for item in section["facts"]}
    assert facts["profile"] == "nightly"
    assert captured["timeout"] == 12.0


def test_teams_adapter_rejects_error_status() -> None:
    adapter = TeamsWebhookAdapter(
        "https://outlook.office.com/webhook/abcd",
        post=lambda *_: (400, "bad request"),
    )

    with pytest.raises(NotificationError):
        adapter.send(NotificationMessage(subject="Alert", body="Failure"))


def test_teams_adapter_requires_url() -> None:
    with pytest.raises(NotificationError):
        TeamsWebhookAdapter(" ")


def test_teams_adapter_without_metadata() -> None:
    captured: dict[str, object] = {}

    def fake_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        captured["payload"] = payload
        return 200, ""

    adapter = TeamsWebhookAdapter("https://outlook.office.com/webhook/abcd", post=fake_post)

    adapter.send(NotificationMessage(subject="Alert", body=""))

    payload = captured["payload"]
    assert payload["text"] == "Alert"
    assert "sections" not in payload


def test_teams_adapter_wraps_url_errors() -> None:
    def failing_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        raise URLError("boom")

    adapter = TeamsWebhookAdapter("https://outlook.office.com/webhook/abcd", post=failing_post)

    with pytest.raises(NotificationError):
        adapter.send(NotificationMessage(subject="Alert", body="body"))


def test_teams_adapter_uses_body_for_summary() -> None:
    captured: dict[str, object] = {}

    def fake_post(url: str, payload: dict[str, object], timeout: float | None) -> tuple[int, str]:
        captured["payload"] = payload
        return 200, ""

    adapter = TeamsWebhookAdapter("https://outlook.office.com/webhook/abcd", post=fake_post)

    adapter.send(NotificationMessage(subject="", body="Nightly drift detected"))

    payload = captured["payload"]
    assert payload["summary"].startswith("Nightly drift detected")


def test_teams_adapter_default_post(monkeypatch: pytest.MonkeyPatch) -> None:
    captured: dict[str, object] = {}

    class DummyResponse:
        def __enter__(self) -> "DummyResponse":
            return self

        def __exit__(self, exc_type, exc, tb) -> bool:  # type: ignore[override]
            return False

        def getcode(self) -> int:
            return 200

        def read(self) -> bytes:
            return b""

    def fake_urlopen(request, timeout=None):  # type: ignore[no-untyped-def]
        captured["url"] = request.full_url
        captured["data"] = request.data
        captured["timeout"] = timeout
        return DummyResponse()

    monkeypatch.setattr("driftbuster.notifications.teams.urlopen", fake_urlopen)

    adapter = TeamsWebhookAdapter("https://outlook.office.com/webhook/default")

    adapter.send(NotificationMessage(subject="Alert", body="Body"))

    assert captured["url"].endswith("default")
    payload = json.loads(captured["data"].decode("utf-8"))
    assert payload["summary"] == "Alert"
