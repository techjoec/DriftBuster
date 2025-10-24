"""Heuristic TOML detector without a TOML parser dependency.

Signals used:
- `.toml` extension strong hint
- `[[table]]` arrays of tables
- `key = value` pairs with dotted keys and quoted strings
- bracketed table headers `[table]` (disambiguated from INI via quotes/arrays)
"""

from __future__ import annotations

import re
from collections import Counter
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
_INLINE_TABLE = re.compile(r"=\s*\{.*?\}")


def _analyse_spacing(lines: List[str]) -> Optional[Dict[str, object]]:
    before_counter: Counter[int] = Counter()
    after_counter: Counter[int] = Counter()
    tab_lines: List[int] = []

    for idx, raw in enumerate(lines, 1):
        if "=" not in raw:
            continue
        stripped = raw.lstrip()
        if not stripped or stripped[0] in "#;[":
            continue
        left, _, right = raw.partition("=")
        if "\t" in left or "\t" in right:
            tab_lines.append(idx)
        before_spaces = len(left) - len(left.rstrip(" "))
        after_spaces = len(right) - len(right.lstrip(" "))
        before_counter[before_spaces] += 1
        after_counter[after_spaces] += 1

    if not before_counter and not after_counter and not tab_lines:
        return None

    metadata: Dict[str, object] = {}
    if before_counter:
        before_base, _ = before_counter.most_common(1)[0]
        metadata["before"] = before_base
        allowed_before = {before_base, max(0, before_base - 1), before_base + 1}
        metadata["allowed_before"] = sorted(allowed_before)
    if after_counter:
        after_base, _ = after_counter.most_common(1)[0]
        metadata["after"] = after_base
        allowed_after = {after_base, max(0, after_base - 1), after_base + 1}
        metadata["allowed_after"] = sorted(allowed_after)
    if tab_lines:
        metadata["tab_lines"] = tab_lines[:10]
    return metadata or None


@dataclass
class TomlPlugin:
    name: str = "toml"
    priority: int = 165
    version: str = "0.0.3"

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
        inline_tables = len(_INLINE_TABLE.findall(text))

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
        if inline_tables:
            reasons.append("Found inline table assignments")

        # Gate on content signals only; treat extension as a confidence hint, not a gate.
        content_signals = sum(
            1
            for flag in (
                has_array_tables,
                has_table_headers,
                has_key_equals,
                quoted_pairs > 0,
                array_pairs > 0,
                inline_tables > 0,
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

        spacing_profile = _analyse_spacing(text.splitlines())
        if spacing_profile:
            metadata["key_value_spacing"] = spacing_profile
            if spacing_profile.get("tab_lines"):
                review_reasons.append("Tab characters around '=' detected in TOML sample")

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
        if inline_tables:
            confidence += 0.03
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
