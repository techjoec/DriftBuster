"""Run the ``cli`` parity surface: each case runs a Python entry point and the ``driftbuster`` console tool the same way and compares them.

Temporary; deleted together with the Python package.

    python tools/parity/cli_parity.py run --cli <driftbuster exe> --work <scratch dir> <case dir>...
    python tools/parity/cli_parity.py --self-test

A case is a directory holding ``case.json``:

    python          argv after ``python`` (a module run with ``-m`` or a script path, then its arguments)
    port            argv after ``driftbuster``
    stdin           optional text fed to both sides (no stdin otherwise)
    workdir         optional tree copied as the working directory: a path relative to the case directory or starting with ``{repo}``
                    (default: the case's ``workdir/`` when it exists, otherwise an empty directory)
    copy            optional list of {"from", "to"}: extra files or trees copied into the working directory
    databases       optional {relative path: SQL script}: SQLite databases built in the working directory with ``executescript``
    env             optional {name: value} added to both sides' environment
    compare_stderr  compare stderr bytes as well (default false)
    accept          optional list of acceptor names (below) the case needs

``{repo}`` and ``{workdir}`` are replaced in argv, stdin, env and copy sources. Both sides run at the same absolute working directory, built
afresh for each side, with the same environment (PYTHONPATH naming the repository's ``src``, no bytecode files, and DRIFTBUSTER_DATA_ROOT
under the working directory unless the case sets it). Compared: the exit code, stdout bytes, stderr bytes when ``compare_stderr`` is set,
and every file created, changed or removed under the working directory (sorted relative paths, bytes; a symlink by its target).

Acceptors (see expected_divergences.md, "Console tool"). Each rewrites both runs before the comparison, and every acceptor a case names
must change at least one of them, so a stale declaration fails:

    parse-error      D2: both sides exit 2 with empty stdout and a parse error on stderr (argparse's ``usage:`` block and
                     ``<prog>: error:`` line; the port's ``driftbuster: error:`` lines and help hint); both stderr texts become one token.
    usage-line       D2: a ``parser.error`` raised by command code; Python's ``usage:`` block (the line and its indented continuation
                     lines) directly before the ``<prog>: error: <message>`` line is removed, and the rest of stderr is compared.
    traceback        D2: an exception the command does not handle; Python's ``Traceback (most recent call last):`` block is reduced to
                     its last line, which the port prints alone.
    json-decode-reason  ``multi-server`` given a request that is not JSON: Python's error record names the ``JSONDecodeError`` reason
                     and position (``Invalid JSON payload: <reason>: line L column C (char N)``) where the port's reads ``Invalid JSON
                     payload: invalid JSON document``; the Python record's message is replaced by the port's.
    json-load-reason ``detection-profile`` given a store that is not JSON: Python's ``error: Failed to parse JSON from <path>: <reason>:
                     line L column C (char N)`` stderr line becomes the port's ``error: Failed to parse JSON from <path>: invalid JSON
                     document`` when the port printed exactly that line.
    empty-response-mappings  ``multi-server`` with no plans: Python's result record carries ``"results": [], "catalog": {},
                     "drilldown": {}`` where the port's carries three empty arrays; Python's two mappings are respelled as arrays.
    run-clock        Wall-clock readings: an ISO 8601 UTC timestamp (``isoformat()`` with ``+00:00`` or ``Z``) or a capture id
                     (``%Y%m%dT%H%M%SZ``) in stdout, stderr, a written file's bytes or its path is replaced by its shape (digits as ``#``)
                     only when it falls inside that side's run window, so the format is still compared.
    capture-durations  ``capture run``'s manifest ``durations`` (``detection_seconds``, ``hunt_seconds``, ``total_seconds``): the
                     measured seconds, each a non-negative JSON number rounded to three places on both sides, become ``0``.
    oracle-fixes     The case's Python side is a cli_oracles.py command that applies the approved plan fixes as the port ships them
                     (fix e in the hunt rules; fixes a, b and d in multi-server; fix f in diff). A third Python run without them
                     (PARITY_CLI_STOCK=1)
                     must differ from the fixed run, and counts as one expected divergence.

Prints ``ok   cli <case>`` or ``FAIL cli <case>: <reason>`` with the differences, then ``#counts <cases> <expected divergences>``;
exits 1 when any case fails.
"""

from __future__ import annotations

import difflib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ACCEPTORS = (
    "parse-error",
    "usage-line",
    "traceback",
    "json-decode-reason",
    "json-load-reason",
    "empty-response-mappings",
    "run-clock",
    "capture-durations",
    "oracle-fixes",
)
PARSE_ERROR_TOKEN = b"<parse error>\n"
PORT_HELP_HINT = b"Run 'driftbuster --help' for usage.\n"
ISO_UTC = re.compile(rb"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?(?:\+00:00|Z)")
CAPTURE_ID = re.compile(rb"(?<!\d)\d{8}T\d{6}Z")
DURATION_KEYS = ("detection_seconds", "hunt_seconds", "total_seconds")
CLOCK_SLACK_SECONDS = 2.0
TIMEOUT_SECONDS = float(os.environ.get("PARITY_PORT_TIMEOUT", "600"))


@dataclass
class Run:
    exit_code: int
    stdout: bytes
    stderr: bytes
    files: dict[str, bytes]
    started: float = 0.0
    finished: float = 0.0
    applied: set[str] = field(default_factory=set)


# ----------------------------------------------------------------------------------------------------------------------------------
# Acceptors: each takes both runs and rewrites them in place, recording its name in ``applied`` when it changed that run.
# ----------------------------------------------------------------------------------------------------------------------------------


def _python_usage_block(text: bytes) -> tuple[int, int] | None:
    """The span of an argparse usage block (``usage: ...`` and its indented continuation lines) followed by a ``<prog>: error:`` line."""

    lines = text.splitlines(keepends=True)
    offset = 0
    for index, line in enumerate(lines):
        if line.startswith(b"usage: "):
            end = index + 1
            while end < len(lines) and lines[end].startswith(b" ") and lines[end].strip():
                end += 1
            if end < len(lines) and re.match(rb"^[^:\n]+: error: ", lines[end]):
                return offset, offset + sum(len(entry) for entry in lines[index:end])
        offset += len(line)
    return None


def accept_parse_error(py: Run, cs: Run) -> None:
    py_parse = py.exit_code == 2 and py.stdout == b"" and _python_usage_block(py.stderr) is not None
    cs_lines = cs.stderr.splitlines(keepends=True)
    cs_parse = (
        cs.exit_code == 2
        and cs.stdout == b""
        and len(cs_lines) >= 2
        and cs_lines[-1] == PORT_HELP_HINT
        and all(line.startswith(b"driftbuster: error: ") for line in cs_lines[:-1])
    )
    if py_parse and cs_parse:
        py.stderr = cs.stderr = PARSE_ERROR_TOKEN
        py.applied.add("parse-error")
        cs.applied.add("parse-error")


def accept_usage_line(py: Run, cs: Run) -> None:
    span = _python_usage_block(py.stderr)
    if span is None or py.exit_code != 2:
        return
    py.stderr = py.stderr[: span[0]] + py.stderr[span[1] :]
    py.applied.add("usage-line")


def accept_traceback(py: Run, cs: Run) -> None:
    marker = b"Traceback (most recent call last):\n"
    start = py.stderr.find(marker)
    if start < 0 or py.exit_code != 1:
        return
    tail = py.stderr[start:].splitlines(keepends=True)
    # The block is the marker, indented frame lines (and caret lines), then the exception line: the first unindented line after it.
    for index, line in enumerate(tail[1:], start=1):
        if not line.startswith(b" "):
            py.stderr = py.stderr[:start] + line + b"".join(tail[index + 1 :])
            py.applied.add("traceback")
            return


JSON_DECODE_PY = re.compile(
    rb'^\{"type": "error", "message": "Invalid JSON payload: [A-Z][^":\\\n]*: line \d+ column \d+ \(char \d+\)"\}\n$'
)
JSON_DECODE_PORT = b'{"type": "error", "message": "Invalid JSON payload: invalid JSON document"}\n'


def accept_json_decode_reason(py: Run, cs: Run) -> None:
    if py.exit_code == cs.exit_code == 1 and cs.stdout == JSON_DECODE_PORT and JSON_DECODE_PY.match(py.stdout):
        py.stdout = cs.stdout
        py.applied.add("json-decode-reason")


JSON_LOAD_PY = re.compile(
    rb"^(error: Failed to parse JSON from [^\n]+): [A-Z][^:\n]*: line \d+ column \d+ \(char \d+\)$", re.MULTILINE
)


def accept_json_load_reason(py: Run, cs: Run) -> None:
    if py.exit_code != 1 or cs.exit_code != 1:
        return
    cs_lines = set(cs.stderr.splitlines())

    def swap(match: re.Match[bytes]) -> bytes:
        port_line = match.group(1) + b": invalid JSON document"
        return port_line if port_line in cs_lines else match.group(0)

    rewritten = JSON_LOAD_PY.sub(swap, py.stderr)
    if rewritten != py.stderr:
        py.stderr = rewritten
        py.applied.add("json-load-reason")


EMPTY_MAPPINGS_PY = b'"results": [], "catalog": {}, "drilldown": {}'
EMPTY_MAPPINGS_PORT = b'"results": [], "catalog": [], "drilldown": []'


def accept_empty_response_mappings(py: Run, cs: Run) -> None:
    if py.exit_code == cs.exit_code == 0 and EMPTY_MAPPINGS_PY in py.stdout and EMPTY_MAPPINGS_PORT in cs.stdout:
        py.stdout = py.stdout.replace(EMPTY_MAPPINGS_PY, EMPTY_MAPPINGS_PORT)
        py.applied.add("empty-response-mappings")


def _clock_rewrite(data: bytes, started: float, finished: float) -> bytes:
    def inside(moment: datetime) -> bool:
        stamp = moment.timestamp()
        return started - CLOCK_SLACK_SECONDS <= stamp <= finished + CLOCK_SLACK_SECONDS

    def shape(match: re.Match[bytes]) -> bytes:
        return re.sub(rb"\d", b"#", match.group(0))

    def iso(match: re.Match[bytes]) -> bytes:
        text = match.group(0).decode("ascii").replace("Z", "+00:00")
        try:
            moment = datetime.fromisoformat(text)
        except ValueError:
            return match.group(0)
        return shape(match) if inside(moment) else match.group(0)

    def capture_id(match: re.Match[bytes]) -> bytes:
        try:
            moment = datetime.strptime(match.group(0).decode("ascii"), "%Y%m%dT%H%M%SZ").replace(tzinfo=UTC)
        except ValueError:
            return match.group(0)
        return shape(match) if inside(moment) else match.group(0)

    return CAPTURE_ID.sub(capture_id, ISO_UTC.sub(iso, data))


def accept_run_clock(py: Run, cs: Run) -> None:
    for run in (py, cs):
        stdout = _clock_rewrite(run.stdout, run.started, run.finished)
        stderr = _clock_rewrite(run.stderr, run.started, run.finished)
        files = {
            _clock_rewrite(os.fsencode(name), run.started, run.finished).decode("utf-8", "surrogateescape"): _clock_rewrite(
                data, run.started, run.finished
            )
            for name, data in run.files.items()
        }
        if stdout != run.stdout or stderr != run.stderr or files != run.files:
            run.applied.add("run-clock")
        run.stdout, run.stderr, run.files = stdout, stderr, files


def _duration_rewrite(data: bytes) -> bytes:
    """A capture manifest (``json.dumps(manifest, indent=2, sort_keys=True)``) with its three measured durations replaced by ``0``."""

    text = data.decode("utf-8", "surrogateescape")
    block = re.search(r'\n  "durations": \{\n((?:    "[a-z_]+": [^\n]+\n)+)  \}', text)
    if block is None:
        return data
    entries = re.findall(r'    "([a-z_]+)": ([^,\n]+),?\n', block.group(1))
    if tuple(name for name, _ in entries) != DURATION_KEYS:
        return data
    for _, value in entries:
        if not re.fullmatch(r"\d+(?:\.\d{1,3})?", value):
            return data
    replaced = "".join(f'    "{name}": 0{"," if index < len(entries) - 1 else ""}\n' for index, (name, _) in enumerate(entries))
    return (text[: block.start(1)] + replaced + text[block.end(1) :]).encode("utf-8", "surrogateescape")


def accept_capture_durations(py: Run, cs: Run) -> None:
    for run in (py, cs):
        files = {name: _duration_rewrite(data) if name.endswith("-manifest.json") else data for name, data in run.files.items()}
        if files != run.files:
            run.applied.add("capture-durations")
        run.files = files


ACCEPTOR_FUNCTIONS = {
    "parse-error": accept_parse_error,
    "usage-line": accept_usage_line,
    "traceback": accept_traceback,
    "json-decode-reason": accept_json_decode_reason,
    "json-load-reason": accept_json_load_reason,
    "empty-response-mappings": accept_empty_response_mappings,
    "run-clock": accept_run_clock,
    "capture-durations": accept_capture_durations,
}


# ----------------------------------------------------------------------------------------------------------------------------------
# Running a case
# ----------------------------------------------------------------------------------------------------------------------------------


def _substitute(value: str, workdir: Path) -> str:
    return value.replace("{repo}", str(REPO)).replace("{workdir}", str(workdir))


def _snapshot(root: Path) -> dict[str, bytes]:
    files: dict[str, bytes] = {}
    for directory, subdirectories, names in os.walk(root, followlinks=False):
        for name in [*names, *[entry for entry in subdirectories if os.path.islink(os.path.join(directory, entry))]]:
            path = os.path.join(directory, name)
            relative = Path(os.path.relpath(path, root)).as_posix()
            if os.path.islink(path):
                files[relative] = b"<symlink> " + os.fsencode(os.readlink(path))
            else:
                try:
                    with open(path, "rb") as handle:
                        files[relative] = handle.read()
                except OSError as exc:
                    files[relative] = f"<unreadable> {exc.strerror}".encode()
    return files


def _build_workdir(case_dir: Path, case: dict, workdir: Path) -> None:
    if workdir.exists():
        subprocess.run(["chmod", "-R", "u+rwX", str(workdir)], check=False)
        shutil.rmtree(workdir)
    source = case.get("workdir")
    if isinstance(source, str) and source.startswith("{repo}"):
        source_path = Path(_substitute(source, workdir))
    else:
        source_path = case_dir / source if source else case_dir / "workdir"
    if source_path.is_dir():
        shutil.copytree(source_path, workdir, symlinks=True)
    elif source:
        raise SystemExit(f"workdir {source_path} is not a directory")
    else:
        workdir.mkdir(parents=True)
    for entry in case.get("copy", []):
        origin = Path(_substitute(entry["from"], workdir))
        if not origin.is_absolute():
            origin = case_dir / origin
        target = workdir / entry["to"]
        target.parent.mkdir(parents=True, exist_ok=True)
        if origin.is_dir():
            shutil.copytree(origin, target, symlinks=True)
        else:
            shutil.copy2(origin, target, follow_symlinks=False)
    for relative, script in case.get("databases", {}).items():
        target = workdir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(target)
        try:
            connection.executescript(script)
            connection.commit()
        finally:
            connection.close()


def _environment(case: dict, workdir: Path, extra: dict[str, str] | None = None) -> dict[str, str]:
    env = dict(os.environ)
    for name in list(env):
        if name.startswith("DRIFTBUSTER_") or name.startswith("PARITY_"):
            del env[name]
    env.update(
        {
            "PYTHONPATH": str(REPO / "src"),
            "PYTHONDONTWRITEBYTECODE": "1",
            "PYTHON_COLORS": "0",
            "NO_COLOR": "1",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DRIFTBUSTER_DATA_ROOT": str(workdir / ".driftbuster-data"),
        }
    )
    env.update({name: _substitute(str(value), workdir) for name, value in case.get("env", {}).items()})
    env.update(extra or {})
    return env


def _run_side(case_dir: Path, case: dict, workdir: Path, argv: list[str], extra_env: dict[str, str] | None = None) -> Run:
    _build_workdir(case_dir, case, workdir)
    before = _snapshot(workdir)
    stdin = case.get("stdin")
    started = time.time()
    try:
        completed = subprocess.run(
            [_substitute(value, workdir) for value in argv],
            cwd=workdir,
            env=_environment(case, workdir, extra_env),
            input=_substitute(stdin, workdir).encode("utf-8") if isinstance(stdin, str) else None,
            stdin=None if isinstance(stdin, str) else subprocess.DEVNULL,
            capture_output=True,
            timeout=TIMEOUT_SECONDS,
            check=False,
        )
        exit_code, stdout, stderr = completed.returncode, completed.stdout, completed.stderr
    except subprocess.TimeoutExpired as exc:
        exit_code, stdout, stderr = -1, exc.stdout or b"", (exc.stderr or b"") + b"\n<timed out>\n"
    finished = time.time()
    after = _snapshot(workdir)
    changed = {name: data for name, data in after.items() if before.get(name) != data}
    changed.update({name: b"<removed>" for name in before if name not in after})
    return Run(exit_code, stdout, stderr, dict(sorted(changed.items())), started, finished)


def _render(run: Run, compare_stderr: bool) -> list[str]:
    lines = [f"exit code: {run.exit_code}", "--- stdout"]
    lines += run.stdout.decode("utf-8", "backslashreplace").splitlines()
    if compare_stderr:
        lines.append("--- stderr")
        lines += run.stderr.decode("utf-8", "backslashreplace").splitlines()
    for name, data in run.files.items():
        lines.append(f"--- file {name} ({len(data)} bytes)")
        lines += data.decode("utf-8", "backslashreplace").splitlines()
    return lines


def _same(py: Run, cs: Run, compare_stderr: bool) -> bool:
    return (
        py.exit_code == cs.exit_code
        and py.stdout == cs.stdout
        and (not compare_stderr or py.stderr == cs.stderr)
        and py.files == cs.files
    )


def run_case(case_dir: Path, cli: str, work: Path) -> tuple[bool, int, list[str]]:
    """(passed, expected divergences, report lines) for one case."""

    label = case_dir.relative_to(REPO).as_posix() if case_dir.is_relative_to(REPO) else str(case_dir)
    case = json.loads((case_dir / "case.json").read_text(encoding="utf-8"))
    unknown = set(case) - {"python", "port", "stdin", "workdir", "copy", "databases", "env", "compare_stderr", "accept", "description"}
    accept = list(case.get("accept", []))
    bad_accept = [name for name in accept if name not in ACCEPTORS]
    if unknown or bad_accept or not isinstance(case.get("python"), list) or not isinstance(case.get("port"), list):
        reason = f"case.json has unknown keys {sorted(unknown)}, unknown acceptors {bad_accept} or no python/port argv"
        return False, 0, [f"FAIL cli {label}: {reason}"]
    compare_stderr = bool(case.get("compare_stderr", False))
    workdir = work / "run"
    python = [sys.executable, *case["python"]]
    port = [cli, *case["port"]]

    oracle_env = {"PARITY_PORT_CLI": cli}
    py = _run_side(case_dir, case, workdir, python, oracle_env)
    cs = _run_side(case_dir, case, workdir, port)
    stock: Run | None = None
    if "oracle-fixes" in accept:
        stock = _run_side(case_dir, case, workdir, python, {**oracle_env, "PARITY_CLI_STOCK": "1"})
    shutil.rmtree(workdir, ignore_errors=True)

    raw_same = _same(py, cs, compare_stderr)
    for name in accept:
        if name in ACCEPTOR_FUNCTIONS:
            ACCEPTOR_FUNCTIONS[name](py, cs)
    expected = 0
    problems: list[str] = []
    if stock is not None:
        for name in accept:
            if name in ACCEPTOR_FUNCTIONS:
                ACCEPTOR_FUNCTIONS[name](stock, Run(cs.exit_code, cs.stdout, cs.stderr, dict(cs.files), cs.started, cs.finished))
        if _same(stock, py, True):
            problems.append("oracle-fixes declared but the stock oracle gives the same output")
        else:
            expected += 1
    for name in accept:
        if name in ACCEPTOR_FUNCTIONS and name not in py.applied and name not in cs.applied:
            problems.append(f"acceptor {name} declared but changed neither side")
    same = _same(py, cs, compare_stderr)
    if same and not raw_same:
        expected += 1
    lines: list[str] = []
    if same and not problems:
        note = f": {expected} expected divergence(s) ({', '.join(accept)})" if expected else ""
        lines.append(f"ok   cli {label}{note}")
        return True, expected, lines
    lines.append(f"FAIL cli {label}: {'; '.join(problems) if problems else 'runs differ'}")
    if not same:
        diff = difflib.unified_diff(_render(py, compare_stderr), _render(cs, compare_stderr), "python", "port", lineterm="")
        lines += [line[:2000] for line in list(diff)[:400]]
    return False, expected, lines


# ----------------------------------------------------------------------------------------------------------------------------------
# Self-test
# ----------------------------------------------------------------------------------------------------------------------------------


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, condition: bool) -> None:
        if not condition:
            failures.append(name)

    def run(
        exit_code: int = 0, stdout: bytes = b"", stderr: bytes = b"", files: dict[str, bytes] | None = None, started: float = 0.0
    ) -> Run:
        return Run(exit_code, stdout, stderr, dict(files or {}), started, started + 1.0)

    usage = b"usage: driftbuster diff [-h] [--content-type {auto,text,xml}]\n" + b" " * 24 + b"baseline comparisons [comparisons ...]\n"

    # parse-error
    py, cs = run(2, stderr=usage + b"driftbuster diff: error: the following arguments are required: comparisons\n"), run(
        2, stderr=b"driftbuster: error: Required argument missing for command: 'diff'.\n" + PORT_HELP_HINT
    )
    accept_parse_error(py, cs)
    check("parse-error accepts both parse errors", py.stderr == cs.stderr == PARSE_ERROR_TOKEN and "parse-error" in py.applied)
    py, cs = run(2, stderr=usage + b"driftbuster diff: error: x\n"), run(1, stderr=b"driftbuster: error: x\n" + PORT_HELP_HINT)
    accept_parse_error(py, cs)
    check("parse-error refuses a port exit code other than 2", not py.applied and py.stderr != cs.stderr)
    py, cs = run(2, stdout=b"out", stderr=usage + b"p: error: x\n"), run(2, stderr=b"driftbuster: error: x\n" + PORT_HELP_HINT)
    accept_parse_error(py, cs)
    check("parse-error refuses stdout output", not py.applied)
    py, cs = run(2, stderr=b"driftbuster: error: Path does not exist: /x\n"), run(2, stderr=b"driftbuster: error: x\n" + PORT_HELP_HINT)
    accept_parse_error(py, cs)
    check("parse-error refuses a Python error without usage", not py.applied)
    py, cs = run(2, stderr=usage + b"p: error: x\n"), run(2, stderr=b"driftbuster: error: x\n")
    accept_parse_error(py, cs)
    check("parse-error refuses a port error without the help hint", not cs.applied)

    # usage-line
    py, cs = run(2, stderr=usage + b"driftbuster diff: error: Baseline path does not exist: /w/x\n"), run(
        2, stderr=b"driftbuster diff: error: Baseline path does not exist: /w/x\n"
    )
    accept_usage_line(py, cs)
    check("usage-line removes the usage block", py.stderr == cs.stderr)
    py = run(2, stderr=b"usage: x\nsomething else\np: error: y\n")
    accept_usage_line(py, run(2))
    check("usage-line refuses a block not followed by the error line", "usage-line" not in py.applied)
    py = run(1, stderr=usage + b"p: error: y\n")
    accept_usage_line(py, run(1))
    check("usage-line refuses exit code 1", "usage-line" not in py.applied)

    # traceback
    trace = b'Traceback (most recent call last):\n  File "x.py", line 1, in <module>\n    main()\n    ~~~~^^\nValueError: bad\n'
    py = run(1, stderr=b"warning: first\n" + trace)
    accept_traceback(py, run(1))
    check("traceback keeps the last line", py.stderr == b"warning: first\nValueError: bad\n")
    py = run(0, stderr=trace)
    accept_traceback(py, run(0))
    check("traceback refuses exit code 0", "traceback" not in py.applied)

    # json-decode-reason
    for reason in (b"Expecting value", b"Expecting ',' delimiter", b"Extra data", b"Expecting property name enclosed in double quotes"):
        py = run(1, stdout=b'{"type": "error", "message": "Invalid JSON payload: ' + reason + b': line 1 column 12 (char 11)"}\n')
        cs = run(1, stdout=JSON_DECODE_PORT)
        accept_json_decode_reason(py, cs)
        check(f"json-decode-reason accepts {reason!r}", py.stdout == cs.stdout)
    py = run(1, stdout=b'{"type": "error", "message": "Unsupported schema version: 9"}\n')
    accept_json_decode_reason(py, run(1, stdout=JSON_DECODE_PORT))
    check("json-decode-reason refuses another error", not py.applied)
    py = run(1, stdout=b'{"type": "error", "message": "Invalid JSON payload: Expecting value: line 1 column 12 (char 11)"}\n')
    accept_json_decode_reason(py, run(1, stdout=b'{"type": "error", "message": "Invalid JSON payload: other"}\n'))
    check("json-decode-reason refuses another port message", not py.applied)
    py = run(0, stdout=b'{"type": "error", "message": "Invalid JSON payload: Expecting value: line 1 column 12 (char 11)"}\n')
    accept_json_decode_reason(py, run(0, stdout=JSON_DECODE_PORT))
    check("json-decode-reason refuses exit code 0", not py.applied)

    # json-load-reason
    py_line = b"error: Failed to parse JSON from store.json: Illegal trailing comma before end of array: line 1 column 16 (char 15)\n"
    port_line = b"error: Failed to parse JSON from store.json: invalid JSON document\n"
    py, cs = run(1, stderr=py_line), run(1, stderr=port_line)
    accept_json_load_reason(py, cs)
    check("json-load-reason accepts the reason", py.stderr == cs.stderr and "json-load-reason" in py.applied)
    py = run(1, stderr=py_line)
    accept_json_load_reason(py, run(1, stderr=b"error: Failed to parse JSON from other.json: invalid JSON document\n"))
    check("json-load-reason refuses another path", not py.applied)
    py = run(1, stderr=py_line)
    accept_json_load_reason(py, run(0, stderr=port_line))
    check("json-load-reason refuses another exit code", not py.applied)
    py = run(1, stderr=b"error: Unknown profile: x\n")
    accept_json_load_reason(py, run(1, stderr=port_line))
    check("json-load-reason refuses another error", not py.applied)

    # empty-response-mappings
    py_record = b'{"type": "result", "payload": {"results": [], "catalog": {}, "drilldown": {}, "summary": {}}}\n'
    port_record = b'{"type": "result", "payload": {"results": [], "catalog": [], "drilldown": [], "summary": {}}}\n'
    py, cs = run(stdout=py_record), run(stdout=port_record)
    accept_empty_response_mappings(py, cs)
    check("empty-response-mappings respells the mappings", py.stdout == cs.stdout)
    py = run(stdout=py_record.replace(b'"results": []', b'"results": [1]'))
    accept_empty_response_mappings(py, run(stdout=port_record))
    check("empty-response-mappings refuses non-empty results", not py.applied)
    py = run(stdout=py_record)
    accept_empty_response_mappings(py, run(stdout=py_record))
    check("empty-response-mappings refuses a port response with mappings", not py.applied)

    # run-clock
    now = time.time()
    stamp = datetime.fromtimestamp(now, UTC)
    iso = stamp.isoformat().encode()
    old = b"2001-02-03T04:05:06.123456+00:00"
    cid = stamp.strftime("%Y%m%dT%H%M%SZ").encode()
    manifest_name = cid.decode() + "-manifest.json"
    py = run(stdout=b"at " + iso + b" and " + old + b"\n", files={manifest_name: b'{"captured_at": "' + iso + b'"}'}, started=now - 0.5)
    cs = run(stdout=b"at " + iso + b" and " + old + b"\n", started=now - 0.5)
    accept_run_clock(py, cs)
    check("run-clock shapes a reading inside the window", b"at ####-##-##T##:##:##" in py.stdout and old in py.stdout)
    check("run-clock shapes a capture id in a file name", "########T######Z-manifest.json" in py.files)
    check("run-clock shapes a file's timestamp", b'"captured_at": "####-##-##T##:##:##' in next(iter(py.files.values())))
    micro = run(stdout=stamp.replace(microsecond=0).isoformat().encode(), started=now - 0.5)
    other = run(stdout=stamp.replace(microsecond=5).isoformat().encode(), started=now - 0.5)
    accept_run_clock(micro, other)
    check("run-clock keeps the shape (fraction digits) comparable", micro.stdout != other.stdout)
    late = run(stdout=b"2001-02-03T04:05:06+00:00", started=now)
    accept_run_clock(late, run(started=now))
    check("run-clock leaves a reading outside the window", late.stdout == b"2001-02-03T04:05:06+00:00" and not late.applied)
    local = run(stdout=stamp.replace(tzinfo=None).isoformat().encode(), started=now - 0.5)
    accept_run_clock(local, run(started=now))
    check("run-clock leaves a timestamp without a UTC designator", not local.applied)

    # capture-durations
    manifest = (
        b'{\n  "capture": {},\n  "durations": {\n    "detection_seconds": 0.012,\n    "hunt_seconds": 0.0,\n    "total_seconds": 1\n  },\n'
        b'  "x": 1\n}'
    )
    py = run(files={"captures/a-manifest.json": manifest, "captures/a-snapshot.json": manifest})
    accept_capture_durations(py, run())
    zeroed = py.files["captures/a-manifest.json"]
    check(
        "capture-durations zeroes the three durations",
        zeroed == manifest.replace(b"0.012", b"0").replace(b"0.0,", b"0,").replace(b'"total_seconds": 1', b'"total_seconds": 0'),
    )
    check("capture-durations leaves other files", py.files["captures/a-snapshot.json"] == manifest)
    negative = run(files={"a-manifest.json": manifest.replace(b"0.012", b"-0.012")})
    accept_capture_durations(negative, run())
    check("capture-durations refuses a negative duration", not negative.applied)
    extra_key = run(files={"a-manifest.json": manifest.replace(b'"hunt_seconds"', b'"other_seconds"')})
    accept_capture_durations(extra_key, run())
    check("capture-durations refuses other keys", not extra_key.applied)
    precise = run(files={"a-manifest.json": manifest.replace(b"0.012", b"0.0123")})
    accept_capture_durations(precise, run())
    check("capture-durations refuses more than three places", not precise.applied)

    # comparison
    check("_same compares files", not _same(run(files={"a": b"1"}), run(files={"a": b"2"}), False))
    check("_same ignores stderr unless asked", _same(run(stderr=b"a"), run(stderr=b"b"), False))
    check("_same compares stderr when asked", not _same(run(stderr=b"a"), run(stderr=b"b"), True))

    for name in failures:
        print(f"self-test failed: {name}", file=sys.stderr)
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    if len(argv) >= 5 and argv[0] == "run" and argv[1] == "--cli" and argv[3] == "--work":
        cli, work, cases = argv[2], Path(argv[4]), [Path(entry).resolve() for entry in argv[5:]]
        work.mkdir(parents=True, exist_ok=True)
        status = 0
        expected_total = 0
        for case_dir in cases:
            passed, expected, lines = run_case(case_dir, cli, work)
            expected_total += expected
            status |= 0 if passed else 1
            print("\n".join(lines), flush=True)
        print(f"#counts {len(cases)} {expected_total}")
        return status
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
