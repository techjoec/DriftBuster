from __future__ import annotations

import argparse
import glob
import json
import os
import re
import subprocess
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


def coverage_path_candidates(path: str) -> set[str]:
    normalised = normalise_coverage_path(path)
    candidates = {normalised}

    # Cobertura paths are typically project-relative (e.g. DriftBuster.Gui/...),
    # while git diff uses repo-relative paths (e.g. gui/DriftBuster.Gui/...).
    if normalised.startswith("DriftBuster."):
        candidates.add(f"gui/{normalised}")

    if os.path.isabs(normalised):
        try:
            rel = normalise_coverage_path(os.path.relpath(normalised, os.getcwd()))
            if not rel.startswith("../"):
                candidates.add(rel)
        except ValueError:
            pass

    return candidates


def load_cobertura_summary(path: str) -> tuple[
    float,
    list[tuple[str, float]],
    list[tuple[str, int, int]],
    dict[str, dict[int, int]],
]:
    tree = ET.parse(path)
    root = tree.getroot()
    line_rate = float(root.attrib.get("line-rate", 0.0))
    classes: list[tuple[str, float]] = []
    files: list[tuple[str, int, int]] = []
    line_hits: dict[str, dict[int, int]] = {}
    for cls in root.findall(".//class"):
        name = cls.attrib.get("filename") or cls.attrib.get("name") or "?"
        rate = float(cls.attrib.get("line-rate", 0.0))
        classes.append((name, rate))
        lines = cls.findall("./lines/line")
        total = len(lines)
        hit = sum(1 for line in lines if int(line.attrib.get("hits", "0")) > 0)
        files.append((name, hit, total))

        if not lines:
            continue

        for candidate in coverage_path_candidates(name):
            index = line_hits.setdefault(candidate, {})
            for line in lines:
                number = int(line.attrib.get("number", "0"))
                hits = int(line.attrib.get("hits", "0"))
                existing = index.get(number)
                if existing is None or hits > existing:
                    index[number] = hits
    classes.sort(key=lambda x: x[1])
    return line_rate, classes, files, line_hits


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


def load_changed_production_lines(base_ref: str) -> dict[str, set[int]]:
    try:
        diff_output = subprocess.check_output(
            ["git", "diff", "--unified=0", f"{base_ref}...HEAD", "--", "*.cs"],
            text=True,
            stderr=subprocess.DEVNULL,
        )
    except FileNotFoundError as exc:
        raise RuntimeError("git executable is not available") from exc
    except subprocess.CalledProcessError as exc:
        raise RuntimeError(f"unable to compute git diff for base ref '{base_ref}'") from exc

    changed: dict[str, set[int]] = {}
    current_file: str | None = None
    for raw_line in diff_output.splitlines():
        if raw_line.startswith("+++ "):
            path = raw_line[4:].strip()
            if path == "/dev/null":
                current_file = None
                continue
            if path.startswith("b/"):
                path = path[2:]

            path = normalise_coverage_path(path)
            if is_production_dotnet_source(path):
                changed.setdefault(path, set())
                current_file = path
            else:
                current_file = None
            continue

        if not raw_line.startswith("@@") or current_file is None:
            continue

        match = re.search(r"\+(\d+)(?:,(\d+))?", raw_line)
        if match is None:
            continue

        start = int(match.group(1))
        count = int(match.group(2) or "1")
        if count == 0:
            continue

        for line_number in range(start, start + count):
            changed[current_file].add(line_number)

    return changed


def summarise_changed_dotnet_lines(
    changed_lines: dict[str, set[int]],
    line_hits: dict[str, dict[int, int]],
) -> tuple[float | None, list[tuple[str, int, int, float]], list[str]]:
    details: list[tuple[str, int, int, float]] = []
    skipped: list[str] = []
    total_hit = 0
    total_lines = 0

    for file_path in sorted(changed_lines):
        lines = changed_lines[file_path]
        hits_index = line_hits.get(file_path)
        if not hits_index:
            skipped.append(file_path)
            continue

        executable = 0
        covered = 0
        for line_number in lines:
            hits = hits_index.get(line_number)
            if hits is None:
                continue
            executable += 1
            if hits > 0:
                covered += 1

        if executable == 0:
            skipped.append(file_path)
            continue

        total_hit += covered
        total_lines += executable
        details.append((file_path, covered, executable, covered / executable))

    details.sort(key=lambda item: (item[3], -item[2], item[0]))
    ratio = (total_hit / total_lines) if total_lines else None
    return ratio, details, skipped


def percent(v: float) -> str:
    return f"{v * 100:.2f}%"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Summarize repo-wide test coverage.")
    parser.add_argument("--python-json", default="coverage.json")
    parser.add_argument("--dotnet-root", default="artifacts/coverage-dotnet")
    parser.add_argument("--top", type=int, default=5, help="Show top N most undercovered .NET classes")
    parser.add_argument(
        "--dotnet-threshold",
        type=float,
        default=90.0,
        help="Fail when the selected .NET coverage scope is below this percent.",
    )
    parser.add_argument(
        "--dotnet-diff-base",
        default="",
        help="Optional git base ref for changed-line .NET coverage (for example: origin/main).",
    )
    parser.add_argument(
        "--dotnet-enforce-scope",
        choices=("scoped", "changed"),
        default="scoped",
        help="Coverage scope for --enforce-dotnet-threshold.",
    )
    parser.add_argument(
        "--enforce-dotnet-threshold",
        action="store_true",
        help="Return non-zero when the selected .NET coverage scope is below --dotnet-threshold.",
    )
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

    line_rate, classes, files, line_hits = load_cobertura_summary(cob)
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

    changed_rate: float | None = None
    changed_error: str | None = None
    if args.dotnet_diff_base:
        try:
            changed_lines = load_changed_production_lines(args.dotnet_diff_base)
        except RuntimeError as exc:
            changed_error = str(exc)
            print(
                f".NET coverage (changed production .cs executable lines vs {args.dotnet_diff_base}): "
                f"n/a ({changed_error})"
            )
        else:
            changed_rate, changed_files, changed_skipped = summarise_changed_dotnet_lines(changed_lines, line_hits)
            covered = sum(item[1] for item in changed_files)
            total = sum(item[2] for item in changed_files)
            if changed_rate is None:
                print(
                    f".NET coverage (changed production .cs executable lines vs {args.dotnet_diff_base}): "
                    "n/a (no executable changed lines)"
                )
            else:
                print(
                    f".NET coverage (changed production .cs executable lines vs {args.dotnet_diff_base}): "
                    f"{percent(changed_rate)} ({covered}/{total})"
                )
            if changed_files:
                print("Top undercovered changed .NET files:")
                for name, hit, total_lines, rate in changed_files[: args.top]:
                    print(f"- {name}: {percent(rate)} ({hit}/{total_lines})")
            if changed_skipped:
                print(
                    f"Note: {len(changed_skipped)} changed file(s) had no executable coverage-mapped "
                    "lines (for example signature-only edits)."
                )
    elif args.dotnet_enforce_scope == "changed" and args.enforce_dotnet_threshold:
        changed_error = "--dotnet-diff-base is required when enforcing changed scope"

    if changed_error and args.enforce_dotnet_threshold and args.dotnet_enforce_scope == "changed":
        print(f".NET changed-line coverage check failed: {changed_error}.")
        return 1

    threshold_ratio = args.dotnet_threshold / 100.0
    if args.enforce_dotnet_threshold:
        if args.dotnet_enforce_scope == "changed":
            if changed_rate is None:
                print(
                    ".NET changed-line coverage check skipped: no executable changed production .cs lines found."
                )
            elif changed_rate < threshold_ratio:
                print(
                    f".NET changed-line coverage check failed: {percent(changed_rate)} "
                    f"is below required {args.dotnet_threshold:.2f}%."
                )
                return 1
        elif scoped_rate < threshold_ratio:
            print(
                f".NET scoped coverage check failed: {percent(scoped_rate)} "
                f"is below required {args.dotnet_threshold:.2f}%."
            )
            return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
