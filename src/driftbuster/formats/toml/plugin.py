"""Heuristic TOML detector without a TOML parser dependency.

Signals used:
- `.toml` extension strong hint
- `[[table]]` arrays of tables
- `key = value` pairs with dotted keys and quoted strings
- bracketed table headers `[table]` (disambiguated from INI via quotes/arrays)
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from ..format_registry import register
from ...core.types import DetectionMatch


_EXT = ".toml"
_TABLE_HEADER = re.compile(r"^\s*\[[A-Za-z0-9_.\-]+\]\s*$", re.MULTILINE)
_ARRAY_OF_TABLES = re.compile(r"^\s*\[\[[A-Za-z0-9_.\-]+\]\]\s*$", re.MULTILINE)
_KEY_EQUALS = re.compile(r"^\s*[A-Za-z0-9_.\-]+\s*=\s*.+$", re.MULTILINE)
_QUOTED_VALUE = re.compile(r"=\s*(\"[^\"]*\"|'[^']*')")
_ARRAY_VALUE = re.compile(r"=\s*\[.*?\]", re.DOTALL)


@dataclass
class TomlPlugin:
    name: str = "toml"
    priority: int = 165
    version: str = "0.0.2"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        ext = path.suffix.lower()
        reasons: List[str] = []
        metadata: Dict[str, object] = {}
        review_reasons: List[str] = []

        if ext == _EXT:
            reasons.append("File extension .toml suggests TOML content")

        has_array_tables = bool(_ARRAY_OF_TABLES.search(text))
        has_table_headers = bool(_TABLE_HEADER.search(text))
        key_pairs = _KEY_EQUALS.findall(text)
        has_key_equals = bool(key_pairs)
        quoted_pairs = len(_QUOTED_VALUE.findall(text))
        array_pairs = len(_ARRAY_VALUE.findall(text))

        if has_array_tables:
            reasons.append("Found [[array-of-tables]] declaration")
        if has_table_headers:
            reasons.append("Found [table] headers typical of TOML")
        if has_key_equals:
            reasons.append("Detected key = value assignments")
        if quoted_pairs:
            reasons.append("Found quoted value assignments")
        if array_pairs:
            reasons.append("Found array value assignments")

        # Gate on content signals only; treat extension as a confidence hint, not a gate.
        content_signals = sum(
            1
            for flag in (
                has_array_tables,
                has_table_headers,
                has_key_equals,
                quoted_pairs > 0,
                array_pairs > 0,
            )
            if flag
        )
        if content_signals < 2:
            return None

        variant = "array-of-tables" if has_array_tables else "generic"

        # Oddities: suspect trailing commas in arrays or lines missing '=' where expected
        if re.search(r",\s*\]", text):
            review_reasons.append("Array with trailing comma before closing bracket")
        # lines that look like bare keys without '=' (risky, keep conservative)
        bare_key_lines = [ln for ln in text.splitlines()[:500] if ln.strip() and not ln.lstrip().startswith(('#',';','[')) and ('=' not in ln) and (':' not in ln)]
        if len(bare_key_lines) >= 3 and has_table_headers is False:
            review_reasons.append("Multiple bare key lines without '=' suggest malformed TOML")

        confidence = 0.5
        # Extension contributes as a hint only.
        if ext == _EXT:
            confidence += 0.12
        if has_array_tables:
            confidence += 0.15
        if has_table_headers:
            confidence += 0.1
        if has_key_equals:
            confidence += 0.05
        if quoted_pairs or array_pairs:
            confidence += 0.05
        confidence = min(0.95, confidence)

        if review_reasons:
            metadata["needs_review"] = True
            metadata["review_reasons"] = review_reasons

        return DetectionMatch(
            plugin_name=self.name,
            format_name="toml",
            variant=variant,
            confidence=confidence,
            reasons=reasons or ["Heuristics indicate TOML"],
            metadata=metadata or None,
        )


register(TomlPlugin())
