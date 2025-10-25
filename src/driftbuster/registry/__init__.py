"""Windows Registry live scan utilities.

This package provides a backend-abstracted interface for enumerating installed
applications and scanning registry trees to locate settings by keyword or
pattern. It favours read-only access and graceful fallbacks on non‑Windows
platforms.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from functools import wraps
import time
from typing import Callable, Dict, Mapping, Tuple, TypeVar, cast
import re

from .scan import (
    is_windows,
    RegistryApp,
    RegistryHit,
    SearchSpec,
    enumerate_installed_apps as _enumerate_installed_apps,
    find_app_registry_roots as _find_app_registry_roots,
    search_registry as _search_registry,
)

_Operation = TypeVar("_Operation", bound=Callable[..., object])


@dataclass
class _UsageCounters:
    """Mutable usage counters for a registry operation."""

    calls: int = 0
    successes: int = 0
    errors: int = 0
    total_duration: float = 0.0
    last_duration: float | None = None
    last_error: str | None = None
    first_invocation: float | None = None
    last_invocation: float | None = None

    def snapshot(self, name: str) -> Mapping[str, object]:
        avg_duration = self.total_duration / self.successes if self.successes else 0.0
        return {
            "operation": name,
            "calls": self.calls,
            "successes": self.successes,
            "errors": self.errors,
            "total_duration_ms": round(self.total_duration * 1000, 3),
            "avg_duration_ms": round(avg_duration * 1000, 3),
            "last_duration_ms": round((self.last_duration or 0.0) * 1000, 3),
            "first_invocation": _format_timestamp(self.first_invocation),
            "last_invocation": _format_timestamp(self.last_invocation),
            "last_error": self.last_error,
        }

    def reset(self) -> None:
        self.calls = 0
        self.successes = 0
        self.errors = 0
        self.total_duration = 0.0
        self.last_duration = None
        self.last_error = None
        self.first_invocation = None
        self.last_invocation = None


def _format_timestamp(value: float | None) -> str | None:
    if value is None:
        return None
    return datetime.fromtimestamp(value, tz=timezone.utc).isoformat().replace("+00:00", "Z")


_OPERATIONS = (
    "enumerate_installed_apps",
    "find_app_registry_roots",
    "search_registry",
)

_USAGE: Dict[str, _UsageCounters] = {name: _UsageCounters() for name in _OPERATIONS}


@dataclass(frozen=True)
class RegistryRoot:
    """Descriptor pointing at a registry hive path."""

    hive: str
    path: str
    view: str | None = None

    def as_tuple(self) -> tuple[str, str, str | None]:
        return self.hive, self.path, self.view


_ROOT_DESCRIPTOR_RE = re.compile(r"^(HKLM|HKCU)\\(.+)$", re.IGNORECASE)


def parse_registry_root_descriptor(text: str) -> RegistryRoot:
    """Parse a CLI/config-friendly hive path descriptor."""

    value = (text or "").strip()
    if not value:
        raise ValueError("Registry root descriptor must be non-empty")

    segments = [segment.strip() for segment in value.split(",") if segment.strip()]
    base = segments[0].replace("/", "\\")
    match = _ROOT_DESCRIPTOR_RE.match(base)
    if not match:
        raise ValueError(
            "Registry root descriptor must start with HKLM\\ or HKCU\\"
        )

    hive = match.group(1).upper()
    path = match.group(2).strip()
    if not path:
        raise ValueError("Registry root path segment must be non-empty")

    view: str | None = None
    for option in segments[1:]:
        if "=" not in option:
            raise ValueError(
                f"Registry root option '{option}' must be formatted as key=value"
            )
        key, raw_value = option.split("=", 1)
        key = key.strip().lower()
        raw_value = raw_value.strip()
        if key != "view":
            raise ValueError(f"Unsupported registry root option '{key}'")
        if not raw_value:
            raise ValueError("Registry root view must be non-empty when provided")
        normalised = raw_value.upper()
        if normalised == "AUTO":
            view = None
        elif normalised in {"32", "64"}:
            view = normalised
        else:
            raise ValueError("Registry root view must be 32, 64, or auto")

    return RegistryRoot(hive=hive, path=path, view=view)


def _instrument(name: str, func: _Operation) -> _Operation:
    counters = _USAGE[name]

    @wraps(func)
    def wrapper(*args, **kwargs):
        counters.calls += 1
        now = time.time()
        if counters.first_invocation is None:
            counters.first_invocation = now
        counters.last_invocation = now
        start = time.perf_counter()
        try:
            result = func(*args, **kwargs)
        except Exception as exc:
            counters.errors += 1
            duration = time.perf_counter() - start
            counters.total_duration += duration
            counters.last_duration = duration
            counters.last_error = f"{exc.__class__.__name__}: {exc}"
            raise
        else:
            duration = time.perf_counter() - start
            counters.successes += 1
            counters.total_duration += duration
            counters.last_duration = duration
            counters.last_error = None
            return result

    return cast(_Operation, wrapper)


enumerate_installed_apps = _instrument(
    "enumerate_installed_apps", _enumerate_installed_apps
)
find_app_registry_roots = _instrument(
    "find_app_registry_roots", _find_app_registry_roots
)
search_registry = _instrument("search_registry", _search_registry)


def registry_summary(*, reset: bool = False) -> Tuple[Mapping[str, object], ...]:
    """Return usage statistics for the live registry operations.

    Args:
        reset: When ``True`` counters are cleared after creating the snapshot.
    """

    snapshot = tuple(_USAGE[name].snapshot(name) for name in _OPERATIONS)
    if reset:
        for counters in _USAGE.values():
            counters.reset()
    return snapshot


__all__ = [
    "is_windows",
    "RegistryApp",
    "RegistryHit",
    "RegistryRoot",
    "SearchSpec",
    "enumerate_installed_apps",
    "find_app_registry_roots",
    "search_registry",
    "registry_summary",
    "parse_registry_root_descriptor",
]

