"""Fix g reference model behind the secrets normaliser of run_parity.sh.

Temporary; deleted together with the Python package.

Usage:
    secrets_guard.py <python.jsonl> <root> [--ruleset R]

``copy_with_secret_filter`` never returns on a line where rules keep matching inside the ``[SECRET]`` text they inserted, so
``py_dump.py`` gives up on such a file (``"error": "Timeout"``). The port applies the rule documented in
``expected_divergences.md`` ("Fix g"): it replaces exactly as Python does until a line has taken more than ``GUARD_BUDGET``
non-shrinking replacements lying wholly inside inserted text since its last replacement that consumed source text, then
restores the line (with its findings and log lines) to that replacement, stops every rule that replaced inside inserted text
since then on that line, records each as ``redaction_guard`` ``{"rule", "line"}``, and carries on with the remaining rules.

This script runs that rule with CPython's ``re`` over every enumerated file of ``<root>`` and prints the Python dump with each
timed-out record replaced by the model's record (the port record must then equal it exactly). A timed-out file on which the
model fires no guard, and a file Python returned on whose model record differs from Python's own record (the model must be
``copy_with_secret_filter`` wherever the guard does not fire), are violations: exit status 1, reported on stderr.
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import py_dump  # noqa: E402  (the dump's context, walk and serialisation)
from driftbuster import secret_scanning  # noqa: E402

PLACEHOLDER = "[SECRET]"
# SecretScanner.GuardBudget in the port.
GUARD_BUDGET = 1024


def _redact_line(line: str, lineno: int, display: str, context, guards: list[dict[str, object]]):
    """One line of ``copy_with_secret_filter`` with the fix g guard: (working line, findings, log lines)."""

    working = line
    inserted = [False] * len(line)
    checkpoint = (working, list(inserted), 0)
    findings: list[secret_scanning.SecretFinding] = []
    logs: list[str] = []
    budget = 0
    looping: list[secret_scanning.SecretDetectionRule] = []
    stopped: list[secret_scanning.SecretDetectionRule] = []
    while True:
        triggered = None
        match_obj = None
        for rule in context.rules:
            if rule.name in context.ignore_rules or any(rule is known for known in stopped):
                continue
            match = rule.pattern.search(working)
            if not match:
                continue
            if any(pattern.search(line) for pattern in context.ignore_patterns):
                continue
            triggered = rule
            match_obj = match
            break
        if triggered is None or match_obj is None:
            break
        start, end = match_obj.span()
        inside_inserted = start != end and all(inserted[start:end])
        if inside_inserted:
            if end - start <= len(PLACEHOLDER):
                budget += 1
                if budget > GUARD_BUDGET:
                    working, inserted, kept = checkpoint[0], list(checkpoint[1]), checkpoint[2]
                    del findings[kept:]
                    del logs[kept:]
                    for rule in looping:
                        stopped.append(rule)
                        guards.append({"rule": rule.name, "line": lineno})
                    looping = []
                    budget = 0
                    continue
            if not any(triggered is known for known in looping):
                looping.append(triggered)
        consumed_source = not all(inserted[start:end])
        redacted_line = working[:start] + PLACEHOLDER + working[end:]
        preview_line = redacted_line.rstrip("\n\r")
        masked_preview = preview_line[:120]
        if len(preview_line) > 120:
            masked_preview = preview_line[:117] + "..."
        findings.append(secret_scanning.SecretFinding(path=display, rule=triggered.name, line=lineno, snippet=preview_line[:200]))
        logs.append(f"secret candidate redacted ({triggered.name}) from {display}:{lineno} -> {masked_preview}")
        working = redacted_line
        inserted[start:end] = [True] * len(PLACEHOLDER)
        if consumed_source:
            checkpoint = (working, list(inserted), len(logs))
            budget = 0
            looping = []
        if start == end:
            break
    return working, findings, logs


def model_record(relative: str, path: Path, scanner: dict[str, object]) -> dict[str, object]:
    """``py_dump._secret_copy`` with ``copy_with_secret_filter`` replaced by the fix g model."""

    context = secret_scanning.build_context(py_dump.SECRET_OPTIONS, scanner)
    findings: list[secret_scanning.SecretFinding] = []
    log: list[str] = []
    guards: list[dict[str, object]] = []
    if not context.rules_loaded or not context.rules or secret_scanning.looks_binary(path):
        output = path.read_bytes()
    else:
        sanitized: list[str] = []
        matched = False
        matches = 0
        with path.open("r", encoding="utf-8", errors="replace") as handle:
            for lineno, line in enumerate(handle, 1):
                working, line_findings, line_logs = _redact_line(line, lineno, relative, context, guards)
                findings.extend(line_findings)
                log.extend(line_logs)
                matches += len(line_logs)
                matched = matched or bool(line_logs)
                sanitized.append(working if line_logs else line)
        if matched:
            output = "".join(sanitized).encode("utf-8")
            log.append(f"scrubbed {matches} potential secret line(s) from {relative}")
        else:
            output = path.read_bytes()
    record: dict[str, object] = {
        "path": relative,
        "size": len(output),
        "sha256": hashlib.sha256(output).hexdigest(),
        "findings": [{"rule": finding.rule, "line": finding.line, "snippet": finding.snippet} for finding in findings],
        "log": log,
        "output_text": output.decode("utf-8", "replace"),
    }
    if guards:
        record["redaction_guard"] = guards
    return record


def main(python_path: str, root_text: str, ruleset_path: str | None) -> int:
    root = Path(root_text)
    scanner = dict(py_dump.SECRET_SCANNER)
    if ruleset_path is not None:
        scanner["ruleset"] = json.loads(Path(ruleset_path).read_text(encoding="utf-8"))
    with open(python_path, encoding="utf-8") as handle:
        records = [json.loads(line) for line in handle if line.strip()]
    walked = {relative: path for relative, path, errored in py_dump._walk(root) if not errored}
    failures = 0
    guarded = 0
    for record in records:
        path = walked.get(str(record.get("path")))
        if record.get("error") not in (None, "Timeout") or path is None:
            py_dump._emit(record)
            continue
        model = model_record(str(record["path"]), path, scanner)
        if record.get("error") == "Timeout":
            if "redaction_guard" not in model:
                failures += 1
                print(f"fix g: {record['path']}: python did not return but the model fired no guard", file=sys.stderr)
            else:
                guarded += 1
            py_dump._emit(model)
            continue
        if json.loads(json.dumps(py_dump._escape_lone_surrogates(model))) != record:
            failures += 1
            print(f"fix g: {record['path']}: the model differs from python where python returned", file=sys.stderr)
        py_dump._emit(record)
    print(f"fix g: {guarded} timed-out files replaced by the model, {failures} violations", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    arguments = sys.argv[1:]
    ruleset = None
    if len(arguments) == 4 and arguments[2] == "--ruleset":
        ruleset = arguments[3]
        arguments = arguments[:2]
    if len(arguments) != 2:
        raise SystemExit(__doc__)
    raise SystemExit(main(*arguments, ruleset))
