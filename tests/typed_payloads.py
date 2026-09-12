"""Typed accessors for JSON-shaped payloads returned by the engine under test."""

from __future__ import annotations

from typing import Any


def as_dict(value: object) -> dict[str, Any]:
    """Assert ``value`` is a mapping payload and return it with dynamic value types."""

    assert isinstance(value, dict), f"expected dict payload, got {type(value).__name__}"
    return value


def as_list(value: object) -> list[Any]:
    """Assert ``value`` is a list payload and return it with dynamic item types."""

    assert isinstance(value, list), f"expected list payload, got {type(value).__name__}"
    return value
