"""Hunt mode helpers for locating dynamic configuration content."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, List, Optional, Sequence, Tuple

from .formats import registry


@dataclass(frozen=True)
class HuntRule:
    """Rule describing how to locate dynamic configuration elements."""

    name: str
    description: str
    token_name: Optional[str] = None
    keywords: Tuple[str, ...] = ()
    patterns: Tuple[re.Pattern[str] | str, ...] = field(default_factory=tuple)

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


@dataclass(frozen=True)
class HuntHit:
    rule: HuntRule
    path: Path
    line_number: int
    excerpt: str


def _iter_text(path: Path, sample_size: int) -> Optional[str]:
    with path.open("rb") as handle:
        sample = handle.read(sample_size)
        if not registry.looks_text(sample):
            return None
        text, _encoding = registry.decode_text(sample)
        return text


def _matches_keywords(text: str, keywords: Sequence[str]) -> bool:
    return all(keyword in text.lower() for keyword in keywords)


def _extract_hits(text: str, rule: HuntRule, path: Path) -> List[HuntHit]:
    hits: List[HuntHit] = []
    lines = text.splitlines()
    for idx, line in enumerate(lines, start=1):
        line_lower = line.lower()
        if rule.keywords and not any(keyword in line_lower for keyword in rule.keywords):
            continue
        matched = False
        if rule.patterns:
            for pattern in rule.patterns:
                if pattern.search(line):
                    matched = True
                    break
        else:
            matched = True
        if matched:
            excerpt = line.strip()
            hits.append(HuntHit(rule=rule, path=path, line_number=idx, excerpt=excerpt))
    return hits


def _should_exclude(
    candidate: Path,
    *,
    relative: Optional[Path],
    patterns: Sequence[str],
) -> bool:
    for pattern in patterns:
        if candidate.match(pattern):
            return True
        if relative is not None and relative.match(pattern):
            return True
    return False


def hunt_path(
    root: Path,
    *,
    rules: Sequence[HuntRule],
    glob: str = "**/*",
    sample_size: int = 128 * 1024,
    exclude_patterns: Optional[Sequence[str]] = None,
    return_json: bool = False,
) -> List[HuntHit] | List[dict[str, Any]]:
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
    targets: List[Path]
    if path.is_file():
        targets = [path]
    else:
        targets = [candidate for candidate in path.glob(glob) if candidate.is_file()]

    root_dir = path if path.is_dir() else path.parent
    exclusions: Tuple[str, ...] = tuple(exclude_patterns or ())
    findings: List[HuntHit] = []
    for candidate in sorted(targets):
        if exclusions:
            relative: Optional[Path]
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

    json_ready: List[dict[str, Any]] = []
    for hit in findings:
        try:
            relative_path = hit.path.relative_to(root_dir)
            relative_text = relative_path.as_posix()
        except ValueError:
            relative_text = hit.path.name
        json_ready.append(
            {
                "rule": {
                    "name": hit.rule.name,
                    "description": hit.rule.description,
                    "token_name": hit.rule.token_name,
                    "keywords": hit.rule.keywords,
                    "patterns": tuple(pattern.pattern for pattern in hit.rule.patterns),
                },
                "path": str(hit.path),
                "relative_path": relative_text,
                "line_number": hit.line_number,
                "excerpt": hit.excerpt,
            }
        )
    return json_ready


def default_rules() -> Tuple[HuntRule, ...]:
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
            name="config-appsetting",
            description="AppSettings key/value pairs inside .config files",
            token_name="app_setting_value",
            keywords=("<add", "key=", "value="),
            patterns=(
                r"<add\s+[^>]*\bkey\s*=\s*\"[^\"]+\"[^>]*\bvalue\s*=\s*\"[^\"]+\"",
                r"<add\s+[^>]*\bvalue\s*=\s*\"[^\"]+\"[^>]*\bkey\s*=\s*\"[^\"]+\"",
            ),
        ),
        HuntRule(
            name="config-connection-string",
            description="ConnectionString attributes inside .config files",
            token_name="connection_string",
            keywords=("connectionstring",),
            patterns=(
                r"<add\s+[^>]*\bconnectionString\s*=\s*\"[^\"]+\"",
            ),
        ),
        HuntRule(
            name="install-path",
            description="Suspicious installation or directory paths",
            token_name="install_path",
            keywords=("path", "install"),
            patterns=(r"[A-Za-z]:\\\\[\\w\\-\\.\\s]+", r"/opt/[\w\-\.]+"),
        ),
    )


__all__ = ["HuntRule", "HuntHit", "default_rules", "hunt_path"]
