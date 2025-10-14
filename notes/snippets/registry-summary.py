"""Quick snippet to capture registry ordering for manual reviews."""

from __future__ import annotations

import json

from driftbuster import registry_summary


def main() -> None:
    summary = registry_summary()
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
