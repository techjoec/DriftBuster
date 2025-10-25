#!/usr/bin/env python3
"""Score detection coverage on configsamples and emit fuzz inputs.

Usage::

    python -m scripts.score_configsamples [root]

The script scans ``configsamples/library/by-format`` folders and reports:

* Per-plugin match counts and percentages
* A summary of files with no detections (``0%`` coverage items)
* Optional fuzzed variants that can be fed back into regression suites

Provide ``--fuzz-output`` and ``--fuzz-count`` to materialise fuzzed samples
in a target directory. The generator operates deterministically when a seed is
supplied so fixtures can be refreshed or expanded safely.
"""

from __future__ import annotations

import argparse
import random
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Optional, Sequence

from driftbuster.core.detector import Detector


TARGET_FOLDERS = ("conf", "yaml", "text", "toml", "hcl", "dockerfile")
DEFAULT_MAX_TOTAL_SAMPLE_BYTES = 16 * 1024 * 1024  # 16 MiB guardrail.
DEFAULT_FUZZ_MAX_BYTES = 4096


@dataclass
class ScanOutcome:
    """Represents the results of a scoring run."""

    plugin_counts: Counter[str]
    unmatched: list[Path]
    scanned_files: list[Path]
    by_plugin_files: dict[str, list[Path]]
    budget_exhausted: bool


def iter_files(root: Path) -> Iterable[Path]:
    for folder in TARGET_FOLDERS:
        base = root / "configsamples" / "library" / "by-format" / folder
        if not base.is_dir():
            continue
        for p in base.rglob("*"):
            if not p.is_file():
                continue
            if p.name.lower() == "metadata.json":
                continue
            yield p


def build_detector(max_total_sample_bytes: Optional[int] = None) -> Detector:
    """Create a :class:`Detector` honouring the sampling budget guardrail."""

    kwargs = {"sort_plugins": True}
    if max_total_sample_bytes is not None:
        kwargs["max_total_sample_bytes"] = max_total_sample_bytes
    return Detector(**kwargs)


def scan_files(
    files: Sequence[Path],
    *,
    detector: Detector,
    stop_on_budget: bool = True,
) -> ScanOutcome:
    """Scan files and accumulate plugin coverage statistics."""

    plugin_counts: Counter[str] = Counter()
    unmatched: list[Path] = []
    scanned_files: list[Path] = []
    by_plugin_files: dict[str, list[Path]] = defaultdict(list)
    budget_exhausted = False

    for path in files:
        scanned_files.append(path)
        match = detector.scan_file(path)
        if match is None:
            unmatched.append(path)
        else:
            plugin_counts[match.plugin_name] += 1
            by_plugin_files[match.plugin_name].append(path)

        if detector.sample_budget_exhausted:
            budget_exhausted = True
            if stop_on_budget:
                break

    return ScanOutcome(
        plugin_counts=plugin_counts,
        unmatched=unmatched,
        scanned_files=scanned_files,
        by_plugin_files=by_plugin_files,
        budget_exhausted=budget_exhausted,
    )


def _flip_byte(data: bytes, rng: random.Random) -> bytes:
    index = rng.randrange(len(data))
    delta = rng.randrange(1, 255)
    mutated = bytearray(data)
    mutated[index] ^= delta
    return bytes(mutated)


def _drop_region(data: bytes, rng: random.Random) -> bytes:
    if len(data) == 1:
        return data
    start = rng.randrange(0, len(data) - 1)
    end = rng.randrange(start + 1, len(data))
    prefix = data[:start]
    suffix = data[end:]
    filler = b"# fuzz-drop\n"
    return prefix + filler + suffix


def _duplicate_region(data: bytes, rng: random.Random, max_bytes: int) -> bytes:
    start = rng.randrange(0, len(data))
    end = min(len(data), start + rng.randrange(1, min(32, len(data) - start) + 1))
    region = data[start:end]
    insertion = data[:end] + region
    return (insertion + data[end:])[:max_bytes]


def _insert_comment(data: bytes, rng: random.Random, max_bytes: int) -> bytes:
    comment = f"# fuzz-{rng.randrange(10_000):04d}\n".encode("utf-8")
    insert_at = rng.randrange(0, len(data) + 1)
    mutated = data[:insert_at] + comment + data[insert_at:]
    return mutated[:max_bytes]


def _mutate_bytes(data: bytes, rng: random.Random, max_bytes: int) -> bytes:
    data = data[:max_bytes]
    if not data:
        return b"# fuzz-empty\n"

    strategies = [
        lambda: _flip_byte(data, rng),
        lambda: _drop_region(data, rng),
        lambda: _duplicate_region(data, rng, max_bytes),
        lambda: _insert_comment(data, rng, max_bytes),
    ]

    mutated = strategies[rng.randrange(len(strategies))]()
    mutated = mutated[:max_bytes]
    if not mutated.strip():
        mutated = b"# fuzz-placeholder\n"
    if mutated == data:
        mutated = _flip_byte(data, rng)
    return mutated[:max_bytes]


def generate_fuzz_inputs(
    sources: Sequence[Path],
    *,
    root: Path,
    output_dir: Path,
    per_file: int,
    seed: Optional[int] = None,
    max_bytes: int = DEFAULT_FUZZ_MAX_BYTES,
) -> list[Path]:
    """Generate fuzzed variants for the supplied ``sources``."""

    if per_file <= 0:
        return []

    rng = random.Random(seed)
    created: list[Path] = []
    output_dir.mkdir(parents=True, exist_ok=True)

    for source in sources:
        relative = source.relative_to(root)
        data = source.read_bytes()
        for index in range(per_file):
            variant = _mutate_bytes(data, rng, max_bytes)
            target_dir = output_dir / relative.parent
            target_dir.mkdir(parents=True, exist_ok=True)
            stem = relative.stem or relative.name
            suffix = relative.suffix
            fuzz_name = f"{stem}.fuzz{index}{suffix}" if suffix else f"{stem}.fuzz{index}"
            destination = target_dir / fuzz_name
            destination.write_bytes(variant)
            created.append(destination)

    return created


def parse_args(argv: Optional[list[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "root",
        nargs="?",
        help="Project root containing configsamples (defaults to repository root)",
    )
    parser.add_argument(
        "--max-total-sample-bytes",
        type=int,
        default=DEFAULT_MAX_TOTAL_SAMPLE_BYTES,
        help="Sampling budget guardrail passed to Detector (default: 16 MiB)",
    )
    parser.add_argument(
        "--fuzz-output",
        type=Path,
        help="Directory where fuzzed samples will be written",
    )
    parser.add_argument(
        "--fuzz-count",
        type=int,
        default=0,
        help="Number of fuzz variants per source file (disabled when 0)",
    )
    parser.add_argument(
        "--fuzz-seed",
        type=int,
        help="Optional random seed for deterministic fuzz generation",
    )
    parser.add_argument(
        "--fuzz-max-bytes",
        type=int,
        default=DEFAULT_FUZZ_MAX_BYTES,
        help="Maximum bytes to read/write for each fuzzed sample",
    )
    return parser.parse_args(argv)


def main(argv: Optional[list[str]] = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    project_root = (
        Path(args.root).resolve()
        if args.root
        else Path(__file__).resolve().parents[1]
    )

    files = list(iter_files(project_root))
    if not files:
        print("No files found to scan.")
        return 1

    detector = build_detector(args.max_total_sample_bytes)
    outcome = scan_files(files, detector=detector)
    total = len(outcome.scanned_files)

    plugin_counts = outcome.plugin_counts
    print("== Plugin Coverage ==")
    for name, count in plugin_counts.most_common():
        pct = 100.0 * count / max(total, 1)
        print(f"{name:20s} {count:4d} / {total:4d}  ({pct:5.1f}%)")

    unmatched = outcome.unmatched
    if unmatched:
        print("\n== 0% Coverage Items ==")
        for path in sorted(unmatched):
            rel = path.relative_to(project_root)
            print(str(rel))
    else:
        print("\n== 0% Coverage Items ==\nNone — all sampled files matched.")

    if outcome.budget_exhausted:
        remaining = detector.sample_budget_remaining
        print(
            "\nSampling budget exhausted; remaining bytes: "
            f"{max(remaining, 0)}"
        )

    if args.fuzz_output and args.fuzz_count > 0:
        sources: Sequence[Path] = unmatched or outcome.scanned_files
        created = generate_fuzz_inputs(
            list(sources),
            root=project_root,
            output_dir=args.fuzz_output,
            per_file=args.fuzz_count,
            seed=args.fuzz_seed,
            max_bytes=args.fuzz_max_bytes,
        )
        print(
            "\nGenerated" if created else "\nNo fuzz inputs generated.",
            end="",
        )
        if created:
            print(f" {len(created)} fuzzed samples under {args.fuzz_output}")
        else:
            print()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
