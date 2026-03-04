from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET


def load_python_coverage(path: str = "coverage.json") -> dict[str, object] | None:
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def find_cobertura_xml(root: str = "artifacts/coverage-dotnet") -> str | None:
    # Pick the newest run if multiple exist.
    candidates = sorted(
        glob.glob(os.path.join(root, "*/coverage.cobertura.xml"))
        + glob.glob(os.path.join(root, "coverage.cobertura.xml")),
        key=lambda p: os.path.getmtime(p),
    )
    return candidates[-1] if candidates else None


def load_cobertura_summary(path: str) -> tuple[float, list[tuple[str, float]], list[tuple[str, int, int]]]:
    tree = ET.parse(path)
    root = tree.getroot()
    line_rate = float(root.attrib.get("line-rate", 0.0))
    classes: list[tuple[str, float]] = []
    files: list[tuple[str, int, int]] = []
    for cls in root.findall(".//class"):
        name = cls.attrib.get("filename") or cls.attrib.get("name") or "?"
        rate = float(cls.attrib.get("line-rate", 0.0))
        classes.append((name, rate))
        lines = cls.findall(".//line")
        total = len(lines)
        hit = sum(1 for line in lines if int(line.attrib.get("hits", "0")) > 0)
        files.append((name, hit, total))
    classes.sort(key=lambda x: x[1])
    return line_rate, classes, files


def normalise_coverage_path(path: str) -> str:
    return path.replace("\\", "/")


def is_production_dotnet_source(path: str) -> bool:
    # Scope .NET gate/reporting to executable production C# source files.
    # Exclude generated paths and test projects.
    normalised = normalise_coverage_path(path)
    if not normalised.endswith(".cs"):
        return False
    if "/obj/" in normalised:
        return False
    if re.search(r"(^|/)DriftBuster\..*\.Tests(/|$)", normalised):
        return False
    return True


def summarise_scoped_dotnet(files: list[tuple[str, int, int]]) -> tuple[float, list[tuple[str, int, int, float]]]:
    aggregated: dict[str, tuple[int, int]] = {}
    for name, hit, total in files:
        normalised = normalise_coverage_path(name)
        if not is_production_dotnet_source(normalised):
            continue

        previous = aggregated.get(normalised)
        if previous is None:
            aggregated[normalised] = (hit, total)
            continue

        prev_hit, prev_total = previous
        aggregated[normalised] = (prev_hit + hit, prev_total + total)

    scoped = [
        (
            name,
            hit,
            total,
            (hit / total if total else 0.0),
        )
        for name, (hit, total) in aggregated.items()
        if total > 0
    ]
    scoped.sort(key=lambda item: (item[3], -item[2], item[0]))

    total_hit = sum(item[1] for item in scoped)
    total_lines = sum(item[2] for item in scoped)
    ratio = (total_hit / total_lines) if total_lines else 0.0
    return ratio, scoped


def percent(v: float) -> str:
    return f"{v * 100:.2f}%"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Summarize repo-wide test coverage.")
    parser.add_argument("--python-json", default="coverage.json")
    parser.add_argument("--dotnet-root", default="artifacts/coverage-dotnet")
    parser.add_argument("--top", type=int, default=5, help="Show top N most undercovered .NET classes")
    parser.add_argument("--dotnet-threshold", type=float, default=90.0, help="Fail when scoped .NET coverage is below this percent.")
    parser.add_argument("--enforce-dotnet-threshold", action="store_true", help="Return non-zero when scoped .NET coverage is below --dotnet-threshold.")
    args = parser.parse_args(argv)

    py = load_python_coverage(args.python_json)
    if py is not None:
        totals = py.get("totals") if isinstance(py, dict) else None
        py_summary = totals.get("percent_covered") if isinstance(totals, dict) else None
        print(f"Python coverage: {py_summary}%")
    else:
        print("Python coverage: coverage.json not found")

    cob = find_cobertura_xml(args.dotnet_root)
    if cob is None:
        print(".NET coverage: Cobertura XML not found")
        return 0

    line_rate, classes, files = load_cobertura_summary(cob)
    print(f".NET coverage (raw Cobertura): {percent(line_rate)}")

    scoped_rate, scoped_files = summarise_scoped_dotnet(files)
    print(f".NET coverage (scoped production .cs): {percent(scoped_rate)}")

    if scoped_files:
        print("Top undercovered scoped .NET files:")
        for name, hit, total, rate in scoped_files[: args.top]:
            print(f"- {name}: {percent(rate)} ({hit}/{total})")

    # Show undercovered GUI classes to guide test investment
    undercovered = [(n, r) for n, r in classes if n.startswith("DriftBuster.Gui/")]
    if undercovered:
        print("Top undercovered .NET GUI classes:")
        for name, rate in undercovered[: args.top]:
            print(f"- {name}: {percent(rate)}")

    threshold_ratio = args.dotnet_threshold / 100.0
    if args.enforce_dotnet_threshold and scoped_rate < threshold_ratio:
        print(
            f".NET scoped coverage check failed: {percent(scoped_rate)} "
            f"is below required {args.dotnet_threshold:.2f}%."
        )
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
