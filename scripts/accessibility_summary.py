from __future__ import annotations

import argparse
import sys
from pathlib import Path

from driftbuster.accessibility import (
    AccessibilityTranscriptError,
    evaluate_transcript,
    format_evaluation,
    load_accessibility_transcript,
)


DEFAULT_TRANSCRIPT = Path("artifacts/gui-accessibility/narrator-inspect-run-2025-02-14.txt")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Summarise accessibility evidence and flag missing coverage.",
    )
    parser.add_argument(
        "path",
        nargs="?",
        default=str(DEFAULT_TRANSCRIPT),
        help=f"Path to the transcript file (default: {DEFAULT_TRANSCRIPT})",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        transcript = load_accessibility_transcript(args.path)
    except AccessibilityTranscriptError as exc:
        parser.error(str(exc))

    evaluation = evaluate_transcript(transcript)
    for line in format_evaluation(evaluation):
        print(line)

    return 1 if evaluation.has_issues else 0


if __name__ == "__main__":
    sys.exit(main())
