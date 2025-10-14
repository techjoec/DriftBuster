"""Run the profile summary CLI helpers programmatically."""

from __future__ import annotations

from pathlib import Path

import json

from driftbuster import diff_summary_snapshots
from driftbuster.profile_cli import main as profile_cli_main


def main() -> int:
    """Generate summary + diff payloads using the CLI entry point."""

    store_path = Path("profiles.json")
    summary_path = Path("profile-summary.json")
    baseline_summary = Path("baseline-summary.json")
    diff_output = Path("profile-summary-diff.json")

    # Build the latest summary and persist it for manual review.
    summary_result = profile_cli_main(
        [
            "summary",
            str(store_path),
            "--output",
            str(summary_path),
            "--indent",
            "2",
            "--sort-keys",
        ]
    )
    if summary_result:
        raise RuntimeError(f"CLI summary failed with exit code {summary_result}")

    # Compare a stored baseline against the freshly generated snapshot.
    if baseline_summary.exists():
        diff_result = profile_cli_main(
            [
                "diff",
                str(baseline_summary),
                str(summary_path),
                "--output",
                str(diff_output),
                "--indent",
                "2",
            ]
        )
        if diff_result:
            raise RuntimeError(f"CLI diff failed with exit code {diff_result}")
        baseline_payload = json.loads(baseline_summary.read_text())
        current_payload = json.loads(summary_path.read_text())
        diff_snapshot = diff_summary_snapshots(baseline_payload, current_payload)
        print("Changed profiles via helper:", [entry["name"] for entry in diff_snapshot["changed_profiles"]])
    return 0


if __name__ == "__main__":  # pragma: no cover - manual helper
    raise SystemExit(main())
