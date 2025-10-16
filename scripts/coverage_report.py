from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET


def load_python_coverage(path: str = "coverage.json") -> dict[str, object] | None:
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def find_cobertura_xml(root: str = "artifacts/coverage-dotnet") -> str | None:
    # Pick the newest run if multiple exist
    candidates = sorted(
        glob.glob(os.path.join(root, "*/coverage.cobertura.xml")),
        key=lambda p: os.path.getmtime(p),
    )
    return candidates[-1] if candidates else None


def load_cobertura_summary(path: str) -> tuple[float, list[tuple[str, float]]]:
    tree = ET.parse(path)
    root = tree.getroot()
    line_rate = float(root.attrib.get("line-rate", 0.0))
    # Collect classes with lowest line-rate
    classes: list[tuple[str, float]] = []
    for cls in root.findall(".//class"):
        name = cls.attrib.get("filename") or cls.attrib.get("name") or "?"
        rate = float(cls.attrib.get("line-rate", 0.0))
        classes.append((name, rate))
    classes.sort(key=lambda x: x[1])
    return line_rate, classes


def percent(v: float) -> str:
    return f"{v * 100:.2f}%"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Summarize repo-wide test coverage.")
    parser.add_argument("--python-json", default="coverage.json")
    parser.add_argument("--dotnet-root", default="artifacts/coverage-dotnet")
    parser.add_argument("--top", type=int, default=5, help="Show top N most undercovered .NET classes")
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

    line_rate, classes = load_cobertura_summary(cob)
    print(f".NET coverage: {percent(line_rate)} (Cobertura)")
    # Show undercovered GUI classes to guide test investment
    undercovered = [(n, r) for n, r in classes if n.startswith("DriftBuster.Gui/")]
    if undercovered:
        print("Top undercovered .NET GUI classes:")
        for name, rate in undercovered[: args.top]:
            print(f"- {name}: {percent(rate)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

