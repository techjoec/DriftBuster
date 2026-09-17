"""Python oracles for the ``cli`` parity surface: the console commands Python has no entry point for, and multi-server with the plan fixes.

Temporary; deleted together with the Python package.

    python tools/parity/cli_oracles.py hunt PATH [--glob G] [--sample-size N] [--exclude P ...] [--placeholder-template T]
    python tools/parity/cli_oracles.py report [ROOT] [--format html|jsonl] [--glob G] [--skip-hunt] [--title T] [--mask-token T ...]
                                              [--placeholder P] [--output FILE]
    python tools/parity/cli_oracles.py multi-server < request.json
    python tools/parity/cli_oracles.py diff BASELINE COMPARISON... [cli.py diff options]

``hunt`` prints ``json.dumps(hunt_path(PATH, rules=default_rules(), ..., return_json=True), ensure_ascii=False, sort_keys=True)`` and a new
line. ``report`` renders the detections of ``Detector().scan_path(ROOT, glob=G)`` and, unless ``--skip-hunt``, the hits of
``hunt_path(ROOT, rules=default_rules(), glob=G)`` with ``render_html_report`` or ``write_json_lines`` to stdout, or writes the text to
``--output`` with ``write_text`` and prints ``Report written to <output>``. Both are the behaviour expected_divergences.md ("Console tool")
records for ``driftbuster hunt`` and ``driftbuster report``. ``default_rules()`` carries fix e as the port ships it.

``multi-server`` is ``python -m driftbuster.multi_server`` with the port's plan fixes applied as the multi-server surface applies them
(py_dump.py's ``_MultiServerFixes`` with fixes a and b on every runner, and ``_PortXmlCanonical`` for fix d, which needs PARITY_PORT_CLI).

``diff`` is ``python -m driftbuster.cli diff`` with fix f: ``--content-type auto`` resolves from detection with py_dump.py's
``_resolve_pair`` (the port's ``ContentTypeResolver.ResolvePair``) instead of ``_guess_content_type``'s suffix allowlist.

PARITY_CLI_STOCK=1 runs every oracle without the fixes (the ``oracle-fixes`` acceptor of cli_parity.py).
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sys
from pathlib import Path

import py_dump

from driftbuster.core.detector import Detector
from driftbuster.hunt import hunt_path
from driftbuster.reporting.html import render_html_report
from driftbuster.reporting.json_lines import write_json_lines


def _stock() -> bool:
    return os.environ.get("PARITY_CLI_STOCK") == "1"


def _rules():
    return py_dump._hunt_rules(fix_e=not _stock())


def run_multi_server() -> int:
    from driftbuster import multi_server as ms

    if _stock():
        return ms.main()
    runner_type = ms.MultiServerRunner

    def instrumented_runner(*args, **kwargs):
        runner = runner_type(*args, **kwargs)
        py_dump._MultiServerFixes(ms, runner, fixes="ab")
        return runner

    ms.MultiServerRunner = instrumented_runner  # type: ignore[assignment, misc]
    try:
        with py_dump._PortXmlCanonical(ms):
            return ms.main()
    finally:
        ms.MultiServerRunner = runner_type  # type: ignore[misc]


def run_diff(argv: list[str]) -> int:
    from driftbuster import cli

    if _stock():
        return cli.main(["diff", *argv])
    guess = cli._guess_content_type

    def resolve(baseline: Path, candidate: Path, explicit: str = "auto") -> str:
        return explicit if explicit != "auto" else py_dump._resolve_pair(baseline, candidate)

    cli._guess_content_type = resolve
    try:
        return cli.main(["diff", *argv])
    finally:
        cli._guess_content_type = guess


def run_hunt(args: argparse.Namespace) -> int:
    hits = hunt_path(
        Path(args.path),
        rules=_rules(),
        glob=args.glob,
        sample_size=args.sample_size,
        exclude_patterns=args.exclude,
        return_json=True,
        placeholder_template=args.placeholder_template,
    )
    sys.stdout.write(json.dumps(hits, ensure_ascii=False, sort_keys=True) + "\n")
    return 0


def run_report(args: argparse.Namespace) -> int:
    root = Path(args.root)
    matches = [match for _, match in Detector().scan_path(root, glob=args.glob) if match is not None]
    hunt_hits = [] if args.skip_hunt else hunt_path(root, rules=_rules(), glob=args.glob)
    if args.format == "html":
        content = render_html_report(
            matches, title=args.title, hunt_hits=hunt_hits, mask_tokens=args.mask_token, placeholder=args.placeholder
        )
    else:
        stream = io.StringIO()
        write_json_lines(matches, stream, hunt_hits=hunt_hits, mask_tokens=args.mask_token, placeholder=args.placeholder)
        content = stream.getvalue()
    if args.output:
        Path(args.output).write_text(content, encoding="utf-8")
        sys.stdout.write(f"Report written to {args.output}\n")
    else:
        sys.stdout.write(content)
    return 0


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if argv[:1] == ["diff"]:
        return run_diff(argv[1:])
    parser = argparse.ArgumentParser(prog="cli_oracles.py")
    commands = parser.add_subparsers(dest="command", required=True)
    hunt = commands.add_parser("hunt")
    hunt.add_argument("path")
    hunt.add_argument("--glob", default="**/*")
    hunt.add_argument("--sample-size", type=int, default=128 * 1024)
    hunt.add_argument("--exclude", action="append", default=[])
    hunt.add_argument("--placeholder-template", default="{{{{ {token_name} }}}}")
    hunt.set_defaults(func=run_hunt)
    report = commands.add_parser("report")
    report.add_argument("root", nargs="?", default=".")
    report.add_argument("--format", choices=("html", "jsonl"), default="html")
    report.add_argument("--glob", default="**/*")
    report.add_argument("--skip-hunt", action="store_true")
    report.add_argument("--title", default="DriftBuster Report")
    report.add_argument("--mask-token", action="append", default=[])
    report.add_argument("--placeholder", default="[REDACTED]")
    report.add_argument("--output")
    report.set_defaults(func=run_report)
    commands.add_parser("multi-server").set_defaults(func=lambda _args: run_multi_server())
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
