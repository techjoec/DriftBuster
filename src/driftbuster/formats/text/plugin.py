"""Detect directive-style text configuration files.

Targets formats that consist of whitespace-delimited directives without
explicit ``=`` or ``:`` separators. Examples: OpenSSH ``sshd_config`` and
OpenVPN ``client.conf``. This runs as a low-priority fallback after structured
parsers.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from ..format_registry import register
from ...core.types import DetectionMatch


_COMMENT = re.compile(r"^\s*[#;]")
_DIRECTIVE = re.compile(r"^\s*[A-Za-z_][\w.-]*(?:\s+.+)?$")
_ASSIGNMENT = re.compile(r"[=:]")


def _line_kind(line: str) -> str:
    s = line.strip()
    if not s:
        return "blank"
    if _COMMENT.match(s):
        return "comment"
    if _ASSIGNMENT.search(s):
        return "assignment"
    if _DIRECTIVE.match(s):
        return "directive"
    return "other"


@dataclass
class TextPlugin:
    name: str = "text"
    priority: int = 1000
    version: str = "0.0.1"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        lines = [ln for ln in text.splitlines()[:500]]
        kinds = [_line_kind(ln) for ln in lines]
        directive_lines = [ln for ln, k in zip(lines, kinds) if k == "directive"]
        assignment_lines = [ln for ln, k in zip(lines, kinds) if k == "assignment"]
        comment_lines = [ln for ln, k in zip(lines, kinds) if k == "comment"]

        reasons: List[str] = []
        metadata: Dict[str, object] = {}

        lower = path.name.lower()
        content = "\n".join(lines)

        # Known subtypes can be recognised with fewer directive lines
        openssh_hint = lower == "sshd_config" or re.search(
            r"^\s*Subsystem\s+sftp\b", content, re.MULTILINE
        )
        openvpn_hint = re.search(r"^\s*client\s*$", content, re.MULTILINE) and re.search(
            r"^\s*(dev|remote|proto)\b", content, re.MULTILINE
        )

        if (len(directive_lines) >= 4 and len(assignment_lines) <= 1) or (
            openssh_hint and len(directive_lines) >= 3
        ) or (openvpn_hint and len(directive_lines) >= 3):
            reasons.append("Detected whitespace-delimited directives with minimal assignments")
            if comment_lines:
                reasons.append("Found comment lines typical of text configs")
            variant = "generic-directive-text"
            if openssh_hint:
                variant = "openssh-conf"
                reasons.append("Matched OpenSSH markers (sshd_config or Subsystem sftp)")
            elif openvpn_hint:
                variant = "openvpn-conf"
                reasons.append("Matched OpenVPN markers (client/dev/remote/proto)")

            confidence = 0.68
            if variant != "generic-directive-text":
                confidence += 0.12
            confidence = min(0.9, confidence)

            metadata["directive_lines"] = min(len(directive_lines), 50)
            if comment_lines:
                metadata["comment_lines"] = min(len(comment_lines), 50)

            return DetectionMatch(
                plugin_name=self.name,
                format_name="unix-conf",
                variant=variant,
                confidence=confidence,
                reasons=reasons,
                metadata=metadata or None,
            )

        return None


register(TextPlugin())
