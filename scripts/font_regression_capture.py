from __future__ import annotations

import argparse
import json
from pathlib import Path

from driftbuster.font_regression import (
    FontRegressionError,
    format_evidence,
    load_regression_log,
    regression_evidence_to_dict,
)

DEFAULT_LOG = Path("artifacts/logs/fontmanager-regression.txt")
DEFAULT_OUTPUT = Path("artifacts/logs/fontmanager-regression.json")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Convert the font regression stack trace into structured evidence.",
    )
    parser.add_argument(
        "log_path",
        nargs="?",
        default=str(DEFAULT_LOG),
        help="Source stack trace file (default: artifacts/logs/fontmanager-regression.txt)",
    )
    parser.add_argument(
        "--output",
        default=str(DEFAULT_OUTPUT),
        help="Destination for the structured JSON evidence.",
    )
    parser.add_argument(
        "--no-write",
        action="store_true",
        help="Skip writing the JSON evidence file (only print the summary).",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        evidence = load_regression_log(Path(args.log_path))
    except FontRegressionError as exc:
        parser.error(str(exc))

    summary = format_evidence(evidence)
    for line in summary:
        print(line)

    if not args.no_write:
        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        payload = regression_evidence_to_dict(evidence)
        output_path.write_text(json.dumps(payload, indent=2) + "\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
