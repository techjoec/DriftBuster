"""Token approval helpers and candidate collection pipeline.

This module bridges hunt output with the token approval log surfaced in
``notes/checklists/token-approval.md``. It keeps the data structures JSON
serialisable so manual workflows can persist evidence without leaking raw
secrets. The approval store intentionally focuses on deterministic matching so
future automation can layer additional metadata (e.g. catalogue variants or
secure storage references) without breaking compatibility.
"""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
from typing import Any, Iterable, Mapping, MutableMapping, Sequence

from .hunt import PlanTransform

_ApprovalKey = tuple[str, str, str | None]


def _hash_excerpt(excerpt: str | None) -> str | None:
    if not excerpt:
        return None
    digest = hashlib.sha256(excerpt.encode("utf-8")).hexdigest()
    return digest


def _normalise_optional_str(value: Any) -> str | None:
    if isinstance(value, str):
        stripped = value.strip()
        if stripped:
            return stripped
    return None


@dataclass(frozen=True)
class TokenApproval:
    """Represents an approved token placeholder entry."""

    token_name: str
    placeholder: str
    excerpt_hash: str | None = None
    source_path: str | None = None
    catalog_variant: str | None = None
    sample_hash: str | None = None
    approved_by: str | None = None
    approved_at_utc: str | None = None
    expires_at_utc: str | None = None
    secure_location: str | None = None
    notes: str | None = None

    @classmethod
    def from_mapping(cls, payload: Mapping[str, Any]) -> "TokenApproval":
        token_name = _normalise_optional_str(payload.get("token_name"))
        placeholder = _normalise_optional_str(payload.get("placeholder"))
        if not token_name or not placeholder:
            raise ValueError("token_name and placeholder are required for TokenApproval")
        return cls(
            token_name=token_name,
            placeholder=placeholder,
            excerpt_hash=_normalise_optional_str(payload.get("excerpt_hash")),
            source_path=_normalise_optional_str(payload.get("source_path")),
            catalog_variant=_normalise_optional_str(payload.get("catalog_variant")),
            sample_hash=_normalise_optional_str(payload.get("sample_hash")),
            approved_by=_normalise_optional_str(payload.get("approved_by")),
            approved_at_utc=_normalise_optional_str(payload.get("approved_at_utc")),
            expires_at_utc=_normalise_optional_str(payload.get("expires_at_utc")),
            secure_location=_normalise_optional_str(payload.get("secure_location")),
            notes=_normalise_optional_str(payload.get("notes")),
        )

    def to_mapping(self) -> Mapping[str, Any]:
        payload: MutableMapping[str, Any] = {
            "token_name": self.token_name,
            "placeholder": self.placeholder,
        }
        optional_fields = {
            "excerpt_hash": self.excerpt_hash,
            "source_path": self.source_path,
            "catalog_variant": self.catalog_variant,
            "sample_hash": self.sample_hash,
            "approved_by": self.approved_by,
            "approved_at_utc": self.approved_at_utc,
            "expires_at_utc": self.expires_at_utc,
            "secure_location": self.secure_location,
            "notes": self.notes,
        }
        for key, value in optional_fields.items():
            if value is not None:
                payload[key] = value
        return payload


class TokenApprovalStore:
    """In-memory index of approved token placeholders."""

    def __init__(self, approvals: Iterable[TokenApproval] | None = None) -> None:
        self._entries: list[TokenApproval] = []
        self._index: dict[_ApprovalKey, int] = {}
        if approvals:
            for approval in approvals:
                self.add(approval)

    @staticmethod
    def _build_key(token_name: str, placeholder: str, excerpt_hash: str | None) -> _ApprovalKey:
        return (token_name, placeholder, excerpt_hash)

    def add(self, approval: TokenApproval) -> None:
        key = self._build_key(approval.token_name, approval.placeholder, approval.excerpt_hash)
        existing = self._index.get(key)
        if existing is None:
            self._index[key] = len(self._entries)
            self._entries.append(approval)
        else:
            self._entries[existing] = approval

    def find(self, *, token_name: str, placeholder: str, excerpt_hash: str | None) -> TokenApproval | None:
        key = self._build_key(token_name, placeholder, excerpt_hash)
        index = self._index.get(key)
        if index is None:
            return None
        return self._entries[index]

    def entries(self) -> tuple[TokenApproval, ...]:
        return tuple(self._entries)

    def to_json_payload(self) -> list[Mapping[str, Any]]:
        return [entry.to_mapping() for entry in self._entries]

    def dump(self, path: Path, *, indent: int = 2) -> None:
        payload = self.to_json_payload()
        text = json.dumps(payload, indent=indent)
        suffix = "\n" if not text.endswith("\n") else ""
        path.write_text(text + suffix, encoding="utf-8")

    @classmethod
    def from_json_payload(cls, payload: Sequence[Mapping[str, Any]] | Sequence[Any]) -> "TokenApprovalStore":
        approvals: list[TokenApproval] = []
        for entry in payload:
            if isinstance(entry, Mapping):
                approvals.append(TokenApproval.from_mapping(entry))
        return cls(approvals)

    @classmethod
    def load(cls, path: Path) -> "TokenApprovalStore":
        text = path.read_text(encoding="utf-8")
        payload = json.loads(text)
        if not isinstance(payload, Sequence):
            raise ValueError("Approval log must be a JSON array")
        return cls.from_json_payload(payload)


@dataclass(frozen=True)
class TokenCandidate:
    """Represents a token extracted from hunt output awaiting approval."""

    token_name: str
    value: str
    placeholder: str
    rule_name: str
    source_path: str
    relative_path: str | None
    line_number: int | None
    excerpt: str | None
    excerpt_hash: str | None
    catalog_variant: str | None
    sample_hash: str | None
    approval: TokenApproval | None


@dataclass(frozen=True)
class TokenCandidateSet:
    """Collection of approved and pending token candidates."""

    pending: tuple[TokenCandidate, ...]
    approved: tuple[TokenCandidate, ...]


def _coerce_line_number(value: Any) -> int | None:
    if isinstance(value, int):
        return value
    if isinstance(value, str) and value.strip().isdigit():
        return int(value.strip())
    return None


def _normalise_mapping_entry(entry: Mapping[str, Any]) -> TokenCandidate | None:
    rule = entry.get("rule")
    if not isinstance(rule, Mapping):
        return None
    metadata = entry.get("metadata")
    plan_transform: Mapping[str, Any] | None = None
    if isinstance(metadata, Mapping):
        plan_transform = metadata.get("plan_transform") if isinstance(metadata.get("plan_transform"), Mapping) else None
    if plan_transform is None:
        return None

    token_name = _normalise_optional_str(plan_transform.get("token_name") or rule.get("token_name"))
    value = _normalise_optional_str(plan_transform.get("value"))
    placeholder = _normalise_optional_str(plan_transform.get("placeholder"))
    rule_name = _normalise_optional_str(plan_transform.get("rule_name") or rule.get("name"))
    if not token_name or not value or not placeholder or not rule_name:
        return None

    source_path = _normalise_optional_str(entry.get("path")) or _normalise_optional_str(entry.get("relative_path"))
    if not source_path:
        source_path = token_name

    relative_path = _normalise_optional_str(entry.get("relative_path"))
    excerpt = _normalise_optional_str(entry.get("excerpt"))
    catalog_variant = None
    sample_hash = None
    if isinstance(metadata, Mapping):
        catalog_variant = _normalise_optional_str(metadata.get("catalog_variant"))
        sample_hash = _normalise_optional_str(metadata.get("sample_hash"))

    excerpt_hash = _hash_excerpt(excerpt)

    return TokenCandidate(
        token_name=token_name,
        value=value,
        placeholder=placeholder,
        rule_name=rule_name,
        source_path=source_path,
        relative_path=relative_path,
        line_number=_coerce_line_number(entry.get("line_number")),
        excerpt=excerpt,
        excerpt_hash=excerpt_hash,
        catalog_variant=catalog_variant,
        sample_hash=sample_hash,
        approval=None,
    )


def _normalise_transform(transform: PlanTransform) -> TokenCandidate:
    excerpt = transform.excerpt if _normalise_optional_str(transform.excerpt) else None
    excerpt_hash = _hash_excerpt(excerpt)
    return TokenCandidate(
        token_name=transform.token_name,
        value=transform.value,
        placeholder=transform.placeholder,
        rule_name=transform.rule_name,
        source_path=str(transform.path),
        relative_path=str(transform.path),
        line_number=transform.line_number,
        excerpt=excerpt,
        excerpt_hash=excerpt_hash,
        catalog_variant=None,
        sample_hash=None,
        approval=None,
    )


def collect_token_candidates(
    hunts: Iterable[Mapping[str, Any] | PlanTransform],
    *,
    approvals: TokenApprovalStore | None = None,
) -> TokenCandidateSet:
    """Return approved and pending tokens extracted from ``hunts``.

    ``hunts`` may contain the JSON dictionaries returned by
    :func:`driftbuster.hunt.hunt_path(..., return_json=True)` or
    :class:`~driftbuster.hunt.PlanTransform` instances. Entries without a
    ``token_name`` or placeholder are ignored.
    """

    approvals = approvals or TokenApprovalStore()
    pending: list[TokenCandidate] = []
    approved: list[TokenCandidate] = []
    seen: set[tuple[str, str, str | None, str | None]] = set()

    for entry in hunts:
        candidate: TokenCandidate | None
        if isinstance(entry, PlanTransform):
            candidate = _normalise_transform(entry)
        elif isinstance(entry, Mapping):
            candidate = _normalise_mapping_entry(entry)
        else:
            continue
        if candidate is None:
            continue

        key = (
            candidate.token_name,
            candidate.placeholder,
            candidate.excerpt_hash,
            candidate.relative_path,
        )
        if key in seen:
            continue
        seen.add(key)

        approval = approvals.find(
            token_name=candidate.token_name,
            placeholder=candidate.placeholder,
            excerpt_hash=candidate.excerpt_hash,
        )
        enriched = TokenCandidate(
            token_name=candidate.token_name,
            value=candidate.value,
            placeholder=candidate.placeholder,
            rule_name=candidate.rule_name,
            source_path=candidate.source_path,
            relative_path=candidate.relative_path,
            line_number=candidate.line_number,
            excerpt=candidate.excerpt,
            excerpt_hash=candidate.excerpt_hash,
            catalog_variant=candidate.catalog_variant,
            sample_hash=candidate.sample_hash,
            approval=approval,
        )
        if approval is None:
            pending.append(enriched)
        else:
            approved.append(enriched)

    return TokenCandidateSet(pending=tuple(pending), approved=tuple(approved))


__all__ = [
    "TokenApproval",
    "TokenApprovalStore",
    "TokenCandidate",
    "TokenCandidateSet",
    "collect_token_candidates",
]
