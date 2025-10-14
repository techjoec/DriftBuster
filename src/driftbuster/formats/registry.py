"""Format plugin registry utilities.

The registry maintains insertion-ordered plugin records and offers immutable
snapshots so callers can reason about plugin ordering without mutating the
backing store.
"""

from __future__ import annotations

import codecs
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Mapping, Optional, Protocol, Tuple

from ..core.types import DetectionMatch


class FormatPlugin(Protocol):
    """Protocol implemented by format plugins."""

    name: str
    priority: int
    version: str

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        ...


@dataclass
class PluginRecord:
    plugin: FormatPlugin


_PLUGINS: List[PluginRecord] = []


def register(plugin: FormatPlugin) -> None:
    """Register a format plugin while keeping names unique.

    Repeated calls with the same plugin instance are treated as idempotent and
    ignored. A different plugin declaring an existing ``plugin.name`` raises a
    :class:`ValueError` so collisions surface during import. Downstream call
    sites should import :class:`Tuple` and :class:`Iterable` from :mod:`typing`
    when annotating registry helpers.
    """

    if not _ensure_unique(plugin):
        return
    _PLUGINS.append(PluginRecord(plugin=plugin))


def get_plugins(*, readonly: bool = True) -> Tuple[FormatPlugin, ...]:
    """Return a tuple snapshot of registered format plugins.

    Args:
        readonly: When ``True`` (default) returns an immutable tuple suitable
            for reuse. ``False`` also returns a tuple but signals that the
            caller plans to copy before mutation.
    """

    # Ensure registration side effects have run.
    from . import xml  # noqa: F401

    plugins = tuple(record.plugin for record in _PLUGINS)
    if readonly:
        return plugins
    # ``readonly`` exists for ergonomic parity with future list views while we
    # keep the snapshot immutable today.
    return tuple(plugin for plugin in plugins)


def registry_summary() -> Tuple[Mapping[str, object], ...]:
    """Return an ordered summary of registered plugins for manual auditing."""

    plugins = get_plugins()
    summary = []
    for index, plugin in enumerate(plugins):
        summary.append(
            {
                "name": plugin.name,
                "version": getattr(plugin, "version", "0.0.0"),
                "priority": plugin.priority,
                "order": index,
                "module": plugin.__class__.__module__,
                "qualname": plugin.__class__.__qualname__,
            }
        )
    return tuple(summary)


def plugin_versions() -> Dict[str, str]:
    """Return a mapping of plugin names to declared versions."""

    return {plugin.name: getattr(plugin, "version", "0.0.0") for plugin in get_plugins()}


def _ensure_unique(plugin: FormatPlugin) -> bool:
    """Validate that ``plugin`` does not duplicate existing registry entries."""

    for record in _PLUGINS:
        existing = record.plugin
        if existing is plugin:
            return False
        if existing.name == plugin.name:
            raise ValueError(
                f"A plugin named {plugin.name!r} is already registered: {existing!r}"
            )
    return True


# Utility helpers shared across plugins.
_ASCII_WHITELIST = {*range(32, 127), 9, 10, 13}


def _strip_bom(sample: bytes) -> tuple[bytes, bool]:
    for bom in (codecs.BOM_UTF8, codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE):
        if bom and sample.startswith(bom):
            return sample[len(bom) :], True
    return sample, False


def _ascii_ratio(data: bytes) -> float:
    if not data:
        return 1.0
    matches = sum(1 for b in data if b in _ASCII_WHITELIST)
    return matches / len(data)


def looks_text(sample: bytes, thresh: float = 0.90) -> bool:
    if not sample:
        return True

    stripped, had_bom = _strip_bom(sample)
    if had_bom:
        return True

    if _ascii_ratio(stripped) >= thresh:
        return True

    if stripped and stripped.count(0) >= len(stripped) // 4:
        even_bytes = stripped[::2]
        odd_bytes = stripped[1::2]
        if _ascii_ratio(even_bytes) >= thresh and odd_bytes.count(0) / max(len(odd_bytes), 1) >= 0.6:
            return True
        if _ascii_ratio(odd_bytes) >= thresh and even_bytes.count(0) / max(len(even_bytes), 1) >= 0.6:
            return True

    return False


def decode_text(sample: bytes) -> Tuple[str, str]:
    """Decode ``sample`` and return the resulting text and codec name."""

    candidates = []
    if sample.startswith(codecs.BOM_UTF8):
        candidates.append("utf-8-sig")
    if sample.startswith(codecs.BOM_UTF16_LE):
        candidates.append("utf-16-le")
    if sample.startswith(codecs.BOM_UTF16_BE):
        candidates.append("utf-16-be")
    candidates.extend(["utf-8", "utf-16-le", "utf-16-be", "latin-1"])

    seen: set[str] = set()
    for encoding in candidates:
        if encoding in seen:
            continue
        seen.add(encoding)
        try:
            text = sample.decode(encoding)
        except UnicodeDecodeError:
            continue
        return text.lstrip("\ufeff"), encoding
    return sample.decode("latin-1", errors="replace"), "latin-1"
