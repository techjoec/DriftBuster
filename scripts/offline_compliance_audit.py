"""Audit offline GUI packaging evidence for compliance gaps."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

from driftbuster.offline_compliance import check_offline_compliance


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Audit offline GUI packaging evidence for compliance gaps.",
    )
    parser.add_argument(
        "path",
        nargs="?",
        default="artifacts/gui-packaging",
        help="Path to the packaging evidence directory (default: artifacts/gui-packaging)",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    report = check_offline_compliance(Path(args.path))

    for check in report.checks:
        status = "OK" if check.passed else "FAIL"
        location = str(check.path) if check.path is not None else "<none>"
        if check.detail:
            print(f"[{status}] {check.name} :: {check.detail} ({location})")
        else:
            print(f"[{status}] {check.name} ({location})")

    if report.issues:
        print("\nOffline compliance issues detected:")
        for issue in report.issues:
            print(f" - {issue}")
        return 1

    print("\nOffline compliance evidence looks good.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
