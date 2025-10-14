"""Utility helpers for masking sensitive tokens in reporting outputs."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Iterable, Mapping, MutableMapping, Sequence


@dataclass
class RedactionFilter:
    """Replace known tokens with a placeholder while tracking usage counts."""

    tokens: Sequence[str] = ()
    placeholder: str = "[REDACTED]"
    _hits: MutableMapping[str, int] = field(default_factory=dict, init=False, repr=False)
    _ordered_tokens: Sequence[str] = field(default_factory=tuple, init=False, repr=False)

    def __post_init__(self) -> None:
        unique = [token for token in dict.fromkeys(self.tokens) if token]
        unique.sort(key=len, reverse=True)
        self._ordered_tokens = tuple(unique)

    def apply(self, text: str) -> str:
        """Return ``text`` with each configured token replaced by ``placeholder``."""

        if not self._ordered_tokens or not text:
            return text
        result = text
        for token in self._ordered_tokens:
            occurrences = result.count(token)
            if not occurrences:
                continue
            self._hits[token] = self._hits.get(token, 0) + occurrences
            result = result.replace(token, self.placeholder)
        return result

    def stats(self) -> Mapping[str, int]:
        """Return a read-only view of the redaction counts."""

        return dict(self._hits)

    @property
    def has_hits(self) -> bool:
        return bool(self._hits)

    def reset(self) -> None:
        self._hits.clear()


def redact_data(data: Any, redactor: RedactionFilter) -> Any:
    """Recursively apply ``redactor`` to strings within ``data``."""

    if isinstance(data, str):
        return redactor.apply(data)
    if isinstance(data, Mapping):
        return {str(key): redact_data(value, redactor) for key, value in data.items()}
    if isinstance(data, list):
        return [redact_data(item, redactor) for item in data]
    if isinstance(data, tuple):
        return tuple(redact_data(item, redactor) for item in data)
    if isinstance(data, set):
        return {redact_data(item, redactor) for item in data}
    if isinstance(data, Iterable) and not isinstance(data, (str, bytes, bytearray)):
        return [redact_data(item, redactor) for item in data]
    return data


def resolve_redactor(
    *,
    redactor: RedactionFilter | None = None,
    mask_tokens: Sequence[str] | None = None,
    placeholder: str = "[REDACTED]",
) -> RedactionFilter | None:
    """Return a configured :class:`RedactionFilter` for the provided arguments."""

    if redactor and mask_tokens:
        raise ValueError("Provide either an explicit redactor or mask_tokens, not both.")
    if redactor:
        return redactor
    if mask_tokens:
        return RedactionFilter(tokens=mask_tokens, placeholder=placeholder)
    return None
