"""Accept the recorded phase 7 divergences between a Python and a port record of ``run_parity.sh report|capture``.

Temporary; deleted together with the Python package.

    python tools/parity/phase7_divergences.py report <py.json> <cs.json> <py-raised-limit.json>
    python tools/parity/phase7_divergences.py capture <py.json> <cs.json> <case dir>
    python tools/parity/phase7_divergences.py --self-test

Prints the number of accepted divergences and exits 0 when the two records are equal once every accepted difference is
replaced by the port's value; exits 1 with a reason otherwise. The rules (see expected_divergences.md, "Registry, SQL export,
reporting and capture"):

- ``report``: a stage (``html``, ``json_lines``, ``json_lines_unsorted``, ``summary``, ``manifest``) that Python ends with
  ``RecursionError`` (``redact_data`` or ``summarise_metadata`` recursing once per nesting level of the metadata) where the port
  rendered a result. The port's stage and its ``key_order`` child must equal, as canonical JSON text (so ``30`` and ``30.0``, ``1``
  and ``true`` differ), the same stage of a second CPython run of the case with a raised recursion limit
  (``PARITY_REPORT_RECURSION_LIMIT``, the third file); then the Python stage and its ``key_order`` child are replaced by the
  port's. One divergence per stage; a stage the raised-limit run also ends in error, or renders differently, is refused.
- ``capture``: a ``compare`` step that both sides end with ``TypeError`` from sorting detection keys of mixed types, where the
  two messages name the same two operand types in opposite order (Python names them in its set's iteration order, the port in
  insertion order); the Python message is replaced by the port's. One divergence per step. And a step Python ends with
  ``IsADirectoryError: [Errno 21] Is a directory: '<p>'`` where the port ends with ``PermissionError: [Errno 13] Permission denied:
  '<p>'`` for the same ``<p>``, which must name, in the case's committed ``workdir/``, a symlink whose target ends with ``/`` and
  does not exist (the kernel's ``EISDIR`` for such a final link, reported as access denied by the runtime); the Python error is
  replaced by the port's. One divergence per step.
"""

from __future__ import annotations

import json
import os
import re
import sys

sys.setrecursionlimit(20000)

REPORT_STAGES = ("html", "json_lines", "json_lines_unsorted", "summary", "manifest")
TYPE_ERROR = re.compile(r"^'<' not supported between instances of '([^']+)' and '([^']+)'$")
IS_A_DIRECTORY = re.compile(r"^\[Errno 21\] Is a directory: '(.+)'$")
PERMISSION_DENIED = re.compile(r"^\[Errno 13\] Permission denied: '(.+)'$")
WORKDIR_TOKEN = "<workdir>/"


def _load(path: str) -> dict:
    with open(path, encoding="utf-8") as handle:
        record = json.loads(handle.read())
    if not isinstance(record, dict):
        raise SystemExit(f"{path}: not a JSON object")
    return record


def _key_order_child(key_order: object, key: str) -> object:
    for entry in key_order if isinstance(key_order, list) else []:
        if isinstance(entry, list) and len(entry) == 2 and entry[0] == key:
            return entry[1]
    return None


def _set_key_order_child(key_order: object, key: str, child: object) -> None:
    for entry in key_order if isinstance(key_order, list) else []:
        if isinstance(entry, list) and len(entry) == 2 and entry[0] == key:
            entry[1] = child


def accept_report(py: dict, cs: dict, raised: dict | None = None) -> int:
    """``raised`` is the same case's record under a raised recursion limit: the oracle for every stage stock Python cannot render."""

    accepted = 0
    for stage in REPORT_STAGES:
        py_stage = py.get(stage)
        cs_stage = cs.get(stage)
        if py_stage == cs_stage:
            continue
        if (
            isinstance(py_stage, dict)
            and set(py_stage) == {"error"}
            and isinstance(py_stage["error"], dict)
            and py_stage["error"].get("type") == "RecursionError"
            and not (isinstance(cs_stage, dict) and "error" in cs_stage)
            and raised is not None
            and _canonical(raised.get(stage)) == _canonical(cs_stage)
            and _canonical(_key_order_child(raised.get("key_order"), stage)) == _canonical(_key_order_child(cs.get("key_order"), stage))
        ):
            py[stage] = cs_stage
            _set_key_order_child(py.get("key_order"), stage, _key_order_child(cs.get("key_order"), stage))
            accepted += 1
    return accepted


def _swapped_type_error(py_step: object, cs_step: object) -> bool:
    if not (isinstance(py_step, dict) and isinstance(cs_step, dict)):
        return False
    py_error = py_step.get("error")
    cs_error = cs_step.get("error")
    if not (isinstance(py_error, dict) and isinstance(cs_error, dict)):
        return False
    if py_error.get("type") != "TypeError" or cs_error.get("type") != "TypeError":
        return False
    py_match = TYPE_ERROR.match(str(py_error.get("message")))
    cs_match = TYPE_ERROR.match(str(cs_error.get("message")))
    if py_match is None or cs_match is None or py_match.groups() != cs_match.groups()[::-1] or py_match.group(1) == py_match.group(2):
        return False
    rest_py = {key: value for key, value in py_step.items() if key != "error"}
    rest_cs = {key: value for key, value in cs_step.items() if key != "error"}
    return rest_py == rest_cs and py_step.get("command") == "compare"


def _dangling_trailing_slash_link(case_dir: str | None, shown: str) -> bool:
    if case_dir is None:
        return False
    relative = shown[len(WORKDIR_TOKEN) :] if shown.startswith(WORKDIR_TOKEN) else shown
    if os.path.isabs(relative) or ".." in relative.split("/"):
        return False
    link = os.path.join(case_dir, "workdir", relative)
    if not os.path.islink(link):
        return False
    target = os.readlink(link)
    resolved = os.path.join(os.path.dirname(link), target.rstrip("/"))
    return target.endswith("/") and os.path.isdir(os.path.dirname(resolved) or ".") and not os.path.isdir(resolved)


def _eisdir_through_trailing_slash_link(py_step: object, cs_step: object, case_dir: str | None) -> bool:
    if not (isinstance(py_step, dict) and isinstance(cs_step, dict)):
        return False
    py_error = py_step.get("error")
    cs_error = cs_step.get("error")
    if not (isinstance(py_error, dict) and isinstance(cs_error, dict)):
        return False
    if py_error.get("type") != "IsADirectoryError" or cs_error.get("type") != "PermissionError":
        return False
    py_match = IS_A_DIRECTORY.match(str(py_error.get("message")))
    cs_match = PERMISSION_DENIED.match(str(cs_error.get("message")))
    if py_match is None or cs_match is None or py_match.group(1) != cs_match.group(1):
        return False
    rest_py = {key: value for key, value in py_step.items() if key != "error"}
    rest_cs = {key: value for key, value in cs_step.items() if key != "error"}
    return rest_py == rest_cs and _dangling_trailing_slash_link(case_dir, py_match.group(1))


def accept_capture(py: dict, cs: dict, case_dir: str | None = None) -> int:
    accepted = 0
    py_steps = py.get("steps")
    cs_steps = cs.get("steps")
    if not (isinstance(py_steps, list) and isinstance(cs_steps, list) and len(py_steps) == len(cs_steps)):
        return 0
    for py_step, cs_step in zip(py_steps, cs_steps, strict=True):
        if py_step == cs_step:
            continue
        if _swapped_type_error(py_step, cs_step) or _eisdir_through_trailing_slash_link(py_step, cs_step, case_dir):
            py_step["error"] = dict(cs_step["error"])
            accepted += 1
    return accepted


ACCEPTORS = {"report": accept_report, "capture": accept_capture}


def _canonical(record: object) -> str:
    """The JSON text the byte comparison sees: ``json.dumps`` spells an int and a float, and a bool and an int, differently."""

    return json.dumps(record, sort_keys=True, ensure_ascii=True, separators=(",", ":"))


def compare(
    surface: str, py: dict, cs: dict, raised: dict | None = None, case_dir: str | None = None
) -> tuple[int, str | None]:
    """The accepted divergence count and None, or the count and the reason the records still differ."""

    accepted = accept_report(py, cs, raised) if surface == "report" else accept_capture(py, cs, case_dir)
    if _canonical(py) != _canonical(cs):
        return accepted, "records differ beyond the recorded divergences"
    if accepted == 0:
        return accepted, "records are equal; nothing to accept"
    return accepted, None


def _self_test() -> None:
    def report(stage_values: dict, key_order: list) -> dict:
        record = {stage: {"error": {"type": "ValueError", "message": "x"}} for stage in REPORT_STAGES}
        record.update(stage_values)
        record["key_order"] = key_order
        return record

    def copy(record: dict) -> dict:
        return json.loads(json.dumps(record))

    recursion = {"error": {"type": "RecursionError", "message": "maximum recursion depth exceeded"}}
    rendered = {"a": [[["deep"]]]}
    py = report({"html": recursion, "manifest": recursion}, [["html", [["error", None]]], ["manifest", [["error", None]]]])
    cs = report({"html": "<html>", "manifest": rendered}, [["html", None], ["manifest", [["a", None]]]])
    # The raised-limit run renders exactly what the port renders (its other stages may differ: only the RecursionError stages are read).
    raised = report({"html": "<html>", "manifest": rendered, "summary": {"other": 1}}, cs["key_order"])
    assert compare("report", copy(py), copy(cs), copy(raised)) == (2, None)
    # Without the oracle nothing is accepted.
    assert compare("report", copy(py), copy(cs)) == (0, "records differ beyond the recorded divergences")
    # A wrong port rendering fails: the oracle renders something else, in html text, in manifest content, or in the manifest's key order.
    wrong_html = report({"html": "<html>B", "manifest": rendered}, cs["key_order"])
    assert compare("report", copy(py), wrong_html, copy(raised))[1]
    wrong_manifest = report({"html": "<html>", "manifest": {"a": [[["deep", "extra"]]]}}, cs["key_order"])
    assert compare("report", copy(py), wrong_manifest, copy(raised))[1]
    wrong_order = report({"html": "<html>", "manifest": rendered}, [["html", None], ["manifest", [["b", None]]]])
    assert compare("report", copy(py), wrong_order, copy(raised))[1]
    # A port rendering that differs only in number type (30 for 30.0, 1 for true) is refused: Python's == would call them equal.
    typed = report({"html": "<html>", "manifest": {"n": 30, "b": [True, 2]}}, [["html", None], ["manifest", [["n", None], ["b", [None, None]]]]])
    py_typed = report({"html": recursion, "manifest": recursion}, [["html", [["error", None]]], ["manifest", [["error", None]]]])
    assert compare("report", copy(py_typed), copy(typed), copy(typed)) == (2, None)
    for wrong in ({"n": 30.0, "b": [True, 2]}, {"n": 30, "b": [1, 2]}, {"n": 30, "b": [True, 2.0]}):
        assert compare("report", copy(py_typed), report({"html": "<html>", "manifest": wrong}, typed["key_order"]), copy(typed))[1]
    # The oracle still ending in RecursionError (or any error) is refused.
    still_failing = report({"html": "<html>", "manifest": recursion}, cs["key_order"])
    assert compare("report", copy(py), copy(cs), still_failing)[1]
    # The port erroring too, another error type, or a difference elsewhere is refused.
    assert compare("report", copy(py), report({"html": recursion, "manifest": rendered}, cs["key_order"]), copy(raised))[1]
    other = report({"html": {"error": {"type": "TypeError", "message": "m"}}, "manifest": recursion}, py["key_order"])
    assert compare("report", other, copy(cs), copy(raised))[1]
    changed = copy(cs)
    changed["summary"] = {"result": 1}
    assert compare("report", copy(py), changed, copy(raised))[1]
    assert compare("report", copy(cs), copy(cs), copy(raised)) == (0, "records are equal; nothing to accept")

    def step(message: str, **rest: object) -> dict:
        return {"command": "compare", "error": {"type": "TypeError", "message": message}, "stdout": "", "stderr": "", **rest}

    py_c = {
        "steps": [
            step("'<' not supported between instances of 'int' and 'NoneType'"),
            {"command": "compare", "exit_code": 0, "stdout": "s", "stderr": ""},
        ],
        "files": [],
    }
    cs_c = {
        "steps": [
            step("'<' not supported between instances of 'NoneType' and 'int'"),
            {"command": "compare", "exit_code": 0, "stdout": "s", "stderr": ""},
        ],
        "files": [],
    }
    assert compare("capture", json.loads(json.dumps(py_c)), json.loads(json.dumps(cs_c))) == (1, None)
    # The same order, different operand pairs, the same type twice, another error type, a different stdout, or a run step is refused.
    same = {"steps": [step("'<' not supported between instances of 'int' and 'NoneType'")], "files": []}
    assert compare(
        "capture", json.loads(json.dumps(same)), {"steps": [step("'<' not supported between instances of 'int' and 'str'")], "files": []}
    )[1]
    assert compare(
        "capture",
        {"steps": [step("'<' not supported between instances of 'int' and 'int'")], "files": []},
        {"steps": [step("'<' not supported between instances of 'int' and 'int'")], "files": []},
    )[1]
    assert compare(
        "capture",
        json.loads(json.dumps(same)),
        {"steps": [step("'<' not supported between instances of 'NoneType' and 'int'", stdout="x")], "files": []},
    )[1]
    value_error = {
        "steps": [
            {
                "command": "compare",
                "error": {"type": "ValueError", "message": "'<' not supported between instances of 'NoneType' and 'int'"},
                "stdout": "",
                "stderr": "",
            }
        ],
        "files": [],
    }
    assert compare("capture", json.loads(json.dumps(same)), value_error)[1]
    run_step = {"steps": [{**step("'<' not supported between instances of 'NoneType' and 'int'"), "command": "run"}], "files": []}
    assert compare(
        "capture",
        {"steps": [{**step("'<' not supported between instances of 'int' and 'NoneType'"), "command": "run"}], "files": []},
        run_step,
    )[1]
    _self_test_trailing_slash_link()
    print("self-test ok")


def _self_test_trailing_slash_link() -> None:
    import tempfile

    def run_step(error_type: str, message: str, **rest: object) -> dict:
        return {"command": "run", "error": {"type": error_type, "message": message}, "stdout": "", "stderr": "", **rest}

    with tempfile.TemporaryDirectory(prefix="phase7-divergences-") as case_dir:
        os.makedirs(os.path.join(case_dir, "workdir", "out"))
        os.makedirs(os.path.join(case_dir, "workdir", "exists"))
        os.symlink("newdir/", os.path.join(case_dir, "workdir", "out", "c.json"))
        os.symlink("newdir", os.path.join(case_dir, "workdir", "out", "noslash.json"))
        os.symlink("../exists/", os.path.join(case_dir, "workdir", "out", "existing.json"))
        with open(os.path.join(case_dir, "workdir", "afile"), "w", encoding="utf-8") as handle:
            handle.write("x")
        os.symlink("../afile/", os.path.join(case_dir, "workdir", "out", "file-target.json"))
        os.symlink("nodir/sub/", os.path.join(case_dir, "workdir", "out", "missing-parent.json"))

        def records(path: str, py_type: str = "IsADirectoryError", cs_path: str | None = None, **rest: object) -> tuple[dict, dict]:
            py_step = run_step(py_type, f"[Errno 21] Is a directory: '{path}'")
            cs_step = run_step("PermissionError", f"[Errno 13] Permission denied: '{cs_path or path}'", **rest)
            return {"steps": [py_step], "files": []}, {"steps": [cs_step], "files": []}

        assert compare("capture", *records("out/c.json"), case_dir=case_dir) == (1, None)
        assert compare("capture", *records("<workdir>/out/c.json"), case_dir=case_dir) == (1, None)
        assert compare("capture", *records("out/file-target.json"), case_dir=case_dir) == (1, None)
        # Without the case directory, through a link without the trailing slash, to an existing directory, to a target whose parent
        # does not exist, through a missing link, another path, another error type or a difference elsewhere in the step, nothing is
        # accepted.
        assert compare("capture", *records("out/c.json"))[1]
        assert compare("capture", *records("out/noslash.json"), case_dir=case_dir)[1]
        assert compare("capture", *records("out/existing.json"), case_dir=case_dir)[1]
        assert compare("capture", *records("out/missing-parent.json"), case_dir=case_dir)[1]
        assert compare("capture", *records("out/absent.json"), case_dir=case_dir)[1]
        assert compare("capture", *records("out/c.json", cs_path="out/noslash.json"), case_dir=case_dir)[1]
        assert compare("capture", *records("out/c.json", py_type="NotADirectoryError"), case_dir=case_dir)[1]
        assert compare("capture", *records("out/c.json", stdout="x"), case_dir=case_dir)[1]


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        _self_test()
        return 0
    if not argv or argv[0] not in ACCEPTORS or len(argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2
    if argv[0] == "report":
        accepted, reason = compare("report", _load(argv[1]), _load(argv[2]), _load(argv[3]))
    else:
        accepted, reason = compare("capture", _load(argv[1]), _load(argv[2]), case_dir=argv[3])
    if reason is not None:
        print(f"{accepted} accepted; {reason}", file=sys.stderr)
        return 1
    print(accepted)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
