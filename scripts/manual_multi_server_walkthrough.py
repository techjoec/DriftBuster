#!/usr/bin/env python3
"""Capture a reproducible multi-server walkthrough for release evidence.

This script executes cold and hot runs of ``driftbuster.multi_server`` using the
bundled multi-server fixtures. It records the console transcript, validates
cache reuse, and summarises diff planner drilldowns so the manual session
artefacts stored under ``artifacts/manual-runs`` remain deterministic.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Mapping, Sequence


@dataclass
class RunResult:
    """Outcome for a single multi-server execution."""

    transcript: str
    payload: Mapping[str, object]

    @property
    def host_cache_flags(self) -> dict[str, bool]:
        """Return cache reuse flags keyed by host identifier."""

        results = self.payload.get("results") or []
        flags: dict[str, bool] = {}
        for entry in results:
            if isinstance(entry, Mapping):
                host_id = str(entry.get("host_id"))
                flags[host_id] = bool(entry.get("used_cache"))
        return flags

    @property
    def hosts(self) -> list[str]:
        """Return host identifiers in the payload."""

        results = self.payload.get("results") or []
        hosts: list[str] = []
        for entry in results:
            if isinstance(entry, Mapping) and entry.get("host_id"):
                hosts.append(str(entry.get("host_id")))
        return hosts

    @property
    def drilldown(self) -> Sequence[Mapping[str, object]]:
        drilldown = self.payload.get("drilldown")
        return drilldown if isinstance(drilldown, Sequence) else []

    @property
    def catalog(self) -> Sequence[Mapping[str, object]]:
        catalog = self.payload.get("catalog")
        return catalog if isinstance(catalog, Sequence) else []


def _load_result(entries: Iterable[Mapping[str, object]]) -> Mapping[str, object]:
    for entry in entries:
        if entry.get("type") == "result":
            payload = entry.get("payload")
            if isinstance(payload, Mapping):
                return payload
    raise RuntimeError("multi_server run did not emit a result payload")


def _run_multi_server(data_root: Path, plans: Sequence[Mapping[str, object]]) -> RunResult:
    env = os.environ.copy()
    env["DRIFTBUSTER_DATA_ROOT"] = str(data_root)
    request = json.dumps({"plans": list(plans)}, ensure_ascii=False)
    proc = subprocess.run(
        [sys.executable, "-m", "driftbuster.multi_server"],
        input=request,
        env=env,
        text=True,
        capture_output=True,
        check=True,
    )
    lines: list[Mapping[str, object]] = []
    for raw_line in proc.stdout.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        try:
            parsed = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(parsed, Mapping):
            lines.append(parsed)
    payload = _load_result(lines)
    return RunResult(transcript=proc.stdout, payload=payload)


def _discover_plans(samples_root: Path) -> list[Mapping[str, object]]:
    hosts = sorted(
        path for path in samples_root.iterdir() if path.is_dir() and not path.name.startswith(".")
    )
    if len(hosts) < 2:
        raise FileNotFoundError(
            f"Expected at least two host directories under {samples_root}, found {len(hosts)}"
        )

    plans: list[Mapping[str, object]] = []
    for index, host_dir in enumerate(hosts):
        is_baseline = index == 0
        plans.append(
            {
                "host_id": host_dir.name,
                "label": "Baseline" if is_baseline else f"Drift sample {index}",
                "scope": "custom_roots",
                "roots": [str(host_dir.resolve())],
                "baseline": {"is_preferred": is_baseline, "priority": 10 if is_baseline else 5},
            }
        )
    return plans


def _prepare_output_paths(output_dir: Path, tag: str | None) -> tuple[Path, Path, str]:
    timestamp = datetime.now(timezone.utc)
    stamp = f"{timestamp:%Y-%m-%d}-{timestamp:%H%M%S}Z"
    slug = tag or "multi-server-session"
    base_name = f"{stamp}-{slug}"
    notes_path = output_dir / f"{base_name}.md"
    console_path = output_dir / f"{base_name}-console.txt"
    return notes_path, console_path, base_name


def _format_diff_summary(run: RunResult) -> list[str]:
    if not run.drilldown:
        return ["- No drilldown entries returned."]

    chosen_entry: Mapping[str, object] | None = None
    for entry in run.drilldown:
        unified = entry.get("unified_diff")
        if isinstance(unified, str) and unified.strip():
            chosen_entry = entry
            break
    if chosen_entry is None:
        chosen_entry = run.drilldown[0]

    config_id = chosen_entry.get("config_id", "unknown")
    catalog_entry = next(
        (item for item in run.catalog if isinstance(item, Mapping) and item.get("config_id") == config_id),
        None,
    )
    summary = chosen_entry.get("diff_summary") if isinstance(chosen_entry.get("diff_summary"), Mapping) else {}
    comparisons = summary.get("comparisons") if isinstance(summary.get("comparisons"), Sequence) else []
    comparison_count = len(comparisons)
    sample_diff = chosen_entry.get("unified_diff") or ""
    diff_lines = sample_diff.splitlines()
    preview_lines = diff_lines[: min(20, len(diff_lines))]
    preview = preview_lines if preview_lines else ["<no diff lines>"]

    lines = [
        f"- Drilldown sample: `{config_id}` ({comparison_count} comparison(s))",
        "  - Flags: "
        + ", ".join(
            filter(
                None,
                [
                    f"drift_count={catalog_entry.get('drift_count')}" if catalog_entry else None,
                    f"has_secrets={bool(chosen_entry.get('has_secrets'))}",
                    f"has_masked_tokens={bool(chosen_entry.get('has_masked_tokens'))}",
                    f"has_validation_issues={bool(chosen_entry.get('has_validation_issues'))}",
                ],
            )
        ),
    ]

    if comparisons:
        metadata_entry = comparisons[0].get("metadata")
        if isinstance(metadata_entry, Mapping):
            parts = []
            for key in ("content_type", "baseline_name", "comparison_name"):
                value = metadata_entry.get(key)
                if value:
                    parts.append(f"{key}={value}")
            if parts:
                lines.append("  - Metadata: " + ", ".join(parts))
        summary_entry = comparisons[0].get("summary")
        if isinstance(summary_entry, Mapping):
            stats_parts = []
            for key in ("added_lines", "removed_lines", "changed_lines"):
                value = summary_entry.get(key)
                if value is not None:
                    stats_parts.append(f"{key}={value}")
            diff_digest = summary_entry.get("diff_digest")
            if diff_digest:
                stats_parts.append(f"diff_digest={diff_digest}")
            if stats_parts:
                lines.append("  - Comparison stats: " + ", ".join(stats_parts))

    lines.append("  - Unified diff preview:")
    lines.append("  ```diff")
    lines.extend(f"  {line}" for line in preview)
    lines.append("  ```")
    return lines


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--samples-root",
        type=Path,
        default=Path("samples/multi-server"),
        help="Directory containing host fixtures.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("artifacts/manual-runs"),
        help="Directory where walkthrough artefacts will be written.",
    )
    parser.add_argument(
        "--tag",
        help="Optional suffix for generated filenames (defaults to 'multi-server-session').",
    )
    args = parser.parse_args()

    samples_root = args.samples_root.resolve()
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    plans = _discover_plans(samples_root)

    tmp_root = Path.home() / ".driftbuster-walkthrough-tmp"
    tmp_root.mkdir(parents=True, exist_ok=True)
    data_root = tmp_root / "multi-server"
    if data_root.exists():
        shutil.rmtree(data_root)
    data_root.mkdir(parents=True, exist_ok=True)

    cold_run = _run_multi_server(data_root, plans)
    hot_run = _run_multi_server(data_root, plans)

    cache_dir = data_root / "cache" / "diffs"
    cache_entries = sorted(p for p in cache_dir.glob("*.json"))

    missing_cache = [host for host, reused in hot_run.host_cache_flags.items() if not reused]
    if missing_cache:
        raise RuntimeError(f"Cache reuse failed for hosts: {', '.join(missing_cache)}")

    notes_path, console_path, base_name = _prepare_output_paths(output_dir, args.tag)

    console_lines = [
        f"# Multi-server walkthrough transcript ({base_name})",
        "## Cold run",
        cold_run.transcript.strip(),
        "",
        "## Hot run",
        hot_run.transcript.strip(),
        "",
    ]
    console_path.write_text("\n".join(console_lines), encoding="utf-8")

    cache_preview_lines = [
        f"  - {path.name}" for path in cache_entries[: min(10, len(cache_entries))]
    ]
    if len(cache_entries) > 10:
        cache_preview_lines.append("  - ...")
    if not cache_preview_lines:
        cache_preview_lines.append("  - <no cache files found>")

    diff_summary_lines = _format_diff_summary(hot_run)

    captured_at = datetime.now(timezone.utc).isoformat()
    notes_lines: list[str] = [
        f"# Multi-server walkthrough summary ({base_name})",
        "",
        f"- Captured: {captured_at}",
        f"- Data root: `{data_root}`",
        f"- Cold run hosts: {', '.join(cold_run.hosts) or '<none>'}",
        f"- Hot run cache reuse: {', '.join(f"{host}={str(flag).lower()}" for host, flag in hot_run.host_cache_flags.items())}",
        f"- Cache entries written: {len(cache_entries)}",
        "",
        "## Cache file preview",
        "",
    ]
    notes_lines.extend(cache_preview_lines)
    notes_lines.extend(["", "## Diff planner verification", ""])
    notes_lines.extend(diff_summary_lines)
    notes_lines.extend([
        "",
        "## Artefacts",
        "",
        f"- Console transcript: `{console_path}`",
        f"- Source samples: `{samples_root}`",
    ])

    notes_path.write_text("\n".join(notes_lines) + "\n", encoding="utf-8")

    print(f"Walkthrough captured to {notes_path}")
    print(f"Console transcript stored at {console_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
