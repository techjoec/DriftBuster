from __future__ import annotations

import smtplib
from email.message import EmailMessage
from typing import Any

import pytest

from driftbuster.notifications import SMTPNotificationAdapter
from driftbuster.notifications.base import NotificationError, NotificationMessage


class DummySMTP:
    def __init__(self, host: str, port: int, timeout: float | None) -> None:
        self.host = host
        self.port = port
        self.timeout = timeout
        self.starttls_called = False
        self.login_args: tuple[str, str] | None = None
        self.sent_message: EmailMessage | None = None
        self.closed = False

    def __enter__(self) -> "DummySMTP":
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.closed = True

    def ehlo(self) -> None:  # pragma: no cover - no behaviour to assert
        return None

    def starttls(self) -> None:
        self.starttls_called = True

    def login(self, username: str, password: str) -> None:
        self.login_args = (username, password)

    def send_message(self, message: EmailMessage) -> None:
        self.sent_message = message


class FailingSMTP(DummySMTP):
    def send_message(self, message: EmailMessage) -> None:  # noqa: D401 - behaviour described above
        raise smtplib.SMTPException("failed")


def test_smtp_adapter_sends_message_with_metadata() -> None:
    captured: dict[str, DummySMTP] = {}

    def factory(host: str, port: int, timeout: float | None) -> DummySMTP:
        client = DummySMTP(host, port, timeout)
        captured["client"] = client
        return client

    adapter = SMTPNotificationAdapter(
        "smtp.example.com",
        port=2525,
        sender="alerts@example.com",
        recipients=["ops@example.com", ""],
        username="user",
        password="secret",
        smtp_factory=factory,
    )

    message = NotificationMessage(
        subject="Profile Drift",
        body="A nightly scan detected drift.",
        metadata={"profile": "nightly", "severity": "high"},
    )

    adapter.send(message)

    client = captured["client"]
    assert client.host == "smtp.example.com"
    assert client.port == 2525
    assert client.timeout == 15.0
    assert client.starttls_called is True
    assert client.login_args == ("user", "secret")
    assert client.sent_message is not None
    assert client.sent_message["Subject"] == "Profile Drift"
    assert client.sent_message.get_body(preferencelist=("plain",)).get_content().startswith(
        "A nightly scan detected drift."
    )
    assert "Metadata:" in client.sent_message.get_body(preferencelist=("plain",)).get_content()


def test_smtp_adapter_without_starttls() -> None:
    captured: dict[str, DummySMTP] = {}

    def factory(host: str, port: int, timeout: float | None) -> DummySMTP:
        client = DummySMTP(host, port, timeout)
        captured["client"] = client
        return client

    adapter = SMTPNotificationAdapter(
        "smtp.example.com",
        sender="alerts@example.com",
        recipients=["ops@example.com"],
        use_starttls=False,
        smtp_factory=factory,
    )

    adapter.send(NotificationMessage(subject="Alert", body="Check drift."))

    client = captured["client"]
    assert client.starttls_called is False
    assert client.login_args is None


def test_smtp_adapter_requires_matching_credentials() -> None:
    with pytest.raises(NotificationError):
        SMTPNotificationAdapter(
            "smtp.example.com",
            sender="alerts@example.com",
            recipients=["ops@example.com"],
            username="user",
        )


def test_smtp_adapter_wraps_smtp_errors() -> None:
    def factory(host: str, port: int, timeout: float | None) -> FailingSMTP:
        return FailingSMTP(host, port, timeout)

    adapter = SMTPNotificationAdapter(
        "smtp.example.com",
        sender="alerts@example.com",
        recipients=["ops@example.com"],
        smtp_factory=factory,
    )

    with pytest.raises(NotificationError):
        adapter.send(NotificationMessage(subject="Alert", body="Failure"))
