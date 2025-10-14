"""Manual helper to derive token catalog entries from hunt JSON output.

Usage example::

    PYTHONPATH=src python notes/snippets/token-catalog.py \
        --hunts path/to/hunt-results.json \
        --catalog-variant structured-settings-json \
        --output token-catalog.json

The script hashes hunt excerpts so reviewers can reference tokens without
persisting raw secrets in the repository. Provide `--catalog-variant` when the
entire hunt payload maps to a single configuration variant (e.g.,
`structured-settings-json`). Otherwise, the script propagates the value stored
in each hunt entry's `metadata.catalog_variant` field when present.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any, Dict, Iterable, List, Mapping, MutableMapping, Optional


def _load_hunts(path: Path) -> List[Mapping[str, Any]]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, list):
        raise ValueError("Hunt payload must be a JSON array")
    entries: List[Mapping[str, Any]] = []
    for entry in payload:
        if isinstance(entry, Mapping):
            entries.append(entry)
    return entries


def _hash_excerpt(excerpt: Optional[str]) -> Optional[str]:
    if not excerpt:
        return None
    digest = hashlib.sha256(excerpt.encode("utf-8")).hexdigest()
    return digest


def _normalise_entry(
    entry: Mapping[str, Any],
    *,
    default_variant: Optional[str],
) -> Optional[Dict[str, Any]]:
    rule = entry.get("rule") if isinstance(entry.get("rule"), Mapping) else {}
    token_name = None
    if isinstance(rule, Mapping):
        token_name = rule.get("token_name") or rule.get("name")
    if not token_name:
        return None

    catalog_variant = default_variant
    metadata = entry.get("metadata")
    if isinstance(metadata, Mapping):
        variant_value = metadata.get("catalog_variant")
        if isinstance(variant_value, str) and variant_value:
            catalog_variant = variant_value

    result: MutableMapping[str, Any] = {
        "token_name": token_name,
        "hunt_rule": rule.get("name"),
        "catalog_variant": catalog_variant,
        "placeholder": entry.get("placeholder"),
        "source_path": entry.get("relative_path") or entry.get("path"),
        "excerpt_hash": _hash_excerpt(entry.get("excerpt")),
    }
    return {key: value for key, value in result.items() if value is not None}


def build_catalog(
    entries: Iterable[Mapping[str, Any]],
    *,
    default_variant: Optional[str],
) -> List[Dict[str, Any]]:
    catalog: List[Dict[str, Any]] = []
    for entry in entries:
        normalised = _normalise_entry(entry, default_variant=default_variant)
        if normalised:
            catalog.append(normalised)
    return catalog


def main(argv: Optional[Iterable[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Normalise hunt hits into a token catalog skeleton.")
    parser.add_argument("--hunts", type=Path, required=True, help="Path to hunt JSON payload (return_json=True output).")
    parser.add_argument(
        "--catalog-variant",
        help="Optional default catalog variant applied when hunt metadata lacks one.",
    )
    parser.add_argument("--output", type=Path, help="Optional output file (defaults to stdout).")
    args = parser.parse_args(argv)

    entries = _load_hunts(args.hunts)
    catalog = build_catalog(entries, default_variant=args.catalog_variant)
    text = json.dumps(catalog, indent=2)
    if args.output:
        args.output.write_text(text + ("\n" if not text.endswith("\n") else ""), encoding="utf-8")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
