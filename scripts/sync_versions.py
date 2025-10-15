#!/usr/bin/env python3
"""Synchronise project version strings from versions.json.

Each distributable (core package, GUI, PowerShell module, format plugins, and
catalog) declares its version in ``versions.json``.  This script propagates the
canonical values into the build configuration, manifests, docs, and tests so
version bumps only touch the components that actually change.

Usage
-----
Run the script from the repository root after editing ``versions.json``::

    python scripts/sync_versions.py

The command exits with a non-zero status if any expected replacement fails
so mismatches surface immediately.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VERSIONS_FILE = ROOT / "versions.json"


class SyncError(RuntimeError):
    """Raised when a replacement fails."""


def load_versions() -> dict[str, object]:
    data = json.loads(VERSIONS_FILE.read_text(encoding="utf-8"))
    expected_keys = {"core", "catalog", "gui", "powershell", "formats"}
    missing = expected_keys - data.keys()
    if missing:
        raise SyncError(f"versions.json is missing keys: {sorted(missing)}")
    return data


def update_file(path: Path, pattern: str, replacement: str, *, count: int = 0) -> None:
    original = path.read_text(encoding="utf-8")
    new_text, applied = re.subn(pattern, replacement, original, count=count)
    if applied == 0:
        raise SyncError(f"No replacements made in {path} for pattern {pattern!r}")
    path.write_text(new_text, encoding="utf-8")


def main() -> None:
    versions = load_versions()
    formats: dict[str, str] = versions.get("formats", {})  # type: ignore[arg-type]

    # Core package / catalog
    update_file(
        ROOT / "pyproject.toml",
        r'version\s*=\s*"[^"]+"',
        f'version = "{versions["core"]}"',
        count=1,
    )
    update_file(
        ROOT / "Directory.Build.props",
        r"<DriftBusterCoreVersion>[^<]+</DriftBusterCoreVersion>",
        f"<DriftBusterCoreVersion>{versions['core']}</DriftBusterCoreVersion>",
        count=1,
    )
    update_file(
        ROOT / "src" / "driftbuster" / "catalog.py",
        r'version="[^"]+"',
        f'version="{versions["catalog"]}"',
        count=1,
    )

    # GUI + PowerShell
    update_file(
        ROOT / "gui" / "GuiVersion.props",
        r"<DriftBusterGuiVersion>[^<]+</DriftBusterGuiVersion>",
        f"<DriftBusterGuiVersion>{versions['gui']}</DriftBusterGuiVersion>",
        count=1,
    )
    psd1 = ROOT / "cli" / "DriftBuster.PowerShell" / "DriftBuster.psd1"
    update_file(
        psd1,
        r"ModuleVersion\s*=\s*'[^']+'",
        f"ModuleVersion     = '{versions['powershell']}'",
        count=1,
    )
    update_file(
        psd1,
        r"BackendVersion\s*=\s*'[^']+'",
        f"        BackendVersion = '{versions['powershell']}'",
        count=1,
    )

    # Format plugins
    update_file(
        ROOT / "src" / "driftbuster" / "formats" / "ini" / "plugin.py",
        r'version: str = "[^"]+"',
        f'version: str = "{formats.get("ini")}"',
        count=1,
    )
    update_file(
        ROOT / "src" / "driftbuster" / "formats" / "json" / "plugin.py",
        r'version: str = "[^"]+"',
        f'version: str = "{formats.get("json")}"',
        count=1,
    )
    # XML plugin version remains independently managed; ensure consistency if provided.
    if formats.get("xml"):
        update_file(
            ROOT / "src" / "driftbuster" / "formats" / "xml" / "plugin.py",
            r'version: str = "[^"]+"',
            f'version: str = "{formats.get("xml")}"',
            count=1,
        )

    # Docs and tests referencing catalog / plugin versions
    catalog_version = versions["catalog"]
    update_file(
        ROOT / "docs" / "detection-types.md",
        r"DETECTION_CATALOG` \(v[^)]+\)",
        f"DETECTION_CATALOG` (v{catalog_version})",
        count=1,
    )
    update_file(
        ROOT / "docs" / "detection-types.md",
        r"format survey data\n\(v[0-9.]+\)",
        f"format survey data\n(v{catalog_version})",
        count=1,
    )
    update_file(
        ROOT / "docs" / "detection-types.md",
        r"``catalog_version`` \| Detection catalog version embedded in the match payload\.\s+\| ``[0-9.]+``",
        f"``catalog_version`` | Detection catalog version embedded in the match payload.     | ``{catalog_version}``",
        count=1,
    )
    update_file(
        ROOT / "docs" / "detection-types.md",
        r'"catalog_version": "[^"]+"',
        f'"catalog_version": "{catalog_version}"',
    )

    update_file(
        ROOT / "docs" / "format-addition-guide.md",
        r"JsonPlugin` \| 200 \| [0-9.]+",
        f"JsonPlugin` | 200 | {formats.get('json')}",
        count=1,
    )
    update_file(
        ROOT / "docs" / "format-addition-guide.md",
        r"IniPlugin` \| 170 \| [0-9.]+",
        f"IniPlugin` | 170 | {formats.get('ini')}",
        count=1,
    )
    update_file(
        ROOT / "docs" / "format-support.md",
        r"json\s+\|\s+[0-9.]+",
        f"json   | {formats.get('json')}",
    )
    update_file(
        ROOT / "docs" / "format-support.md",
        r"ini\s+\|\s+[0-9.]+",
        f"ini    | {formats.get('ini')}",
    )

    update_file(
        ROOT / "docs" / "customization.md",
        r'version = "[^"]+"',
        f'version = "{versions["core"]}"',
        count=1,
    )

    update_file(
        ROOT / "notes" / "snippets" / "xml-config-diffs.md",
        r'"catalog_version": "[^"]+"',
        f'"catalog_version": "{catalog_version}"',
    )

    update_file(
        ROOT / "tests" / "core" / "test_detector.py",
        r'catalog_version"] == "[^"]+"',
        f'catalog_version"] == "{catalog_version}"',
        count=1,
    )

    # Ensure dev CLAs carry the current release tag for traceability
    update_file(
        ROOT / "CLA" / "INDIVIDUAL.md",
        r"\*\*Version:\*\* [0-9.]+",
        f"**Version:** {versions['core']}",
        count=1,
    )
    update_file(
        ROOT / "CLA" / "ENTITY.md",
        r"\*\*Version:\*\* [0-9.]+",
        f"**Version:** {versions['core']}",
        count=1,
    )


if __name__ == "__main__":  # pragma: no cover - manual utility
    try:
        main()
    except SyncError as exc:
        raise SystemExit(str(exc))
