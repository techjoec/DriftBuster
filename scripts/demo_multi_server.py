from __future__ import annotations

import argparse
from pathlib import Path
from typing import Iterable, Mapping

from driftbuster.core.detector import Detector
from driftbuster.hunt import default_rules, hunt_path
from driftbuster.reporting.diff import build_unified_diff
from driftbuster.reporting.html import render_html_report


def collect_servers(root: Path) -> list[Path]:
    return sorted([p for p in root.iterdir() if p.is_dir() and p.name.lower().startswith("server")])


def detect_all(servers: Iterable[Path]) -> list:
    det = Detector()
    matches = []
    for server in servers:
        for path, match in det.scan_path(server, glob="**/*"):
            record = {
                "plugin": match.plugin_name,
                "format": match.format_name,
                "variant": match.variant,
                "confidence": round(match.confidence, 2),
                "path": str(path),
                "metadata": match.metadata or {},
            }
            matches.append(record)
    return matches


def diffs_vs_baseline(servers: Iterable[Path], baseline: Path) -> list[Mapping[str, object]]:
    diffs: list[Mapping[str, object]] = []
    # Candidate relative paths to diff
    candidates = [
        ("app/appsettings.json", "text"),
        ("app/app.ini", "text"),
        ("web/web.config", "xml"),
        ("msbuild/Project.csproj", "xml"),
        ("localization/Strings.resx", "xml"),
    ]
    for rel, content_type in candidates:
        base_path = baseline / rel
        if not base_path.exists():
            continue
        before = base_path.read_text()
        for server in servers:
            if server == baseline:
                continue
            candidate = server / rel
            if not candidate.exists():
                continue
            after = candidate.read_text()
            label = f"{baseline.name} → {server.name}: {rel}"
            diff = build_unified_diff(
                before,
                after,
                content_type=content_type,
                from_label=f"{baseline.name}:{rel}",
                to_label=f"{server.name}:{rel}",
                label=label,
            )
            diffs.append({"label": label, "diff": diff.diff, "stats": dict(diff.stats)})
    return diffs


def main() -> int:
    parser = argparse.ArgumentParser(description="Multi-server demo runner")
    parser.add_argument("--root", type=Path, default=Path("samples/multi-server"))
    parser.add_argument("--baseline", default="server01")
    parser.add_argument("--html", type=Path, help="Optional HTML output path")
    args = parser.parse_args()

    servers = collect_servers(args.root)
    if not servers:
        print("No servers found under", args.root)
        return 1

    baseline = next((s for s in servers if s.name == args.baseline), servers[0])
    print(f"Detected {len(servers)} servers. Baseline: {baseline.name}")

    matches = detect_all(servers)
    print(f"Detections: {len(matches)}")

    diffs = diffs_vs_baseline(servers, baseline)
    print(f"Diffs vs baseline: {len(diffs)}")

    hunts = hunt_path(args.root, rules=default_rules(), glob="**/*.json", return_json=True)
    print(f"Hunt hits: {len(hunts)}")

    if args.html:
        html = render_html_report(
            matches=[],  # omit per-file match spam; summary is enough for demo
            title=f"DriftBuster Multi-Server Report ({baseline.name} baseline)",
            diffs=diffs,
            profile_summary={},
            hunt_hits=hunts,
            extra_metadata={"baseline": baseline.name},
            warnings=["Demo-only data; sample paths and hosts."]
        )
        args.html.parent.mkdir(parents=True, exist_ok=True)
        args.html.write_text(html)
        print("Wrote HTML report:", args.html)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
