"""Detect notable .conf DSLs not covered by INI heuristics.

Today this focuses on Elastic Logstash pipeline configs which use blocks like
``input { }``, ``filter { }``, and ``output { }`` with nested plugin sections.

We keep detection tight to avoid stealing matches from INI-style ``.conf``
files (Splunk etc.), which are already handled by the INI plugin.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional

from ..format_registry import register
from ...core.types import DetectionMatch


_LOGSTASH_BLOCK = re.compile(r"^\s*(input|filter|output)\s*\{", re.MULTILINE)
_LOGSTASH_PLUGIN = re.compile(r"^\s*[a-zA-Z_][\w-]*\s*\{", re.MULTILINE)


@dataclass
class ConfPlugin:
    name: str = "conf"
    priority: int = 150
    version: str = "0.0.1"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        reasons: List[str] = []
        blocks = _LOGSTASH_BLOCK.findall(text)
        if len(blocks) >= 1:
            reasons.append("Detected Logstash pipeline block(s): " + ", ".join(sorted({b for b in blocks})))
            # Check for at least one nested plugin stanza to strengthen the signal
            if _LOGSTASH_PLUGIN.search(text):
                reasons.append("Found nested plugin stanza inside pipeline block")
            return DetectionMatch(
                plugin_name=self.name,
                format_name="unix-conf",
                variant="logstash-pipeline",
                confidence=0.8 if len(blocks) >= 2 else 0.72,
                reasons=reasons,
                metadata=None,
            )
        return None


register(ConfPlugin())
