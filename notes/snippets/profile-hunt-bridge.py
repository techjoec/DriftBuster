"""Manual helper to bridge hunt hits with configuration profiles."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Iterable, Mapping

from driftbuster import ProfileStore


def load_json(path: Path) -> object:
    text = path.read_text(encoding="utf-8")
    return json.loads(text)


def iter_bridge(
    store: ProfileStore,
    hunts: Iterable[Mapping[str, object]],
    *,
    tags: Iterable[str],
    root: Path | None = None,
) -> list[dict[str, object]]:
    results: list[dict[str, object]] = []
    for entry in hunts:
        relative = entry.get("relative_path")
        path_text = entry.get("path")
        if not relative and isinstance(path_text, str):
            candidate = Path(path_text)
            if root is not None:
                try:
                    relative = candidate.relative_to(root).as_posix()
                except ValueError:
                    relative = candidate.name
            else:
                relative = candidate.name

        matches = store.matching_configs(tags, relative_path=relative if isinstance(relative, str) else None)
        results.append(
            {
                "hunt": entry,
                "relative_path": relative,
                "profiles": [
                    {
                        "profile": match.profile.name,
                        "config": match.config.identifier,
                        "expected_format": match.config.expected_format,
                        "expected_variant": match.config.expected_variant,
                    }
                    for match in matches
                ],
            }
        )
    return results


def main() -> None:
    store_payload = load_json(Path("profiles.json"))
    hunts_payload = load_json(Path("hunt-results.json"))

    store = ProfileStore.from_dict(store_payload)
    if not isinstance(hunts_payload, list):
        raise SystemExit("hunt-results.json must contain a JSON array")

    bridge = iter_bridge(store, (entry for entry in hunts_payload if isinstance(entry, dict)), tags=["env:prod", "tier:web"], root=Path("deployments/prod-web-01"))
    print(json.dumps({"items": bridge}, indent=2))


if __name__ == "__main__":
    main()
