"""Detect registry live-scan definition files.

The detector recognises small JSON/YAML manifests that describe a live Windows
Registry hunt (app token, keywords, regex patterns). This integrates the
"registry hunts" concept into the format plugin pipeline without relying on
`.reg` exports.

JSON structure (preferred):

{
  "registry_scan": {
    "token": "Vendor App",
    "keywords": ["server", "endpoint"],
    "patterns": ["https://", "api\\.internal\\.local"],
    "max_depth": 12,
    "max_hits": 200,
    "time_budget_s": 10.0
  }
}

YAML structure (best-effort heuristic detection without full parse):

registry_scan:
  token: Vendor App
  keywords: [server, endpoint]
  patterns:
    - https://
    - api.internal.local
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Sequence

from ..format_registry import register
from ...core.types import DetectionMatch


_JSON_KEY_RE = re.compile(r"\{[^\n\r]*\"registry_scan\"\s*:\s*\{", re.DOTALL)
_YAML_KEY_RE = re.compile(r"^\s*registry_scan\s*:\s*$", re.IGNORECASE | re.MULTILINE)


@dataclass
class RegistryLivePlugin:
    name: str = "registry-live"
    priority: int = 30
    version: str = "0.0.1"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        lower = path.name.lower()
        extension = path.suffix.lower()
        reasons: List[str] = []
        metadata: Dict[str, object] = {}

        # Quick filename/extension hints
        if lower.endswith(".regscan.json") or lower.endswith(".registry.json"):
            reasons.append("Filename suggests a registry scan JSON manifest")
        elif extension in {".json", ".yml", ".yaml"} and any(tok in lower for tok in ("reg", "registry", "scan")):
            reasons.append("Filename contains registry/scan hints")

        # Prefer JSON detection
        parsed_json: Optional[dict] = None
        if extension in {".json", ""} or _JSON_KEY_RE.search(text):
            if _JSON_KEY_RE.search(text):
                reasons.append("Found 'registry_scan' top-level key in JSON payload")
            try:
                parsed_json = json.loads(text)
            except Exception:
                parsed_json = None

        if parsed_json and isinstance(parsed_json, dict) and "registry_scan" in parsed_json:
            spec = parsed_json.get("registry_scan")
            if isinstance(spec, dict):
                token = spec.get("token")
                keywords = spec.get("keywords")
                patterns = spec.get("patterns")
                # Normalise
                if isinstance(token, str) and token.strip():
                    metadata["token"] = token.strip()
                    reasons.append(f"Token provided: {token.strip()}")
                if isinstance(keywords, list):
                    kw = [str(k) for k in keywords if str(k).strip()]
                    if kw:
                        metadata["keywords"] = kw
                        reasons.append("Keyword list provided")
                if isinstance(patterns, list):
                    pt = [str(p) for p in patterns if str(p).strip()]
                    if pt:
                        metadata["patterns"] = pt
                        reasons.append("Pattern list provided")
                for opt in ("max_depth", "max_hits", "time_budget_s"):
                    if opt in spec:
                        metadata[opt] = spec[opt]
                # Detection success via JSON path
                return DetectionMatch(
                    plugin_name=self.name,
                    format_name="registry-live",
                    variant="scan-definition",
                    confidence=min(0.9, 0.65 + 0.05 * (1 if metadata.get("token") else 0) + 0.05 * (1 if metadata.get("keywords") else 0) + 0.05 * (1 if metadata.get("patterns") else 0)),
                    reasons=reasons or ["JSON manifest indicates registry live scan"],
                    metadata=metadata or None,
                )

        # YAML heuristic (no strict parsing to avoid dependency)
        yaml_signal = _YAML_KEY_RE.search(text)
        if extension in {".yml", ".yaml"} and yaml_signal:
            reasons.append("Detected 'registry_scan:' key in YAML content")
            # Best-effort extraction for a few keys
            token_match = re.search(r"^\s*token\s*:\s*(?P<val>.+)$", text, re.MULTILINE)
            if token_match:
                token = token_match.group("val").strip().strip('"\'')
                if token:
                    metadata["token"] = token
            if re.search(r"^\s*keywords\s*:\s*\[.+\]$", text, re.MULTILINE):
                reasons.append("Inline keywords list present")
            if re.search(r"^\s*patterns\s*:\s*(\[|-)\s*", text, re.MULTILINE):
                reasons.append("Pattern list present")
            if metadata.get("token"):
                reasons.append(f"Token provided: {metadata['token']}")
            return DetectionMatch(
                plugin_name=self.name,
                format_name="registry-live",
                variant="scan-definition",
                confidence=0.7 if metadata.get("token") else 0.62,
                reasons=reasons or ["YAML manifest indicates registry live scan"],
                metadata=metadata or None,
            )

        return None


register(RegistryLivePlugin())

