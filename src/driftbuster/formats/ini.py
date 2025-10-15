"""INI detection heuristics for configuration-style payloads."""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from .registry import register
from ..core.types import DetectionMatch


_SECTION_PATTERN = re.compile(r"^\s*\[([^\]\n]+)\]\s*$", re.MULTILINE)
_KEY_VALUE_PATTERN = re.compile(r"^\s*([A-Za-z0-9_.\-]+)\s*=\s*([^\n]*)$", re.MULTILINE)
_COMMENT_PATTERN = re.compile(r"^\s*[;#]", re.MULTILINE)
_INI_EXTENSIONS = {".ini", ".cfg", ".cnf"}
_MAX_SECTION_SNAPSHOT = 10


@dataclass
class IniPlugin:
    """Detect classic INI-style configuration files."""

    name: str = "ini"
    priority: int = 170
    version: str = "0.0.1"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        lower_name = path.name.lower()
        extension = path.suffix.lower()

        reasons: List[str] = []
        metadata: Dict[str, object] = {}

        sections = [section.strip() for section in _SECTION_PATTERN.findall(text) if section.strip()]
        if sections:
            reasons.append("Found [section] headers indicative of INI structure")
            unique_sections = []
            seen = set()
            for section in sections:
                key = section.lower()
                if key in seen:
                    continue
                seen.add(key)
                unique_sections.append(section)
            metadata["sections"] = unique_sections[:_MAX_SECTION_SNAPSHOT]
            metadata["section_count"] = len(unique_sections)

        key_pairs = _KEY_VALUE_PATTERN.findall(text)
        if key_pairs:
            reasons.append("Detected key=value assignments typical of INI configuration")
            metadata["key_value_pairs"] = len(key_pairs)

        if not (sections and key_pairs):
            return None

        if extension in _INI_EXTENSIONS:
            reasons.append(f"File extension {extension} suggests INI content")
        elif lower_name.endswith(".ini"):
            reasons.append("Filename suffix .ini suggests INI content")

        if lower_name == "desktop.ini":
            reasons.append("Filename desktop.ini is commonly produced by Windows shell metadata")
            metadata["profile_hint"] = "desktop.ini"

        comment_signal = bool(_COMMENT_PATTERN.search(text))
        if comment_signal:
            reasons.append("Detected ; or # comment markers used by INI files")

        signals = 0
        if sections:
            signals += 1
        if key_pairs:
            signals += 1
        if extension in _INI_EXTENSIONS or lower_name.endswith(".ini"):
            signals += 1
        if lower_name == "desktop.ini":
            signals += 1
        if comment_signal:
            signals += 1

        if signals < 2:
            return None

        confidence = 0.5
        if sections:
            confidence += 0.15
        if key_pairs:
            confidence += 0.15
        if extension in _INI_EXTENSIONS or lower_name.endswith(".ini"):
            confidence += 0.1
        if lower_name == "desktop.ini":
            confidence += 0.05
        if metadata.get("key_value_pairs", 0) >= 5:
            confidence += 0.05

        confidence = min(confidence, 0.92)

        variant: Optional[str] = None
        if lower_name == "desktop.ini":
            variant = "desktop-ini"

        return DetectionMatch(
            plugin_name=self.name,
            format_name="ini",
            variant=variant,
            confidence=confidence,
            reasons=reasons,
            metadata=metadata or None,
        )


register(IniPlugin())
