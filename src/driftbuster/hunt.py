"""Hunt mode helpers for locating dynamic configuration content."""

from __future__ import annotations

import re
from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal, cast, overload

from .formats import format_registry as registry


@dataclass(frozen=True)
class HuntRule:
    """Rule describing how to locate dynamic configuration elements."""

    name: str
    description: str
    token_name: str | None = None
    keywords: tuple[str, ...] = ()
    patterns: tuple[re.Pattern[str] | str, ...] = field(default_factory=tuple)

    def __post_init__(self) -> None:  # pragma: no cover - small coercions
        compiled = []
        for pattern in self.patterns:
            if isinstance(pattern, re.Pattern):
                compiled.append(pattern)
            else:
                compiled.append(re.compile(pattern, re.IGNORECASE | re.MULTILINE))
        object.__setattr__(self, "patterns", tuple(compiled))
        object.__setattr__(self, "keywords", tuple(keyword.lower() for keyword in self.keywords))
        if self.token_name is not None:
            normalised = self.token_name.strip()
            object.__setattr__(self, "token_name", normalised or None)

    @property
    def compiled_patterns(self) -> tuple[re.Pattern[str], ...]:
        # ``__post_init__`` compiles every string pattern, so the tuple only holds ``re.Pattern`` at runtime.
        return cast(tuple[re.Pattern[str], ...], self.patterns)


@dataclass(frozen=True)
class HuntHit:
    rule: HuntRule
    path: Path
    line_number: int
    excerpt: str
    matches: tuple[str, ...] = ()


@dataclass(frozen=True)
class PlanTransform:
    """Suggested token substitution derived from a hunt hit."""

    token_name: str
    value: str
    placeholder: str
    rule_name: str
    path: Path
    line_number: int
    excerpt: str


def _iter_text(path: Path, sample_size: int) -> str | None:
    with path.open("rb") as handle:
        sample = handle.read(sample_size)
        if not registry.looks_text(sample):
            return None
        text, _encoding = registry.decode_text(sample)
        return text


def _matches_keywords(text: str, keywords: Sequence[str]) -> bool:
    return all(keyword in text.lower() for keyword in keywords)


def _deduplicate_preserving_order(values: Iterable[str]) -> tuple[str, ...]:
    seen: set[str] = set()
    ordered: list[str] = []
    for value in values:
        candidate = value.strip()
        if not candidate:
            continue
        if candidate in seen:
            continue
        seen.add(candidate)
        ordered.append(candidate)
    return tuple(ordered)


def _extract_hits(text: str, rule: HuntRule, path: Path) -> list[HuntHit]:
    hits: list[HuntHit] = []
    lines = text.splitlines()
    for idx, line in enumerate(lines, start=1):
        line_lower = line.lower()
        if rule.keywords and not any(keyword in line_lower for keyword in rule.keywords):
            continue
        matched = False
        matched_values: list[str] = []
        if rule.patterns:
            for pattern in rule.compiled_patterns:
                for match in pattern.finditer(line):
                    matched = True
                    group_values: list[str] = []
                    if match.lastindex:
                        for group_index in range(1, match.lastindex + 1):
                            value = match.group(group_index)
                            if value:
                                group_values.append(value.strip())
                    if group_values:
                        matched_values.extend(group_values)
                    whole_match = match.group(0)
                    if whole_match:
                        matched_values.append(whole_match.strip())
            if matched and not matched_values:
                matched_values.append(line.strip())
        else:
            matched = True
            matched_values.append(line.strip())
        if matched:
            excerpt = line.strip()
            hits.append(
                HuntHit(
                    rule=rule,
                    path=path,
                    line_number=idx,
                    excerpt=excerpt,
                    matches=_deduplicate_preserving_order(matched_values),
                )
            )
    return hits


def _should_exclude(
    candidate: Path,
    *,
    relative: Path | None,
    patterns: Sequence[str],
) -> bool:
    for pattern in patterns:
        if candidate.match(pattern):
            return True
        if relative is not None and relative.match(pattern):
            return True
    return False


@overload
def hunt_path(
    root: Path,
    *,
    rules: Sequence[HuntRule],
    glob: str = ...,
    sample_size: int = ...,
    exclude_patterns: Sequence[str] | None = ...,
    return_json: Literal[False] = ...,
    placeholder_template: str = ...,
) -> list[HuntHit]: ...


@overload
def hunt_path(
    root: Path,
    *,
    rules: Sequence[HuntRule],
    glob: str = ...,
    sample_size: int = ...,
    exclude_patterns: Sequence[str] | None = ...,
    return_json: Literal[True],
    placeholder_template: str = ...,
) -> list[dict[str, Any]]: ...


def hunt_path(
    root: Path,
    *,
    rules: Sequence[HuntRule],
    glob: str = "**/*",
    sample_size: int = 128 * 1024,
    exclude_patterns: Sequence[str] | None = None,
    return_json: bool = False,
    placeholder_template: str = "{{{{ {token_name} }}}}",
) -> list[HuntHit] | list[dict[str, Any]]:
    """Search ``root`` for dynamic configuration signals.

    Args:
        root: File or directory to scan.
        rules: Collection of :class:`HuntRule` definitions.
        glob: Optional glob pattern when traversing directories.
        sample_size: Maximum bytes to read from each file.
        exclude_patterns: Optional glob patterns (applied to both absolute and
            relative paths) to skip during traversal.
        return_json: When ``True`` return JSON-ready dictionaries instead of
            :class:`HuntHit` instances.
    """

    path = Path(root)
    targets: list[Path]
    targets = [path] if path.is_file() else [candidate for candidate in path.glob(glob) if candidate.is_file()]

    root_dir = path if path.is_dir() else path.parent
    exclusions: tuple[str, ...] = tuple(exclude_patterns or ())
    findings: list[HuntHit] = []
    for candidate in sorted(targets):
        if exclusions:
            relative: Path | None
            try:
                relative = candidate.relative_to(root_dir)
            except ValueError:
                relative = None
            if _should_exclude(candidate, relative=relative, patterns=exclusions):
                continue
        text = _iter_text(candidate, sample_size)
        if text is None:
            continue
        for rule in rules:
            if rule.keywords and not _matches_keywords(text, rule.keywords):
                continue
            findings.extend(_extract_hits(text, rule, candidate))
    if not return_json:
        return findings

    json_ready: list[dict[str, Any]] = []
    for hit in findings:
        try:
            relative_path = hit.path.relative_to(root_dir)
            relative_text = relative_path.as_posix()
        except ValueError:
            relative_text = hit.path.name
        entry: dict[str, Any] = {
            "rule": {
                "name": hit.rule.name,
                "description": hit.rule.description,
                "token_name": hit.rule.token_name,
                "keywords": hit.rule.keywords,
                "patterns": tuple(pattern.pattern for pattern in hit.rule.compiled_patterns),
            },
            "path": str(hit.path),
            "relative_path": relative_text,
            "line_number": hit.line_number,
            "excerpt": hit.excerpt,
        }
        transform = _plan_transform_for_hit(hit, placeholder_template=placeholder_template)
        if transform is not None:
            entry["metadata"] = {
                "plan_transform": {
                    "token_name": transform.token_name,
                    "value": transform.value,
                    "placeholder": transform.placeholder,
                    "rule_name": transform.rule_name,
                }
            }
        json_ready.append(entry)
    return json_ready


def _plan_transform_for_hit(
    hit: HuntHit,
    *,
    placeholder_template: str,
) -> PlanTransform | None:
    token_name = hit.rule.token_name
    if not token_name:
        return None
    match_value: str | None = None
    if hit.matches:
        for candidate in hit.matches:
            if any(marker in candidate for marker in (".", ":", "/", "\\")):
                match_value = candidate
                break
        if match_value is None:
            match_value = hit.matches[0]
    elif hit.excerpt:
        match_value = hit.excerpt
    if not match_value:
        return None
    try:
        placeholder = placeholder_template.format(token_name=token_name)
    except KeyError as exc:  # pragma: no cover - defensive guard
        raise ValueError("placeholder_template must include {token_name} placeholder") from exc
    return PlanTransform(
        token_name=token_name,
        value=match_value,
        placeholder=placeholder,
        rule_name=hit.rule.name,
        path=hit.path,
        line_number=hit.line_number,
        excerpt=hit.excerpt,
    )


def build_plan_transforms(
    hits: Sequence[HuntHit],
    *,
    placeholder_template: str = "{{{{ {token_name} }}}}",
) -> tuple[PlanTransform, ...]:
    """Return plan transforms derived from ``hits``.

    Each transform pairs a detected ``token_name`` with the matched value and a
    templated placeholder (e.g. ``{{ server_name }}``). Consumers can feed the
    resulting payload into diff plans, token catalog builders, or manual
    approval workflows.
    """

    transforms: list[PlanTransform] = []
    seen: set[tuple[str, str, Path, int]] = set()
    for hit in hits:
        transform = _plan_transform_for_hit(hit, placeholder_template=placeholder_template)
        if transform is None:
            continue
        key = (transform.token_name, transform.value, transform.path, transform.line_number)
        if key in seen:
            continue
        seen.add(key)
        transforms.append(transform)
    return tuple(transforms)


def default_rules() -> tuple[HuntRule, ...]:
    """Return a baseline set of hunt rules for common dynamic settings."""

    return (
        HuntRule(
            name="server-name",
            description="Potential hostnames, server names, or FQDN references",
            token_name="server_name",
            keywords=("server", "host"),
            patterns=(r"\b[a-z0-9_-]+\.(local|lan|corp|com|net|internal)\b",),
        ),
        HuntRule(
            name="certificate-thumbprint",
            description="Likely certificate thumbprints",
            token_name="certificate_thumbprint",
            keywords=("thumbprint", "certificate"),
            patterns=(r"\b[0-9a-f]{40}\b", r"\b[0-9a-f]{64}\b"),
        ),
        HuntRule(
            name="version-number",
            description="Version identifiers (semver style)",
            token_name="version",
            keywords=("version",),
            patterns=(r"\b\d+\.\d+\.\d+(?:\.\d+)?\b",),
        ),
        HuntRule(
            name="install-path",
            description="Suspicious installation or directory paths",
            token_name="install_path",
            keywords=("path", "install"),
            patterns=(r"[A-Za-z]:\\\\[\\w\\-\\.\\s]+", r"/opt/[\w\-\.]+"),
        ),
        HuntRule(
            name="connection-string",
            description="Connection string attribute assignments",
            token_name="connection_string",
            patterns=(r"""connectionstring\s*=\s*['"][^'"\n]+['"]""",),
        ),
        HuntRule(
            name="service-endpoint",
            description="Service endpoint or base address assignments",
            token_name="service_endpoint",
            patterns=(
                r"""<endpoint\b[^>]*\baddress\s*=\s*['"][^'\"]+['"]""",
                r"""(endpoint|serviceurl|baseaddress)\s*=\s*['"][^'\"]+['"]""",
                r"""key\s*=\s*['"][^'\"]*(endpoint|serviceurl|baseaddress|address)[^'\"]*['"][^\n]*value\s*=\s*['"][^'\"]+['"]""",
            ),
        ),
        HuntRule(
            name="feature-flag",
            description="Feature flag or toggle assignments",
            token_name="feature_flag",
            patterns=(
                r"""key\s*=\s*['"][^'\"]*(feature|flag|toggle)[^'\"]*['"][^\n]*value\s*=\s*['"][^'\"]+['"]""",
                r"""<feature\b[^>]*\b(enabled|value)\s*=\s*['"][^'\"]+['"]""",
            ),
        ),
    )


__all__ = [
    "HuntHit",
    "HuntRule",
    "PlanTransform",
    "build_plan_transforms",
    "default_rules",
    "hunt_path",
]
