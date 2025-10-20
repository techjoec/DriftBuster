"""Heuristic HCL detector for HashiCorp configs (Nomad/Vault/Consul).

Signals:
- `.hcl` extension
- Block forms: `job {}`, `server {}`, `listener {}`, `seal {}`
- Assignments with `=` and quoted values
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from ..format_registry import register
from ...core.types import DetectionMatch


_EXT = ".hcl"
_BLOCK = re.compile(r"^\s*(job|server|seal|listener|datacenter|client)\b[^\n{]*\{", re.MULTILINE)
_KV = re.compile(r"^\s*[A-Za-z0-9_.\-]+\s*=\s*\S+", re.MULTILINE)


@dataclass
class HclPlugin:
    name: str = "hcl"
    priority: int = 158
    version: str = "0.0.1"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        ext = path.suffix.lower()
        reasons: List[str] = []
        metadata: Dict[str, object] = {}

        if ext == _EXT:
            reasons.append("File extension .hcl suggests HashiCorp HCL")

        blocks = _BLOCK.findall(text)
        kvs = _KV.findall(text)
        if blocks:
            reasons.append("Found HCL-style block declarations (e.g., job/server)")
            metadata["blocks_preview"] = sorted(set(blocks))[:5]
        if kvs:
            reasons.append("Detected key = value assignments")

        signals = sum(1 for flag in (ext == _EXT, bool(blocks), bool(kvs)) if flag)
        if signals < 2:
            return None

        variant = "hashicorp-nomad" if "job" in [b.lower() for b in blocks] else (
            "hashicorp-vault" if any(b.lower() in {"seal", "listener"} for b in blocks) else (
                "hashicorp-consul" if "server" in [b.lower() for b in blocks] or "datacenter" in [b.lower() for b in blocks] else "generic"
            )
        )

        confidence = 0.55
        if ext == _EXT:
            confidence += 0.2
        if blocks:
            confidence += 0.15
        if kvs:
            confidence += 0.05
        confidence = min(0.95, confidence)

        return DetectionMatch(
            plugin_name=self.name,
            format_name="hcl",
            variant=variant,
            confidence=confidence,
            reasons=reasons or ["HCL structure detected"],
            metadata=metadata or None,
        )


register(HclPlugin())
