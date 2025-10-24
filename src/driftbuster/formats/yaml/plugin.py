"""YAML detection heuristics for configuration payloads.

The detector focuses on lightweight pattern checks that avoid a full YAML
parser dependency while still catching common structures:

- Top-level and nested ``key: value`` pairs with indentation
- Document start markers ``---`` and list markers ``- item``
- Filename/extension hints for ``.yml`` and ``.yaml`` and content-based
  detection for YAML-in-``.conf`` cases (e.g., ``mongod.conf``)

Variants surfaced today:

``generic``
    Default classification for YAML configuration payloads.

``kubernetes-manifest``
    When both ``apiVersion:`` and ``kind:`` keys are detected in the document.
"""

from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from ..format_registry import register
from ...core.types import DetectionMatch


_EXTENSIONS = {".yml", ".yaml"}
_DOC_START = re.compile(r"^\s*---\s*$", re.MULTILINE)
_KEY_COLON = re.compile(r"^\s*[A-Za-z_][\w.-]*\s*:\s*(\S|$)", re.MULTILINE)
_INDENTED_BLOCK = re.compile(r"\n\s{2,}[A-Za-z_][\w.-]*\s*:\s*(\S|$)")
_LIST_MARKER = re.compile(r"^\s*-\s+\S+", re.MULTILINE)
_COMMENTED_KEY = re.compile(r"^\s*#\s*[A-Za-z_][\w.-]*\s*:\s*", re.MULTILINE)
_DOC_END = re.compile(r"^\s*\.\.\.\s*$", re.MULTILINE)


def _analyse_indentation(lines: List[str]) -> Optional[Dict[str, object]]:
    """Inspect indentation to derive tolerances and drift signals.

    The YAML plugin only needs a coarse-grained profile so we avoid pulling in a
    parser. This helper summarises indentation style and highlights outliers so
    callers can surface actionable review metadata without parsing the full
    document.
    """

    indent_stats: Counter[int] = Counter()
    space_lines: Dict[int, List[int]] = {}
    tab_lines: List[int] = []
    mixed_lines: List[int] = []

    for idx, raw in enumerate(lines, 1):
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        leading = len(raw) - len(raw.lstrip())
        if leading == 0:
            continue
        prefix = raw[:leading]
        if "\t" in prefix and prefix.replace("\t", ""):
            mixed_lines.append(idx)
            continue
        if "\t" in prefix:
            tab_lines.append(idx)
            continue
        indent_stats[leading] += 1
        space_lines.setdefault(leading, []).append(idx)

    if not indent_stats and not tab_lines and not mixed_lines:
        return None

    metadata: Dict[str, object] = {}
    if tab_lines and not indent_stats:
        metadata["style"] = "tabs"
        metadata["tab_lines"] = tab_lines[:10]
        return metadata

    metadata["style"] = "spaces" if indent_stats else "mixed"
    if indent_stats:
        baseline, _ = indent_stats.most_common(1)[0]
        allowed = {baseline}
        # Allow progressive multiples (2x, 3x) and off-by-two for nested
        # structures that occasionally add extra padding.
        for width in list(indent_stats):
            if width % baseline == 0 or abs(width - baseline) <= 2:
                allowed.add(width)
        outliers: List[int] = []
        for width, occurrences in space_lines.items():
            if width not in allowed:
                outliers.extend(occurrences[:10])
        metadata["baseline"] = baseline
        metadata["allowed_widths"] = sorted(allowed)
        if outliers:
            metadata["outlier_lines"] = sorted(set(outliers))

    if tab_lines:
        metadata["tab_lines"] = tab_lines[:10]
        metadata["style"] = "mixed" if indent_stats else "tabs"
    if mixed_lines:
        metadata["mixed_indent_lines"] = mixed_lines[:10]
        metadata["style"] = "mixed"

    return metadata or None


@dataclass
class YamlPlugin:
    name: str = "yaml"
    # Run before INI to avoid unix-conf/env-file stealing YAML payloads
    priority: int = 160
    version: str = "0.0.3"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        extension = path.suffix.lower()
        lower_name = path.name.lower()
        reasons: List[str] = []
        metadata: Dict[str, object] = {}
        review_reasons: List[str] = []

        if extension in _EXTENSIONS:
            reasons.append(f"File extension {extension} suggests YAML content")

        # Trim leading comment blocks to handle reference files
        lines = text.splitlines()
        start = 0
        # Skip leading blank/comment lines within the sampled text
        while start < len(lines):
            s = lines[start].lstrip()
            if not s or s.startswith('#'):
                start += 1
                continue
            break
        scan_text = "\n".join(lines[start:]) if start else text

        # Core YAML signals
        has_key_colon = bool(_KEY_COLON.search(scan_text))
        has_doc = bool(_DOC_START.search(scan_text))
        has_doc_end = bool(_DOC_END.search(scan_text))
        has_list = bool(_LIST_MARKER.search(scan_text))
        has_indented = bool(_INDENTED_BLOCK.search(scan_text))

        # Oddities: tabs for indentation are suspicious
        if "\t" in text:
            review_reasons.append("Tab indentation present in YAML-like content")

        indent_profile = _analyse_indentation(lines)
        if indent_profile:
            metadata["indentation"] = indent_profile
            outliers = indent_profile.get("outlier_lines")
            if outliers:
                review_reasons.append(
                    "Indentation widths outside tolerated range detected"
                )
            if indent_profile.get("style") in {"tabs", "mixed"} and "Tab indentation present in YAML-like content" not in review_reasons:
                review_reasons.append("Tab or mixed indentation detected in YAML sample")

        # Guard against common false positives like inline URLs with ':'
        # by requiring either indentation-based maps or multiple key: lines.
        key_count = len(_KEY_COLON.findall(scan_text))
        strong_structure = has_indented or key_count >= 3 or (has_key_colon and has_list)

        # Avoid claiming common INI-like extensions unless YAML structure is strong
        ini_like_ext = extension in {".conf", ".cfg", ".ini", ".properties", ".preferences"}
        if ini_like_ext and extension not in _EXTENSIONS:
            if not (has_doc or has_indented or (has_key_colon and has_list and key_count >= 5)):
                strong_structure = False

        if has_doc:
            reasons.append("Detected YAML document start marker '---'")
        if has_doc_end:
            reasons.append("Detected YAML document end marker '...'")
        if has_list:
            reasons.append("Detected YAML list marker '- '")
        if has_key_colon:
            reasons.append("Found key: value pairs indicative of YAML")
        if has_indented:
            reasons.append("Found indented nested key: value blocks")

        # Do not allow extension-only detection: require at least one structural
        # YAML signal (key/list/indent/doc) even when extension suggests YAML.
        if not (
            strong_structure
            or (
                extension in _EXTENSIONS
                and (has_key_colon or has_list or has_indented or has_doc)
            )
        ):
            # Heuristic for heavily-commented reference files (e.g., Salt 'minion')
            commented_keys = len(_COMMENTED_KEY.findall(text))
            if lower_name in {"minion"} and commented_keys >= 6:
                reasons.append("Found numerous commented YAML key: value examples")
                variant = "generic"
                confidence = 0.56
                return DetectionMatch(
                    plugin_name=self.name,
                    format_name="yaml",
                    variant=variant,
                    confidence=confidence,
                    reasons=reasons,
                    metadata=None,
                )
            return None

        # Simple variant hinting
        if re.search(r"^\s*apiVersion\s*:\s*\S+", scan_text, re.MULTILINE) and re.search(
            r"^\s*kind\s*:\s*\S+", scan_text, re.MULTILINE
        ):
            variant = "kubernetes-manifest"
            reasons.append("Detected apiVersion and kind keys typical of Kubernetes")
        else:
            variant = "generic"

        # Confidence based on signals
        confidence = 0.5
        if extension in _EXTENSIONS:
            confidence += 0.15
        if has_key_colon:
            confidence += 0.1
        if has_indented:
            confidence += 0.1
        if has_list:
            confidence += 0.05
        if has_doc:
            confidence += 0.05
        if has_doc_end:
            confidence += 0.02
        if variant == "kubernetes-manifest":
            confidence += 0.05
        confidence = min(0.95, confidence)

        # Metadata preview
        tops = []
        for m in _KEY_COLON.finditer(scan_text):
            key = m.group(0).split(":", 1)[0].strip()
            if key and key not in tops:
                tops.append(key)
            if len(tops) >= 8:
                break
        if tops:
            metadata["top_level_keys_preview"] = tops

        if review_reasons:
            metadata["needs_review"] = True
            metadata["review_reasons"] = review_reasons

        return DetectionMatch(
            plugin_name=self.name,
            format_name="yaml",
            variant=variant,
            confidence=confidence,
            reasons=reasons or ["YAML structure detected"],
            metadata=metadata or None,
        )


register(YamlPlugin())
