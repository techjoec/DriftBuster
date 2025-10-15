"""INI detection heuristics for configuration-style payloads."""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from .registry import register
from ..core.types import DetectionMatch


_SECTION_PATTERN = re.compile(r"^\s*\[([^\]\n]+)\]\s*$", re.MULTILINE)
_KEY_VALUE_PATTERN = re.compile(
    r"^\s*(?P<export>export\s+)?(?P<key>[A-Za-z0-9_.\-]+)\s*(?P<separator>=|:)\s*(?P<value>.*?)(?P<continued>\\\s*)?$",
    re.MULTILINE,
)
_COMMENT_PATTERN = re.compile(r"^\s*[;#!]", re.MULTILINE)
_INI_EXTENSIONS = {".ini", ".cfg", ".cnf", ".conf", ".properties", ".env"}
_DOTENV_FILENAMES = {
    ".env",
    ".env.local",
    ".env.development",
    ".env.production",
    ".env.test",
    ".env.example",
    ".env.sample",
}
_DIRECTIVE_PATTERN = re.compile(
    r"^\s*(?:include|loadmodule|setenv|option|alias)\b", re.IGNORECASE | re.MULTILINE
)
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

        key_matches = list(_KEY_VALUE_PATTERN.finditer(text))
        key_pair_count = len(key_matches)
        if key_pair_count:
            reasons.append("Detected key/value assignments typical of INI-style configuration")
            metadata["key_value_pairs"] = key_pair_count

        equals_pairs = sum(1 for match in key_matches if match.group("separator") == "=")
        colon_pairs = key_pair_count - equals_pairs
        if equals_pairs:
            metadata["equals_separator_pairs"] = equals_pairs
        if colon_pairs:
            metadata["colon_separator_pairs"] = colon_pairs

        lines = text.splitlines()
        non_empty_lines = [line for line in lines if line.strip()]
        comment_lines = [line for line in non_empty_lines if _COMMENT_PATTERN.match(line)]
        directive_lines = [line for line in non_empty_lines if _DIRECTIVE_PATTERN.match(line)]

        continuation_lines = sum(1 for match in key_matches if match.group("continued"))
        export_lines = sum(1 for match in key_matches if match.group("export"))
        if export_lines:
            metadata["export_assignments"] = export_lines
            reasons.append("Detected environment-style export assignments")

        if continuation_lines:
            metadata["continuations"] = continuation_lines

        directive_signal = bool(directive_lines)
        if directive_signal:
            metadata["directive_line_count"] = min(len(directive_lines), 10)
            reasons.append("Found directive keywords common in INI/conf files")

        if extension in _INI_EXTENSIONS:
            reasons.append(f"File extension {extension} suggests INI-like configuration")
        elif lower_name.endswith(".ini"):
            reasons.append("Filename suffix .ini suggests INI content")

        dotenv_hint = lower_name in _DOTENV_FILENAMES
        if dotenv_hint:
            reasons.append("Filename is a known dotenv-style configuration")

        if lower_name == "desktop.ini":
            reasons.append("Filename desktop.ini is commonly produced by Windows shell metadata")
            metadata["profile_hint"] = "desktop.ini"

        comment_signal = bool(comment_lines)
        if comment_signal:
            reasons.append("Detected comment markers (;, #, !) used by INI variants")

        effective_lines = max(len(non_empty_lines) - len(comment_lines), 1)
        key_density = key_pair_count / effective_lines
        metadata["key_density"] = round(key_density, 3)

        key_density_strong = key_density >= 0.3 or key_pair_count >= 4
        extension_hint = extension in _INI_EXTENSIONS or lower_name.endswith(".ini")

        if not (key_pair_count or directive_signal):
            return None

        if not sections and not key_density_strong and not directive_signal and not extension_hint:
            return None

        colon_only_assignments = colon_pairs > 0 and equals_pairs == 0
        if colon_only_assignments and not (
            sections or extension_hint or dotenv_hint or directive_signal or export_lines
        ):
            return None

        signals = 0
        if sections:
            signals += 1
        if key_pair_count:
            signals += 1
        if key_density_strong:
            signals += 1
        if extension_hint or dotenv_hint:
            signals += 1
        if directive_signal:
            signals += 1
        if export_lines:
            signals += 1
        if comment_signal:
            signals += 1

        if signals < 2:
            return None

        confidence = 0.4
        confidence += min(key_density, 0.6) * 0.25
        if sections:
            confidence += 0.2
        if extension_hint:
            confidence += 0.1
        if dotenv_hint:
            confidence += 0.05
        if directive_signal:
            confidence += 0.1
        if export_lines:
            confidence += 0.08
        if comment_signal:
            confidence += 0.05
        if key_pair_count >= 6:
            confidence += 0.05

        confidence = min(confidence, 0.95)

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
