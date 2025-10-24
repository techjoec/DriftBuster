"""JSON detection heuristics for configuration-oriented payloads.

The detector focuses on lightweight, sampling-friendly checks so auditors can
reason about matches without executing full JSON parsing on large files. It
combines filename/extension cues with structure hints (top-level braces,
balanced pairs, key/value markers) and optional metadata derived from a best
effort parse when the sample is complete. Comment handling is conservative: we
detect ``//`` and ``/* */`` usage outside of string literals to surface a
``jsonc`` variant while keeping parsing attempts limited to comment-free input.

Variants surfaced today:

``structured-settings-json``
    Triggered for ``appsettings*.json`` style files or when the payload exposes
    well-known ASP.NET configuration keys such as ``Logging`` or
    ``ConnectionStrings``. Provides predictable metadata for profile/hunt
    alignment.

``jsonc``
    Activated when inline or block comments are detected outside of strings.
    The metadata records that comments were encountered so downstream tooling
    can retain redaction behaviour when re-serialising payloads.

``generic``
    Default classification for raw JSON payloads when no specialised variant
    applies.
"""

from __future__ import annotations

import json as json_lib
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

from ..format_registry import register
from ...core.types import DetectionMatch

_STRUCTURED_FILENAMES: Sequence[str] = (
    "appsettings.json",
    "appsettings.development.json",
    "appsettings.production.json",
    "appsettings.staging.json",
)

_ANALYSIS_WINDOW_CLAMP = 200_000


@dataclass
class JsonPlugin:
    """Detect JSON and JSON-with-comments configuration files."""

    name: str = "json"
    priority: int = 200
    version: str = "0.0.3"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        filename = path.name
        lower_name = filename.lower()
        extension = path.suffix.lower()
        reasons: List[str] = []
        metadata: Dict[str, Any] = {}
        review_reasons: List[str] = []

        is_json_extension = extension in {".json", ".jsonc"} or lower_name.endswith(".json")
        if is_json_extension:
            reasons.append(f"File extension {extension or '.json'} suggests JSON content")

        stripped, had_leading_comments = self._strip_leading_comments(text)
        if not stripped:
            return None

        analysis_text, truncated = self._prepare_analysis_window(stripped)

        first_char = analysis_text[0]
        if first_char in "{[":
            top_level_type = "object" if first_char == "{" else "array"
            metadata["top_level_type"] = top_level_type
            reasons.append(f"Detected JSON {top_level_type} opening token {first_char!r}")
        else:
            if not is_json_extension:
                return None
            metadata["top_level_type"] = "unknown"

        has_comments = had_leading_comments or self._contains_comments(analysis_text)
        if has_comments:
            metadata["has_comments"] = True
            reasons.append("Detected comment tokens outside string literals")

        key_signal = self._has_key_value_marker(analysis_text)
        if key_signal:
            reasons.append("Found key/value signature indicative of JSON objects")

        balanced = self._balanced_pairs(analysis_text)
        if balanced:
            reasons.append("Curly/array delimiters appear balanced in sampled content")

        structured_hint = self._is_structured_settings(lower_name, analysis_text)
        if structured_hint:
            metadata["settings_hint"] = structured_hint
            reasons.append("Matched appsettings-style configuration cues")

        environment = self._extract_appsettings_environment(lower_name)
        if environment is not None:
            metadata["settings_environment"] = environment

        parse_candidate = analysis_text
        parsed_via_comment_strip = False
        if has_comments:
            cleaned, removed = self._strip_json_comments(analysis_text)
            if removed:
                parse_candidate = cleaned
                parsed_via_comment_strip = True
        allow_comments_flag = has_comments and not parsed_via_comment_strip
        parse_result = self._attempt_parse(parse_candidate, allow_comments=allow_comments_flag)
        if parse_result.success:
            metadata.update(parse_result.metadata)
            reasons.append("Parsed JSON payload without errors within sample")
            if parsed_via_comment_strip:
                metadata["parsed_with_comment_stripping"] = True
        else:
            # Parsing failed despite JSON-like signals; flag for review.
            if (
                (first_char in "[{" or key_signal or balanced)
                and not has_comments
                and not truncated
            ):
                metadata["parse_failed"] = True
                review_reasons.append("JSON parse failed under sample")

        # Gate detection on content signals only; extension is a confidence hint, not a gate.
        content_signals = sum(
            1
            for flag in (
                metadata.get("top_level_type") in {"object", "array"},
                key_signal,
                balanced,
                parse_result.success,
            )
            if flag
        )

        if content_signals < 2:
            # Allow minimal detection for known JSON extensions even if content
            # signals are weak, to align with real-world appsettings-style files
            # that may be tiny or truncated in samples.
            if not (is_json_extension and stripped):
                return None

        # For non-JSON extensions, require at least a key/value marker or a
        # successful parse to avoid over-eager matches on brace-like text.
        if not is_json_extension and not (parse_result.success or key_signal):
            return None

        variant: Optional[str]
        if structured_hint:
            variant = "structured-settings-json"
        elif has_comments or extension == ".jsonc":
            variant = "jsonc"
        else:
            variant = "generic"

        confidence = 0.55
        # Extension contributes as a hint only.
        if is_json_extension:
            confidence += 0.1
        if metadata.get("top_level_type") in {"object", "array"}:
            confidence += 0.1
        if key_signal:
            confidence += 0.05
        if balanced:
            confidence += 0.05
        if parse_result.success:
            confidence += 0.15
        if structured_hint:
            confidence += 0.07
        if has_comments and variant == "jsonc":
            confidence += 0.03
        if parsed_via_comment_strip:
            confidence += 0.02

        confidence = min(0.95, confidence)

        if truncated:
            metadata["analysis_window_truncated"] = True
            metadata["analysis_window_chars"] = len(analysis_text)

        if review_reasons:
            metadata["needs_review"] = True
            metadata["review_reasons"] = review_reasons

        return DetectionMatch(
            plugin_name=self.name,
            format_name="json",
            variant=variant,
            confidence=confidence,
            reasons=reasons,
            metadata=metadata or None,
        )

    def _strip_leading_comments(self, text: str) -> tuple[str, bool]:
        working = text.lstrip("\ufeff")
        consumed = False
        index = 0
        length = len(working)
        while True:
            while index < length and working[index] in " \t\r\n":
                index += 1
            if index + 1 < length and working[index:index + 2] == "//":
                consumed = True
                newline = working.find("\n", index + 2)
                if newline == -1:
                    return "", consumed
                index = newline + 1
                continue
            if index + 1 < length and working[index:index + 2] == "/*":
                consumed = True
                end = working.find("*/", index + 2)
                if end == -1:
                    return "", consumed
                index = end + 2
                continue
            break
        return working[index:], consumed

    def _prepare_analysis_window(self, text: str) -> Tuple[str, bool]:
        if len(text) <= _ANALYSIS_WINDOW_CLAMP:
            return text, False
        return text[:_ANALYSIS_WINDOW_CLAMP], True

    def _contains_comments(self, text: str) -> bool:
        in_string = False
        escape = False
        i = 0
        length = len(text)
        while i < length:
            char = text[i]
            if in_string:
                if escape:
                    escape = False
                elif char == "\\":
                    escape = True
                elif char == '"':
                    in_string = False
                i += 1
                continue
            if char == '"':
                in_string = True
                i += 1
                continue
            if char == "/" and i + 1 < length:
                nxt = text[i + 1]
                if nxt in {"/", "*"}:
                    return True
            i += 1
        return False

    def _has_key_value_marker(self, text: str) -> bool:
        depth = 0
        in_string = False
        escape = False
        for char in text[:10_000]:
            if in_string:
                if escape:
                    escape = False
                elif char == "\\":
                    escape = True
                elif char == '"':
                    in_string = False
                continue
            if char == '"':
                in_string = True
                continue
            if char == "{":
                depth += 1
                continue
            if char == "}":
                if depth > 0:
                    depth -= 1
                continue
            if depth == 1 and char == ":":
                return True
        return False

    def _balanced_pairs(self, text: str) -> bool:
        open_braces = text.count("{")
        close_braces = text.count("}")
        open_brackets = text.count("[")
        close_brackets = text.count("]")
        return abs(open_braces - close_braces) <= 1 and abs(open_brackets - close_brackets) <= 1

    def _is_structured_settings(self, filename: str, text: str) -> Optional[str]:
        if filename in _STRUCTURED_FILENAMES or filename.startswith("appsettings."):
            return "filename"
        if "\"ConnectionStrings\"" in text or "\"Logging\"" in text:
            return "content"
        return None

    def _extract_appsettings_environment(self, filename: str) -> Optional[str]:
        if not filename.startswith("appsettings."):
            return None
        parts = filename.split(".")
        if len(parts) <= 2:
            return None
        environment = ".".join(parts[1:-1]).strip()
        if not environment:
            return None
        return environment

    def _attempt_parse(self, text: str, *, allow_comments: bool) -> "ParseResult":
        if allow_comments:
            return ParseResult(success=False, metadata={})
        snippet = self._truncate_to_structural_boundary(text)
        if not snippet:
            return ParseResult(success=False, metadata={})
        try:
            parsed = json_lib.loads(snippet)
        except json_lib.JSONDecodeError:
            return ParseResult(success=False, metadata={})
        metadata: Dict[str, Any] = {}
        if isinstance(parsed, dict):
            metadata["top_level_type"] = "object"
            metadata["top_level_keys"] = list(list(parsed.keys())[:5])
        elif isinstance(parsed, list):
            metadata["top_level_type"] = "array"
            if parsed:
                metadata["top_level_sample_types"] = sorted(
                    {type(item).__name__ for item in parsed[:5]}
                )
        return ParseResult(success=True, metadata=metadata)

    def _truncate_to_structural_boundary(self, text: str) -> str:
        depth_curly = 0
        depth_square = 0
        in_string = False
        escape = False
        last_valid = 0
        for index, char in enumerate(text):
            if in_string:
                if escape:
                    escape = False
                elif char == "\\":
                    escape = True
                elif char == '"':
                    in_string = False
                continue
            if char == '"':
                in_string = True
                continue
            if char == "{":
                depth_curly += 1
            elif char == "}":
                if depth_curly > 0:
                    depth_curly -= 1
            elif char == "[":
                depth_square += 1
            elif char == "]":
                if depth_square > 0:
                    depth_square -= 1
            if depth_curly == 0 and depth_square == 0:
                last_valid = index + 1
        return text[:last_valid].strip()

    def _strip_json_comments(self, text: str) -> Tuple[str, bool]:
        if "//" not in text and "/*" not in text:
            return text, False

        result: List[str] = []
        in_string = False
        escape = False
        i = 0
        length = len(text)
        removed = False

        while i < length:
            char = text[i]
            if in_string:
                result.append(char)
                if escape:
                    escape = False
                elif char == "\\":
                    escape = True
                elif char == '"':
                    in_string = False
                i += 1
                continue

            if char == '"':
                in_string = True
                result.append(char)
                i += 1
                continue

            if char == "/" and i + 1 < length:
                nxt = text[i + 1]
                if nxt == "/":
                    removed = True
                    i += 2
                    while i < length and text[i] not in "\r\n":
                        i += 1
                    continue
                if nxt == "*":
                    end = text.find("*/", i + 2)
                    if end == -1:
                        return text, False
                    removed = True
                    i = end + 2
                    continue

            result.append(char)
            i += 1

        return "".join(result), removed


@dataclass(frozen=True)
class ParseResult:
    success: bool
    metadata: Dict[str, Any]


register(JsonPlugin())
