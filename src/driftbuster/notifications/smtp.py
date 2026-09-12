from __future__ import annotations

import smtplib
from collections.abc import Callable, Iterable, Sequence
from email.message import EmailMessage
from typing import Protocol

from .base import NotificationError, NotificationMessage


class _SMTPClient(Protocol):
    """Subset of :class:`smtplib.SMTP` exercised by the adapter."""

    def __enter__(self) -> object: ...

    def __exit__(self, exc_type: object, exc: object, tb: object) -> object: ...

    def ehlo(self) -> object: ...

    def starttls(self) -> object: ...

    def login(self, user: str, password: str, /) -> object: ...

    def send_message(self, msg: EmailMessage, /) -> object: ...


_SMTPFactory = Callable[[str, int, float | None], _SMTPClient]


def _default_factory(host: str, port: int, timeout: float | None) -> smtplib.SMTP:
    # typeshed declares ``timeout: float`` but smtplib accepts ``None`` (no timeout) at runtime.
    return smtplib.SMTP(host=host, port=port, timeout=timeout)  # pyright: ignore[reportArgumentType]


class SMTPNotificationAdapter:
    """Deliver notifications through SMTP."""

    def __init__(
        self,
        host: str,
        *,
        port: int = 587,
        sender: str,
        recipients: Sequence[str],
        username: str | None = None,
        password: str | None = None,
        use_starttls: bool = True,
        timeout: float | None = 15.0,
        smtp_factory: _SMTPFactory | None = None,
        extra_headers: Iterable[tuple[str, str]] | None = None,
    ) -> None:
        if not host.strip():
            raise NotificationError("SMTP host must not be empty")
        if not sender.strip():
            raise NotificationError("SMTP sender must not be empty")
        cleaned = [addr.strip() for addr in recipients if addr and addr.strip()]
        if not cleaned:
            raise NotificationError("At least one SMTP recipient is required")
        if (username is None) ^ (password is None):
            raise NotificationError("Username and password must be provided together")
        self._host = host
        self._port = port
        self._sender = sender
        self._recipients = tuple(cleaned)
        self._username = username
        self._password = password
        self._use_starttls = use_starttls
        self._timeout = timeout
        self._factory = smtp_factory or _default_factory
        self._headers = tuple(extra_headers or ())

    def _build_message(self, message: NotificationMessage) -> EmailMessage:
        email = EmailMessage()
        email["Subject"] = message.subject
        email["From"] = self._sender
        email["To"] = ", ".join(self._recipients)
        for header, value in self._headers:
            email[header] = value
        body_lines: list[str] = []
        if message.body:
            body_lines.append(message.body)
        if message.metadata:
            if body_lines:
                body_lines.append("")
            body_lines.append("Metadata:")
            body_lines.extend(
                f"{key}: {value}" for key, value in sorted(message.metadata.items())
            )
        content = "\n".join(body_lines) if body_lines else message.body
        email.set_content(content)
        return email

    def send(self, message: NotificationMessage) -> None:
        email = self._build_message(message)
        try:
            client = self._factory(self._host, self._port, self._timeout)
        except OSError as exc:  # pragma: no cover - defensive network failure
            raise NotificationError("Failed to connect to SMTP server") from exc
        try:
            with client:
                client.ehlo()
                if self._use_starttls:
                    client.starttls()
                    client.ehlo()
                if self._username and self._password:
                    client.login(self._username, self._password)
                client.send_message(email)
        except smtplib.SMTPException as exc:
            raise NotificationError("SMTP delivery failed") from exc


__all__ = ["SMTPNotificationAdapter"]
