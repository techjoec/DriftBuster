from __future__ import annotations

from dataclasses import dataclass
import plistlib
import re
import sqlite3
from io import BytesIO
from pathlib import Path
from typing import List, Optional

from ..format_registry import decode_text, looks_text, register
from ...core.types import DetectionMatch

_SQLITE_MAGIC = b"SQLite format 3\x00"
_BPLIST_MAGIC = b"bplist00"
_FRONT_MATTER_PATTERN = re.compile(
    r"^---\s*\n(?P<block>.*?\n)---\s*\n",
    re.DOTALL,
)


@dataclass
class BinaryHybridPlugin:
    name: str = "binary-hybrid"
    priority: int = 210
    version: str = "0.1.0"

    def detect(
        self,
        path: Path,
        sample: bytes,
        text: Optional[str],
    ) -> Optional[DetectionMatch]:
        match = self._detect_sqlite(path, sample)
        if match:
            return match

        match = self._detect_binary_plist(path, sample)
        if match:
            return match

        return self._detect_markdown_front_matter(path, sample, text)

    def _detect_sqlite(self, path: Path, sample: bytes) -> Optional[DetectionMatch]:
        if not sample.startswith(_SQLITE_MAGIC):
            return None

        reasons: List[str] = [
            "Detected SQLite database header (SQLite format 3)",
        ]
        table_count = self._count_sqlite_tables(path)
        if table_count is not None:
            reasons.append(f"Enumerated {table_count} table(s) via sqlite3 pragma")
        metadata = {
            "signature": "sqlite-format-3",
            "table_count": table_count,
            "catalog_hint": path.suffix.lower().lstrip("."),
        }
        return DetectionMatch(
            plugin_name=self.name,
            format_name="embedded-sql-db",
            variant="generic",
            confidence=0.98,
            reasons=reasons,
            metadata=metadata,
        )

    def _count_sqlite_tables(self, path: Path) -> Optional[int]:
        if not path.exists():
            return None
        try:
            connection = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        except sqlite3.Error:
            return None
        try:
            cursor = connection.execute(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table'"
            )
            row = cursor.fetchone()
        except sqlite3.Error:
            return None
        finally:
            connection.close()
        if row and isinstance(row[0], int):
            return int(row[0])
        return None

    def _detect_binary_plist(self, path: Path, sample: bytes) -> Optional[DetectionMatch]:
        if not sample.startswith(_BPLIST_MAGIC):
            return None

        metadata: dict[str, object] = {
            "signature": "bplist00",
        }
        reasons = ["Detected binary property list header (bplist00)"]
        try:
            payload = plistlib.load(BytesIO(sample))
        except Exception as exc:  # noqa: BLE001
            metadata["decode_error"] = {
                "type": type(exc).__name__,
                "message": str(exc),
            }
            reasons.append("Binary plist payload could not be decoded; recorded error metadata")
        else:
            metadata["top_level_keys"] = sorted(payload.keys()) if isinstance(payload, dict) else []
            reasons.append("Parsed binary property list via plistlib")

        return DetectionMatch(
            plugin_name=self.name,
            format_name="plist",
            variant="xml-or-binary",
            confidence=0.92,
            reasons=reasons,
            metadata=metadata,
        )

    def _detect_markdown_front_matter(
        self,
        path: Path,
        sample: bytes,
        text: Optional[str],
    ) -> Optional[DetectionMatch]:
        working_text = text
        if working_text is None and looks_text(sample):
            working_text, _encoding = decode_text(sample)

        if not working_text:
            return None

        match = _FRONT_MATTER_PATTERN.match(working_text)
        if not match:
            return None

        block = match.group("block").strip()
        key_candidates = [line.split(":", 1)[0].strip() for line in block.splitlines() if ":" in line]
        metadata = {
            "front_matter_keys": sorted({key for key in key_candidates if key}),
            "has_body": bool(working_text[match.end() :].strip()),
        }
        reasons = [
            "Detected YAML front matter fenced with '---' markers",
        ]
        if metadata["front_matter_keys"]:
            reasons.append(
                "Extracted keys: " + ", ".join(metadata["front_matter_keys"][:5])
            )

        return DetectionMatch(
            plugin_name=self.name,
            format_name="markdown-config",
            variant="embedded-yaml-frontmatter",
            confidence=0.8,
            reasons=reasons,
            metadata=metadata,
        )


register(BinaryHybridPlugin())
