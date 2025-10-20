"""Detect Dockerfiles using directive heuristics.

Signals:
- Filename contains 'Dockerfile' or has .Dockerfile suffix
- First non-comment line starts with FROM
- Presence of common directives: RUN, COPY, ADD, ARG, ENV, WORKDIR, ENTRYPOINT
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from ..format_registry import register
from ...core.types import DetectionMatch


_FIRST_FROM = re.compile(r"^\s*FROM\s+\S+", re.IGNORECASE)
_DIRECTIVES = re.compile(r"^\s*(RUN|COPY|ADD|ARG|ENV|WORKDIR|ENTRYPOINT|CMD|EXPOSE|USER|VOLUME)\b", re.IGNORECASE | re.MULTILINE)


@dataclass
class DockerfilePlugin:
    name: str = "dockerfile"
    priority: int = 120
    version: str = "0.0.1"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        lower = path.name.lower()
        reasons: List[str] = []
        metadata: Dict[str, object] = {}

        name_hint = "dockerfile" in lower or lower.endswith(".dockerfile")
        if name_hint:
            reasons.append("Filename suggests a Dockerfile")

        # strip leading comments/blank lines
        lines = text.splitlines()
        first_idx = 0
        while first_idx < len(lines) and (not lines[first_idx].strip() or lines[first_idx].lstrip().startswith("#")):
            first_idx += 1
        first_line = lines[first_idx] if first_idx < len(lines) else ""

        has_from = bool(_FIRST_FROM.search(first_line))
        if has_from:
            reasons.append("First non-comment line starts with FROM")
        has_directives = bool(_DIRECTIVES.search(text))
        if has_directives:
            reasons.append("Found common Dockerfile directives (RUN/COPY/ARG)")

        signals = sum(1 for flag in (name_hint, has_from, has_directives) if flag)
        if signals < 2:
            return None

        confidence = 0.6
        if name_hint:
            confidence += 0.1
        if has_from:
            confidence += 0.15
        if has_directives:
            confidence += 0.1
        confidence = min(0.95, confidence)

        return DetectionMatch(
            plugin_name=self.name,
            format_name="dockerfile",
            variant="generic",
            confidence=confidence,
            reasons=reasons or ["Dockerfile heuristics matched"],
            metadata=metadata or None,
        )


register(DockerfilePlugin())

