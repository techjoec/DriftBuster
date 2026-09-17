"""Python side of the parity harness: canonical JSON dumps of engine surfaces.

Temporary; deleted together with the Python package once the C# port is proven.

Usage:
    py_dump.py detect <path> [--plugins name,name] [--sample-size N] [--max-total-sample-bytes N]
    py_dump.py decode <path>
    py_dump.py diff <before> <after> [--content-type T] [--context N] [--mask TOKEN]... [--placeholder P]
                    [--label-from L --label-to L]
    py_dump.py diff --pairs <tsv> [same options]
    py_dump.py canon <path> --content-type T
    py_dump.py hunt <path> [--glob G] [--exclude P]...
    py_dump.py secrets <path> [--ruleset R]
    py_dump.py secrets-context <config.json | directory>
    py_dump.py multi-server <plans.json> [--sample-budget N] [--sample-size N] [--runs N]
    py_dump.py profile-store <payload.json> [--tags t1,t2] [--path rel]
    py_dump.py profile-diff <baseline.json> <current.json>
    py_dump.py run-profile <profile.json> <workdir> [--scratch DIR]
    py_dump.py schedule <config.json> <state.json> list|due|mark-complete|skip-until [--at T] [--name N] [--completed-at T]
                        [--resume-at T] [--now T]
    py_dump.py sql-export <case.json> [--scratch DIR]
    py_dump.py report <case.json>
    py_dump.py registry-scan <case.json>
    py_dump.py capture <case dir>/case.json [--scratch DIR]

Both surfaces enumerate the path with the Python ``Detector.scan_path`` itself (a tolerant
subclass that records I/O errors instead of raising), so walk order, symlink handling and
unreadable entries are the engine's own. A path the walk could not read is emitted as
``{"path", "error": "DetectorIOError"}``; a root that could not be scanned is emitted with
path ".". A match the strict Python catalog rejects (fix c) is emitted with both the
``MetadataValidationError`` text and the plugin's own match fields, and a file that makes the
interpreter itself give up (``RecursionError`` in ``json.loads``, the ``int()`` digit limit) is
emitted as ``{"path", "error": "InterpreterLimit: ..."}``. ``detect`` then scans every enumerated file
with one ``Detector`` (restricted to the named plugins when given, otherwise the full default registry) so the
aggregate sampling budget applies across files exactly as ``scan_path`` applies it, and prints one JSON
object per file. ``decode`` prints ``looks_text``,
the codec chosen by ``decode_text`` and the SHA-256 of the decoded text (UTF-8).

The phase 4 surfaces read files as ``bytes.decode("utf-8", "replace")`` (no newline translation, a BOM kept as U+FEFF) so
the canonicalisers see every byte. ``diff`` prints one record per pair: ``build_unified_diff`` of the two files (labels
default to the file names; the content type defaults to the port's ``ContentTypeResolver`` rule, ``xml`` when either file's
``catalog_format`` is ``structured-config-xml`` or ``xml``, otherwise ``text``) and ``diff_summary_to_payload`` of
``summarise_diff_result(result, versions=(from, to))`` with ``generated_at`` replaced by ``<generated_at>``, plus
``key_order``: the insertion order of every mapping in ``result`` and ``summary`` (``redaction_counts`` in first-hit order,
``safety_limits`` and the payload in the engine's order), which the sorted serialisation would otherwise hide. With
``PARITY_PORT_CANONICAL`` set (fix d), a listed pair is diffed from the port's canonical texts instead. ``--pairs``
reads tab-separated ``before<TAB>after`` lines so one process covers many pairs. ``canon`` prints the canonical text of
every enumerated file for one content type. ``hunt`` prints ``hunt_path(..., rules=default_rules(), return_json=True)``
entries with the root prefix of ``path`` replaced by ``<root>`` (with fix e applied to the install-path rule when
``PARITY_HUNT_FIX_E=1``). ``secrets`` runs ``copy_with_secret_filter`` for every
enumerated file into a temporary directory with ``SECRET_OPTIONS`` / ``SECRET_SCANNER`` as the context (``--ruleset`` adds a
``ruleset`` mapping to the scanner) and prints the size, digest, findings, log lines and the written text; with
``PARITY_SECRETS_TIMEOUT`` set, a file whose copy has not returned after that many seconds is ``{"path", "error": "Timeout"}``.
``secrets-context`` prints ``build_context`` and ``manifest_secret_scanner`` for ``{"options": ..., "secret_scanner": ...}`` files.

``multi-server`` runs ``MultiServerRunner(cache, sample_budget=..., sample_size=...).run(plans)`` in process over the
``plans`` of a request file, with a fresh temporary cache (``--runs N`` runs N times against that one cache and dumps the last
run), and prints one document: ``progress`` (the ``{host_id, status, message}`` of every line ``emit_progress`` wrote during
the dumped run, captured by replacing its writer), ``response`` and ``cache`` (``{file, signature, sha256}`` per cache file
in name order), plus ``key_order`` over the three. Every ``timestamp``, ``last_updated``, ``last_seen`` and ``generated_at``
value is replaced by ``<timestamp>`` (``<invalid timestamp: ...>`` when it is not spelled as ``datetime.now(UTC).isoformat()``
spells it), and every absolute root in ``results[].roots`` is respelled relative to the working directory, there and inside
every result ``message``. With ``PARITY_MULTI_SERVER_FIXES=1`` the runner carries plan fixes a and b exactly as the port does,
with ``b`` fix b alone (see ``_MultiServerFixes``); otherwise the runner is stock. ``PARITY_MULTI_SERVER_RECORDS`` names a
file that receives every config id the runner assigned (host, root position, relative path, id), with fix b the files skipped
(and why) and the roots refused, without it the path each failing host's scan raised on, the payloads fix d substituted and,
when ``_build_plans`` or ``run()`` raises, that exception with the run it ended (the dump then exits with it).
With ``PARITY_PORT_CLI`` set, a namespaced xml payload is canonicalised by the port (fix d, ``_PortXmlCanonical``).

``profile-store`` builds the store with ``profile_cli._store_from_payload(_load_json(payload))`` and prints ``summary``,
``applicable_profiles`` (names) and ``matching_configs`` for ``--tags`` (split on commas) and ``--path``, ``find_config`` for every
identifier plus one that is not registered, ``to_dict`` and ``round_trip`` (the ``to_dict`` of a store rebuilt from it); a build that
raises is the record's ``error`` (``{"type", "message"}``), a stage that raises that stage's. ``profile-diff`` prints
``diff_summary_snapshots`` of two ``_load_json`` payloads as ``diff``, or the ``error``. ``run-profile`` copies ``workdir`` to
``<scratch>/work`` (the harness gives both dumps one scratch path, so absolute paths agree), makes it the working directory and runs
``execute_profile`` with a fixed timestamp: ``result`` (``to_dict``), ``collected`` (source, alias directory, relative path, size,
SHA-256 per file), ``error``, ``redaction_guard`` and ``profiles`` (every file under ``Profiles/`` with size, SHA-256 and the text of
``profile.json`` and ``metadata.json``), the working directory respelled ``<workdir>``. With ``PARITY_SECRETS_TIMEOUT`` set, a copy that
has not returned in time (fix g) is redone with ``secrets_guard.py``'s reference model and reported on stderr as ``fix g: <path>``. With
``PARITY_RUN_PROFILE_SURROGATE_NAMES=1`` a variable or user name holding an unpaired surrogate is looked up as not found instead of
raising (decision R, ``_SurrogateNameLookups``). A profile with a structured source (a mapping that sets an alias, optional or exclude patterns, names a registry scan or SQL snapshot,
or that ``OfflineCollectionSource.from_dict`` refuses) has no ``execute_profile`` counterpart: the dump prints ``"mode": "offline-runner"``
with the ``collected`` set, secret ``findings``, ``redaction_guard`` and expected ``profile_json`` of ``offline_runner.execute_config``
instead (structured sources, plan decision); a mapping holding only a path is run as that path string. ``schedule`` runs one
``run_profiles_cli`` schedule command over the manifest and a scratch copy of the state file with ``ProfileScheduler._now`` fixed at
``--now`` and prints ``output`` (the payload ``_print_json`` receives) or ``error``, then ``state`` (the state file's text); with
``PARITY_SCHEDULE_JSON_CALL_BUDGET=1`` a decode that ran out of call budget at the nesting limit is redone as ``_JsonCallBudget``
describes. Each of these records carries ``key_order``.

The phase 7 surfaces print one record each, with ``key_order``. ``sql-export`` builds the case's database from a SQL script beside the
case in a fresh scratch working directory and runs ``write_sqlite_snapshot`` with the case's options and a fixed clock (``cmd_sql_export``).
``report`` renders the HTML report, JSON lines (both key orders), the detection summary and the snapshot manifest over the case's matches,
diffs, hunt hits, profile summary and redaction settings (``cmd_report``). ``registry-scan`` drives the instrumented registry operations,
the offline runner's registry schema readers and ``registry_cli.main`` over a fake ``_Backend`` the case describes (``cmd_registry_scan``).
``capture`` runs ``scripts/capture.py`` ``run``, ``export-sql`` and ``compare`` steps over a copy of the case's ``workdir/`` with the
clocks, host name and environment pinned and prints each step's result and every file the steps wrote (``cmd_capture``).

Every string in a record has each unpaired surrogate (a lone ``\\ud83d`` escape in a JSON key, say) rewritten to the
six characters ``\\uXXXX`` before serialisation, exactly as the port's ``CanonicalJson`` does: a UTF-8 stdout cannot
encode the code point and jq rejects the JSON escape of a lone surrogate. A record the dump itself cannot produce
is emitted as ``{"path", "error": "DumpError: ..."}`` so one file never aborts the run.
"""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import re
import shutil
import signal
import sys
import tempfile
from pathlib import Path

from driftbuster import hunt as hunt_module
from driftbuster import secret_scanning
from driftbuster.core import detector as detector_module
from driftbuster.core.detector import Detector, DetectorIOError
from driftbuster.core.types import MetadataValidationError, _json_safe, validate_detection_metadata
from driftbuster.formats import format_registry as registry
from driftbuster.reporting import diff as diff_module

ROOT_ERROR_PATH = "."


class _CatalogRejection:
    """Fix c seam: keeps the plugin's match when the strict catalog rejects it.

    ``Detector.scan_file`` validates after enriching the match and before normalising reasons; the wrapper records
    the rejection and hands the un-enriched metadata back so the rest of ``scan_file`` runs and the raw plugin output
    (what the port compares against, minus its ``catalog_*`` keys) is emitted next to the error.
    """

    message: str | None = None

    def __call__(self, match, catalog):
        try:
            return validate_detection_metadata(match, catalog)
        except MetadataValidationError as exc:
            self.message = str(exc)
            metadata = match.metadata if match.metadata is not None else {}
            return {str(key): _json_safe(value) for key, value in metadata.items()}

    def take(self) -> str | None:
        message, self.message = self.message, None
        return message


_catalog_rejection = _CatalogRejection()
detector_module.validate_detection_metadata = _catalog_rejection


class _TolerantDetector(Detector):
    """Records DetectorIOError instead of raising so the walk continues past unreadable entries."""

    def __init__(self) -> None:
        super().__init__(plugins=(), sort_plugins=False, max_total_sample_bytes=1 << 62)
        self.errors: list[Path] = []

    def _handle_error(self, path: Path, error: DetectorIOError, *, cause: BaseException | None = None) -> None:
        self.errors.append(Path(path))


_LONE_SURROGATE = re.compile("[\ud800-\udfff]")


def _escape_lone_surrogates(value: object) -> object:
    """Rewrites every unpaired surrogate in every str (keys included) to the literal text ``\\uXXXX``; a tuple becomes a list.
    Built on an explicit stack, as ``_key_order`` is, so a record nested past the interpreter's frame limit is emitted too."""

    def scalar(item: object) -> object:
        return _LONE_SURROGATE.sub(lambda match: f"\\u{ord(match.group()):04x}", item) if isinstance(item, str) else item

    def container(item: object) -> dict | list | None:
        if isinstance(item, dict):
            return {}
        if isinstance(item, (list, tuple)):
            return []
        return None

    root = container(value)
    if root is None:
        return scalar(value)
    # (source container, its rewritten container, the next child index)
    stack: list[tuple[object, dict | list, int]] = [(value, root, 0)]
    while stack:
        source, out, index = stack[-1]
        items = list(source.items()) if isinstance(source, dict) else source
        if index >= len(items):
            stack.pop()
            continue
        stack[-1] = (source, out, index + 1)
        child = items[index][1] if isinstance(source, dict) else items[index]
        rewritten = container(child)
        if rewritten is not None:
            stack.append((child, rewritten, 0))
        else:
            rewritten = scalar(child)
        if isinstance(out, dict):
            out[scalar(items[index][0])] = rewritten
        else:
            out.append(rewritten)
    return root


def _emit(record: dict[str, object]) -> None:
    try:
        line = json.dumps(_escape_lone_surrogates(record), sort_keys=True, ensure_ascii=False)
        line.encode("utf-8")
    except (TypeError, ValueError, UnicodeEncodeError) as exc:
        line = json.dumps({"path": record.get("path"), "error": f"DumpError: {type(exc).__name__}: {exc}"}, sort_keys=True)
    sys.stdout.write(line)
    sys.stdout.write("\n")


def _relative(root: Path, path: Path) -> str:
    if path == root:
        return root.name
    return path.relative_to(root).as_posix()


def _walk(root: Path) -> list[tuple[str, Path, bool]]:
    """(relative posix path, path, errored) in Detector.scan_path order, entries reported without a result included; a root
    failure yields one "." entry."""

    enumerator = _TolerantDetector()
    results = enumerator.scan_path(root)
    errored = set(enumerator.errors)
    if root in errored and not results:
        return [(ROOT_ERROR_PATH, root, True)]
    # An entry the walk reported without scanning (is_file() raised) is listed too, in walk order.
    paths = {path for path, _ in results} | {path for path in errored if path != root}
    return [(_relative(root, path), path, path in errored) for path in sorted(paths)]


def _select_plugins(names: str | None):
    if names is None:
        return None
    wanted = {name.strip() for name in names.split(",") if name.strip()}
    return [plugin for plugin in registry.get_plugins() if plugin.name in wanted]


def cmd_detect(args: argparse.Namespace) -> int:
    root = Path(args.path)
    detector = Detector(
        plugins=_select_plugins(args.plugins),
        sample_size=args.sample_size,
        max_total_sample_bytes=args.max_total_sample_bytes,
    )
    for relative, path, errored in _walk(root):
        record: dict[str, object] = {"path": relative}
        if errored:
            record["error"] = "DetectorIOError"
            _emit(record)
            continue
        try:
            match = detector.scan_file(path)
        except DetectorIOError:
            record["error"] = "DetectorIOError"
            _emit(record)
            continue
        except RecursionError as exc:
            # json.loads recursing past the interpreter limit; the port's scanner has no depth limit.
            record["error"] = f"InterpreterLimit: RecursionError: {exc}"
            _emit(record)
            continue
        except ValueError as exc:
            # int() refusing a literal longer than sys.get_int_max_str_digits(); the port never converts integers.
            if "Exceeds the limit" not in str(exc):
                raise
            record["error"] = f"InterpreterLimit: ValueError: {exc}"
            _emit(record)
            continue
        rejection = _catalog_rejection.take()
        if rejection is not None:
            record["error"] = f"MetadataValidationError: {rejection}"
        if match is None:
            record.update(
                {"plugin": None, "format": None, "variant": None, "confidence": None, "reasons": [], "metadata": None}
            )
        else:
            record.update(
                {
                    "plugin": match.plugin_name,
                    "format": match.format_name,
                    "variant": match.variant,
                    "confidence": round(match.confidence, 6),
                    "reasons": list(match.reasons),
                    "metadata": _json_safe(match.metadata) if match.metadata is not None else None,
                }
            )
        _emit(record)
        if detector.sample_budget_exhausted:
            break
    return 0


def cmd_decode(args: argparse.Namespace) -> int:
    root = Path(args.path)
    for relative, path, errored in _walk(root):
        if errored:
            _emit({"path": relative, "error": "DetectorIOError"})
            continue
        sample = path.read_bytes()
        text, codec = registry.decode_text(sample)
        _emit(
            {
                "path": relative,
                "looks_text": registry.looks_text(sample),
                "codec": codec,
                "text_sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(),
            }
        )
    return 0


GENERATED_AT_TOKEN = "<generated_at>"
ROOT_TOKEN = "<root>"
# The fixed rule context of the secrets surface: the packaged ruleset, one ignore pattern and no ignored rule.
SECRET_OPTIONS: dict[str, object] = {"secret_ignore_patterns": "PARITY-ALLOW"}
SECRET_SCANNER: dict[str, object] = {}


def _read_replace(path: Path) -> str:
    return path.read_bytes().decode("utf-8", "replace")


def _resolve_file(path: Path, detector: Detector) -> str:
    """ContentTypeResolver.ResolveFile: the multi-server rule over the default registry; unreadable is text."""

    try:
        match = detector.scan_file(path)
    except (OSError, MetadataValidationError):
        return "text"
    finally:
        _catalog_rejection.take()
    metadata = match.metadata if match is not None and match.metadata is not None else {}
    return "xml" if metadata.get("catalog_format") in {"structured-config-xml", "xml"} else "text"


def _resolve_pair(before: Path, after: Path) -> str:
    detector = Detector()
    return "xml" if "xml" in (_resolve_file(before, detector), _resolve_file(after, detector)) else "text"


def _port_canonical_texts() -> dict[str, tuple[str, str]]:
    """Fix d seam: ``PARITY_PORT_CANONICAL`` names JSON lines ``{"pair", "canonical_before", "canonical_after"}`` holding the
    port's canonical texts (before the safety clamps) for those pairs."""

    source = os.environ.get("PARITY_PORT_CANONICAL")
    if not source:
        return {}
    texts: dict[str, tuple[str, str]] = {}
    for line in Path(source).read_text(encoding="utf-8").splitlines():
        if line.strip():
            entry = json.loads(line)
            texts[entry["pair"]] = (entry["canonical_before"], entry["canonical_after"])
    return texts


def _diff_record(
    before: Path, after: Path, args: argparse.Namespace, canonical: tuple[str, str] | None = None
) -> dict[str, object]:
    """``build_unified_diff`` of the pair; with ``canonical`` (fix d), the content type's normaliser returns those texts in
    call order (before, then after) instead of canonicalising, so every step after canonicalisation is CPython's own."""

    content_type = args.content_type or _resolve_pair(before, after)
    normalisers = diff_module._NORMALISERS
    original = normalisers.get(content_type)
    if canonical is not None and original is not None:
        texts = iter(canonical)
        normalisers[content_type] = lambda _payload: next(texts)  # type: ignore[index]
    try:
        result = diff_module.build_unified_diff(
            _read_replace(before),
            _read_replace(after),
            content_type=content_type,
            from_label=args.label_from if args.label_from is not None else before.name,
            to_label=args.label_to if args.label_to is not None else after.name,
            mask_tokens=args.mask or None,
            placeholder=args.placeholder,
            context_lines=args.context,
        )
    finally:
        if original is not None:
            normalisers[content_type] = original  # type: ignore[index]
    payload = dict(
        diff_module.diff_summary_to_payload(
            diff_module.summarise_diff_result(result, versions=(result.from_label, result.to_label))
        )
    )
    payload["generated_at"] = GENERATED_AT_TOKEN
    record: dict[str, object] = {
        "result": {
            "canonical_before": result.canonical_before,
            "canonical_after": result.canonical_after,
            "diff": result.diff,
            "stats": dict(result.stats),
            "content_type": result.content_type,
            "from_label": result.from_label,
            "to_label": result.to_label,
            "label": result.label,
            "mask_tokens": list(result.mask_tokens) if result.mask_tokens is not None else None,
            "placeholder": result.placeholder,
            "context_lines": result.context_lines,
            "redaction_counts": dict(result.redaction_counts) if result.redaction_counts is not None else None,
            "safety_limits": result.safety_limits,
        },
        "summary": payload,
    }
    record["key_order"] = _key_order(record)
    return record


def _key_order(value: object) -> object:
    """Every mapping's keys in insertion order, which ``sort_keys`` would hide: ``[[key, child], ...]`` for a mapping, a list
    of children for a sequence, None for a scalar. Built on an explicit stack, so a record nested past the interpreter's frame
    limit (a report whose metadata Python still renders) is described too."""

    if not isinstance(value, (dict, list, tuple)):
        return None
    root: list = []
    # (container, its description list, the next child index)
    stack: list[tuple[object, list, int]] = [(value, root, 0)]
    while stack:
        container, out, index = stack[-1]
        items = list(container.items()) if isinstance(container, dict) else container
        if index >= len(items):
            stack.pop()
            continue
        stack[-1] = (container, out, index + 1)
        child = items[index][1] if isinstance(container, dict) else items[index]
        if isinstance(child, (dict, list, tuple)):
            described: list = []
            stack.append((child, described, 0))
        else:
            described = None
        out.append([str(items[index][0]), described] if isinstance(container, dict) else described)
    return root


def cmd_diff(args: argparse.Namespace) -> int:
    if args.pairs is not None:
        pairs = [line.split("\t") for line in Path(args.pairs).read_text(encoding="utf-8").splitlines() if line]
    elif args.before is not None and args.after is not None:
        pairs = [[args.before, args.after]]
    else:
        raise SystemExit("diff needs <before> <after> or --pairs")
    port_canonical = _port_canonical_texts()
    for before, after in pairs:
        record: dict[str, object] = {"pair": f"{before}\t{after}"}
        try:
            record.update(_diff_record(Path(before), Path(after), args, port_canonical.get(f"{before}\t{after}")))
        except UnicodeEncodeError as exc:
            # The clamps and digests encode strictly: an unpaired surrogate in a canonical text or the diff is an interpreter
            # error the port does not reproduce (expected_divergences.md, "Diff surface").
            record["error"] = "InterpreterError: UnicodeEncodeError" if "surrogates not allowed" in str(exc) else "EngineError"
        except Exception:  # one pair never aborts the run
            record["error"] = "EngineError"
        _emit(record)
    return 0


def cmd_canon(args: argparse.Namespace) -> int:
    root = Path(args.path)
    for relative, path, errored in _walk(root):
        record: dict[str, object] = {"path": relative, "content_type": args.content_type}
        if errored:
            record["error"] = "DetectorIOError"
        else:
            try:
                normaliser = {
                    "text": diff_module.canonicalise_text,
                    "json": diff_module.canonicalise_json,
                    "xml": diff_module.canonicalise_xml,
                }[args.content_type]
                record["canonical"] = normaliser(_read_replace(path))
            except Exception:  # one file never aborts the run
                record["error"] = "EngineError"
        _emit(record)
    return 0


# Fix e: the install-path drive pattern as the port ships it (single-escaped). With PARITY_HUNT_FIX_E=1 the hunt surface runs
# default_rules() with this pattern swapped in, so the harness can compare every hunt record exactly.
FIXED_INSTALL_PATTERN = r"[A-Za-z]:\\[\w\-\.\s]+"


def _hunt_rules(fix_e: bool | None = None) -> tuple[hunt_module.HuntRule, ...]:
    """``default_rules()``, with fix e applied when ``fix_e`` is true (``None``: when ``PARITY_HUNT_FIX_E=1``)."""

    rules = hunt_module.default_rules()
    if not (os.environ.get("PARITY_HUNT_FIX_E") == "1" if fix_e is None else fix_e):
        return rules
    fixed = []
    for rule in rules:
        if rule.name == "install-path":
            rule = hunt_module.HuntRule(
                name=rule.name,
                description=rule.description,
                token_name=rule.token_name,
                keywords=rule.keywords,
                patterns=(FIXED_INSTALL_PATTERN, *(pattern.pattern for pattern in rule.compiled_patterns[1:])),
            )
        fixed.append(rule)
    return tuple(fixed)


def cmd_hunt(args: argparse.Namespace) -> int:
    root = Path(args.path)
    prefix = str(root)
    entries = hunt_module.hunt_path(
        root,
        rules=_hunt_rules(),
        glob=args.glob,
        exclude_patterns=args.exclude or None,
        return_json=True,
    )
    for entry in entries:
        path = str(entry["path"])
        if path == prefix:
            entry["path"] = ROOT_TOKEN
        elif path.startswith(prefix + "/"):
            entry["path"] = ROOT_TOKEN + path[len(prefix):]
        _emit(entry)
    return 0


class _SecretTimeout(Exception):
    """Raised by the SIGALRM handler when one file's copy runs past PARITY_SECRETS_TIMEOUT seconds."""


def _raise_timeout(_signum, _frame):
    raise _SecretTimeout


def _secret_record(relative: str, path: Path, scanner: dict[str, object]) -> dict[str, object]:
    """One file's copy; with PARITY_SECRETS_TIMEOUT set, a copy that has not returned by then (fix g: the redaction loop that
    never ends) is emitted as ``{"path", "error": "Timeout"}``."""

    timeout = float(os.environ.get("PARITY_SECRETS_TIMEOUT") or 0)
    if timeout <= 0:
        return _secret_copy(relative, path, scanner)
    previous = signal.signal(signal.SIGALRM, _raise_timeout)
    signal.setitimer(signal.ITIMER_REAL, timeout)
    try:
        return _secret_copy(relative, path, scanner)
    except _SecretTimeout:
        return {"path": relative, "error": "Timeout"}
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)


def _secret_copy(relative: str, path: Path, scanner: dict[str, object]) -> dict[str, object]:
    context = secret_scanning.build_context(SECRET_OPTIONS, scanner)
    log: list[str] = []
    with tempfile.TemporaryDirectory(prefix="driftbuster-parity-secrets-") as tmp:
        destination = Path(tmp) / "out" / path.name
        size, digest = secret_scanning.copy_with_secret_filter(
            path, destination, display_path=relative, context=context, log=log.append
        )
        output_text = destination.read_bytes().decode("utf-8", "replace")
    return {
        "path": relative,
        "size": size,
        "sha256": digest,
        "findings": [{"rule": finding.rule, "line": finding.line, "snippet": finding.snippet} for finding in context.findings],
        "log": log,
        "output_text": output_text,
    }


def cmd_secrets(args: argparse.Namespace) -> int:
    root = Path(args.path)
    scanner = dict(SECRET_SCANNER)
    if args.ruleset is not None:
        scanner["ruleset"] = json.loads(Path(args.ruleset).read_text(encoding="utf-8"))
    for relative, path, errored in _walk(root):
        if errored:
            _emit({"path": relative, "error": "DetectorIOError"})
            continue
        _emit(_secret_record(relative, path, scanner))
    return 0


def _context_payload(context: secret_scanning.SecretDetectionContext) -> dict[str, object]:
    return {
        "rules": [
            {"name": rule.name, "pattern": rule.pattern.pattern, "flags": int(rule.pattern.flags), "description": rule.description}
            for rule in context.rules
        ],
        "version": context.version,
        "ignore_rules": sorted(context.ignore_rules),
        "ignore_patterns": [pattern.pattern for pattern in context.ignore_patterns],
        "ignore_pattern_text": list(context.ignore_pattern_text),
        "rules_loaded": context.rules_loaded,
    }


def cmd_secrets_context(args: argparse.Namespace) -> int:
    root = Path(args.path)
    files = sorted(path for path in root.glob("*.json") if path.is_file()) if root.is_dir() else [root]
    for path in files:
        config = json.loads(path.read_text(encoding="utf-8"))
        options = config.get("options")
        scanner = config.get("secret_scanner")
        record: dict[str, object] = {"path": path.name}
        secret_scanning.reset_secret_rule_cache()
        context = secret_scanning.build_context(options, scanner)
        record["context"] = _context_payload(context)
        if isinstance(options, dict) and isinstance(scanner, dict):
            record["manifest"] = dict(secret_scanning.manifest_secret_scanner(options, scanner, context))
        _emit(record)
    return 0


TIMESTAMP_TOKEN = "<timestamp>"
TIMESTAMP_KEYS = frozenset({"timestamp", "last_updated", "last_seen", "generated_at"})
# datetime.now(UTC).isoformat(): exactly six fraction digits or none, and "+00:00".
_ISO_UTC = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{6})?\+00:00")


def _replace_timestamps(value: object) -> object:
    if isinstance(value, dict):
        replaced: dict[str, object] = {}
        for key, item in value.items():
            if key in TIMESTAMP_KEYS and isinstance(item, str):
                replaced[key] = TIMESTAMP_TOKEN if _ISO_UTC.fullmatch(item) else f"<invalid timestamp: {item}>"
            else:
                replaced[key] = _replace_timestamps(item)
        return replaced
    if isinstance(value, list):
        return [_replace_timestamps(item) for item in value]
    return value


def _respell_roots(response: dict[str, object]) -> None:
    """Absolute roots in ``results[].roots`` (and inside each result message) become relative to the working directory."""

    results = response.get("results")
    if not isinstance(results, list):
        return
    cwd = os.getcwd()
    absolute = {root for result in results for root in result.get("roots", []) if os.path.isabs(root)}
    spelled = {root: os.path.relpath(root, cwd) for root in absolute}
    # One pass, longest root first at each position, so a respelled root is never rewritten again by a shorter one.
    pattern = re.compile("|".join(re.escape(root) for root in sorted(spelled, key=len, reverse=True))) if spelled else None
    for result in results:
        result["roots"] = [spelled.get(root, root) for root in result.get("roots", [])]
        if pattern is not None and isinstance(result.get("message"), str):
            result["message"] = pattern.sub(lambda match: spelled[match.group()], result["message"])


# The largest file the port reads whole into a record (MultiServerRunner.DefaultMaxTextBytes: a sixth of the longest .NET string).
_PORT_MAX_TEXT_BYTES = 0x3FFFFFDF // 6


def _undecodable(path: object) -> bool:
    """True for a path holding a name os.scandir kept through surrogateescape (bytes that are not UTF-8)."""

    try:
        str(path).encode("utf-8")
    except UnicodeEncodeError:
        return True
    return False


class _MultiServerFixes:
    """Instruments one ``MultiServerRunner`` and applies the port's plan fixes to it without editing the engine.

    ``fixes`` is ``""`` (stock), ``"b"`` or ``"ab"``. Every mode records the config id the runner assigned to each file (host,
    root position, relative path) and, in stock mode, the path each host's scan raised on.

    Fix a replaces ``_normalise_config_id``: the id is ``slug(format)/slug(variant)/slug(relative posix path)`` (the variant part
    only when ``catalog_variant`` is a non-blank string; ``slug(format)#sha1(fallback)[:12]`` when the path slugs to nothing),
    and a record whose id another record of the same host already holds gets ``@root{index}`` (zero-based position of its root
    in ``plan.roots``, missing roots counted), then ``.{n}`` from 2 while that is taken too. ``ConfigIdentity`` in the port.

    Fix b makes the host's secret hunt and detector skip a file that cannot be looked up or read, and a detected file whose whole
    text cannot be read (or is longer than the port reads whole), recording each; a root that cannot be listed or looked up, or a
    root file that cannot be read, raises ``DetectorIOError`` for the root (``permission_denied``), and a root whose ``exists()``
    raises counts as existing so it gets there instead of aborting ``run()``. ``SkippingDetector``,
    ``MultiServerRunner.BuildRecord`` and ``MultiServerRunner.CollectSecretHits`` in the port.

    With fix b the platform limit on undecodable names applies too: a file whose name is not UTF-8 is reported to the detector's
    error handler before it is sampled and skipped (cause ``undecodable-name``), as the port cannot name it.
    """

    def __init__(self, multi_server, runner, *, fixes: str) -> None:
        self._ms = multi_server
        self.fixes = fixes
        self.records: list[dict[str, object]] = []
        self.skipped: list[dict[str, str]] = []
        self.root_errors: list[dict[str, str]] = []
        self.stock_errors: list[dict[str, str]] = []
        self.scans: list[dict[str, object]] = []
        self.results: list[dict[str, object]] = []
        self.host: str | None = None
        self._root: Path | None = None
        self._positions: list[int] = []
        self._scan_index = -1
        self._root_index = -1
        self._taken: set[str] = set()
        self._install(runner)

    def _install(self, runner) -> None:
        detector = runner._detector
        scan_plan, scan_path, scan_file = runner._scan_plan, detector.scan_path, detector.scan_file
        normalise, handle_error, collect = runner._normalise_config_id, detector._handle_error, runner._collect_secret_hits
        result_payload = runner._result_payload
        fix_a, fix_b = "a" in self.fixes, "b" in self.fixes

        def result_payload_hook(plan, **kwargs):
            payload = result_payload(plan, **kwargs)
            self.results.append(payload)
            return payload

        def scan_plan_hook(plan, roots, secret_paths):
            self.host = plan.host_id
            self._positions = self._plan_positions(plan, roots)
            self._scan_index = -1
            self._taken = set()
            return scan_plan(plan, roots, secret_paths)

        def scan_path_hook(root, glob="**/*", *, reset_budget=True):
            self._scan_index += 1
            self._root_index = self._positions[self._scan_index] if self._scan_index < len(self._positions) else self._scan_index
            self._root = Path(root)
            try:
                results = scan_path(root, glob, reset_budget=reset_budget)
            finally:
                self._root = None
            matches = sum(1 for _, match in results if match is not None)
            self.scans.append({"host_id": str(self.host), "plan_index": self.plan_index, "root": str(root), "matches": matches})
            return [self._readable(path, match) for path, match in results] if fix_b else results

        def scan_file_hook(path):
            if _undecodable(path):
                self._cause = "undecodable-name"
                raise OSError(f"File name is not valid UTF-8; the entry cannot be opened: {path!r}")
            return scan_file(path)

        def normalise_hook(match, metadata, relative):
            config_id = self._fixed_config_id(match, metadata, relative) if fix_a else normalise(match, metadata, relative)
            self.records.append(
                {"host_id": self.host, "plan_index": self.plan_index, "root_index": self._root_index, "relative_path": relative.as_posix(),
                 "config_id": config_id}
            )
            return config_id

        def handle_error_hook(path, error, *, cause=None):
            reason, self._cause = self._cause, "unreadable"
            if self._root is None or Path(path) == self._root:
                self.root_errors.append({"host_id": str(self.host), "plan_index": self.plan_index, "path": str(path)})
                return handle_error(path, error, cause=cause)
            self.skipped.append({"host_id": str(self.host), "plan_index": self.plan_index, "path": str(path), "cause": reason})
            return None

        def stock_handle_error_hook(path, error, *, cause=None):
            self.stock_errors.append({"host_id": str(self.host), "plan_index": self.plan_index, "path": str(path)})
            return handle_error(path, error, cause=cause)

        def collect_hook(roots):
            for root in roots:
                try:
                    listable = root.is_dir()
                    if listable:
                        with os.scandir(root):
                            pass
                except OSError as exc:
                    self.root_errors.append({"host_id": str(self.host), "plan_index": self.plan_index, "path": str(root)})
                    raise DetectorIOError(root, str(exc)) from exc
            with self._tolerant_hunt():
                return collect(roots)

        def stock_collect_hook(roots):
            try:
                return collect(roots)
            except OSError as exc:
                self.stock_errors.append(
                    {"host_id": str(self.host), "plan_index": self.plan_index, "path": str(getattr(exc, "filename", None))}
                )
                raise

        self._cause = "unreadable"
        runner._scan_plan = scan_plan_hook
        detector.scan_path = scan_path_hook
        runner._normalise_config_id = normalise_hook
        runner._result_payload = result_payload_hook
        if fix_b:
            detector.scan_file = scan_file_hook
            detector._handle_error = handle_error_hook
            runner._collect_secret_hits = collect_hook
        else:
            detector._handle_error = stock_handle_error_hook
            runner._collect_secret_hits = stock_collect_hook

    @property
    def plan_index(self) -> int:
        """The position in the request of the plan being scanned: every plan ends with exactly one ``_result_payload`` call."""

        return len(self.results)

    @staticmethod
    def _plan_positions(plan, roots) -> list[int]:
        """The position in ``plan.roots`` of each scanned root: the next plan root spelled the same way."""

        positions: list[int] = []
        start = 0
        for index, root in enumerate(roots):
            found = next((candidate for candidate in range(start, len(plan.roots)) if str(plan.roots[candidate]) == str(root)), -1)
            positions.append(found if found >= 0 else index)
            start = found + 1 if found >= 0 else start
        return positions

    def _readable(self, path: Path, match):
        """Fix b for a detected file whose whole text the record cannot read: the entry stays, without its match."""

        if match is None or not path.is_file():
            return path, match
        try:
            if path.stat().st_size > _PORT_MAX_TEXT_BYTES:
                raise OSError(f"File is larger than the {_PORT_MAX_TEXT_BYTES} bytes the scan reads whole: '{path}'")
            with path.open("rb") as handle:
                handle.read()
        except OSError:
            self.skipped.append({"host_id": str(self.host), "plan_index": self.plan_index, "path": str(path), "cause": "unreadable"})
            return path, None
        return path, match

    def tolerate_root_lookups(self, plans) -> None:
        """Fix b: a root whose ``exists()`` raises counts as existing, so the host scan refuses it as ``permission_denied``."""

        base = type(Path())
        fixes = self

        class _TolerantRoot(base):  # type: ignore[misc, valid-type]
            def exists(self, *, follow_symlinks: bool = True) -> bool:
                try:
                    return super().exists(follow_symlinks=follow_symlinks)
                except OSError:
                    return True

        if "b" in fixes.fixes:
            for plan in plans:
                plan.roots = tuple(_TolerantRoot(root) for root in plan.roots)

    def begin_plan(self, host_id: str) -> None:
        self.host = host_id

    def _fixed_config_id(self, match, metadata, relative: Path) -> str:
        slugify = self._ms._slugify
        format_id = str(metadata.get("catalog_format") or match.format_name or "config")
        variant = metadata.get("catalog_variant")
        relative_text = relative.as_posix()
        slug = slugify(relative_text)
        if slug:
            parts = [slugify(format_id)]
            if isinstance(variant, str) and variant.strip():
                parts.append(slugify(variant))
            parts.append(slug)
            config_id = "/".join(part for part in parts if part)
        else:
            fallback = relative_text or match.plugin_name or "config"
            config_id = f"{slugify(format_id)}#{hashlib.sha1(fallback.encode('utf-8', 'ignore')).hexdigest()[:12]}"
        if config_id in self._taken:
            suffixed = f"{config_id}@root{self._root_index}"
            candidate, attempt = suffixed, 2
            while candidate in self._taken:
                candidate, attempt = f"{suffixed}.{attempt}", attempt + 1
            config_id = candidate
        self._taken.add(config_id)
        return config_id

    def _tolerant_hunt(self):
        fixes = self
        base = type(Path())

        class _TolerantPath(base):  # type: ignore[misc, valid-type]
            def is_file(self, *, follow_symlinks: bool = True) -> bool:
                try:
                    return super().is_file(follow_symlinks=follow_symlinks)
                except OSError:
                    fixes.skipped.append(
                        {"host_id": str(fixes.host), "plan_index": fixes.plan_index, "path": str(self), "cause": "unreadable"}
                    )
                    return False

        iter_text = hunt_module._iter_text

        def tolerant_iter_text(path, sample_size):
            try:
                return iter_text(path, sample_size)
            except OSError:
                fixes.skipped.append({"host_id": str(fixes.host), "plan_index": fixes.plan_index, "path": str(path), "cause": "unreadable"})
                return None

        class _Patch:
            def __enter__(self_inner):
                hunt_module.Path = _TolerantPath
                hunt_module._iter_text = tolerant_iter_text

            def __exit__(self_inner, *exc_info):
                hunt_module.Path = Path
                hunt_module._iter_text = iter_text

        return _Patch()


class _PortXmlCanonical:
    """Fix d seam for the multi-server surface: with ``PARITY_PORT_CLI`` naming the port's ``driftbuster`` executable, an xml
    text that mentions ``xmlns`` is canonicalised by the port (``parity-dump canon`` over that exact text, written to a scratch
    file) instead of ``canonicalise_xml``, both where ``_scan_plan`` canonicalises a payload and where ``build_unified_diff``
    canonicalises the two payloads again. The diff, digests, stats and cache entry after canonicalisation are then CPython's own
    over the port's canonical texts. Each substitution whose text differs from Python's is recorded (by the SHA-256 of the input
    text)."""

    def __init__(self, multi_server) -> None:
        self._ms = multi_server
        self._original = multi_server._canonicalise
        self._original_xml = diff_module._NORMALISERS["xml"]
        self.cli = os.environ.get("PARITY_PORT_CLI")
        self.substituted: list[str] = []

    def __enter__(self):
        if self.cli:
            self._ms._canonicalise = self._canonicalise
            diff_module._NORMALISERS["xml"] = self._canonicalise_xml  # type: ignore[index]
        return self

    def __exit__(self, *exc_info) -> None:
        self._ms._canonicalise = self._original
        diff_module._NORMALISERS["xml"] = self._original_xml  # type: ignore[index]

    def _canonicalise(self, content: str, content_type: str) -> str:
        if content_type != "xml":
            return self._original(content, content_type)
        return self._canonicalise_xml(content)

    def _canonicalise_xml(self, content: str) -> str:
        python_text = self._original_xml(content)
        if "xmlns" not in content:
            return python_text
        import subprocess

        with tempfile.TemporaryDirectory(prefix="driftbuster-parity-xml-") as scratch:
            source = Path(scratch) / "payload.xml"
            source.write_bytes(content.encode("utf-8", "surrogatepass"))
            completed = subprocess.run(
                [self.cli, "parity-dump", "canon", str(source), "--content-type", "xml"],
                check=True,
                capture_output=True,
            )
        port_text = json.loads(completed.stdout.decode("utf-8"))["canonical"]
        digest = hashlib.sha256(content.encode("utf-8", "surrogatepass")).hexdigest()
        if port_text != python_text and digest not in self.substituted:
            self.substituted.append(digest)
        return port_text


def cmd_multi_server(args: argparse.Namespace) -> int:
    from driftbuster import multi_server as ms

    request = json.loads(Path(args.plans).read_text(encoding="utf-8"))
    fixes = {"1": "ab", "b": "b"}.get(os.environ.get("PARITY_MULTI_SERVER_FIXES", ""), "")
    records_path = os.environ.get("PARITY_MULTI_SERVER_RECORDS")
    progress: list[dict[str, object]] = []
    instruments: list[_MultiServerFixes] = []
    writer = ms._emit_json_line

    def capture(payload):
        if payload.get("type") == "progress":
            entry = payload["payload"]
            progress.append({"host_id": entry["host_id"], "status": entry["status"], "message": entry["message"]})
            if entry["status"] == "running" and instruments:
                instruments[-1].begin_plan(entry["host_id"])

    _catalog_rejection.take()
    ms._emit_json_line = capture
    cache = Path(tempfile.mkdtemp(prefix="driftbuster-parity-multi-server-"))
    port_xml = _PortXmlCanonical(ms)
    try:
        response: object = None
        for run in range(1, args.runs + 1):
            progress.clear()
            instruments.clear()
            ms._reset_progress_throttle_state()
            runner = ms.MultiServerRunner(cache, sample_budget=args.sample_budget, sample_size=args.sample_size)
            instruments.append(_MultiServerFixes(ms, runner, fixes=fixes))
            port_xml.substituted.clear()
            stage = "build_plans"
            try:
                plans = ms._build_plans(request)
                instruments[-1].tolerate_root_lookups(plans)
                stage = "run"
                with port_xml:
                    response = runner.run(plans)
            except Exception as exc:
                # The dump ends at the run that raised: later runs never start.
                if records_path:
                    results = json.loads(json.dumps({"results": instruments[-1].results}))
                    _respell_roots(results)
                    _write_multi_server_records(
                        records_path, instruments[-1], port_xml,
                        {
                            "stage": stage,
                            "run": run,
                            "type": type(exc).__name__,
                            "message": str(exc),
                            "filename": None if getattr(exc, "filename", None) is None else str(exc.filename),  # pyright: ignore[reportAttributeAccessIssue]
                            "progress": list(progress),
                            "results": _replace_timestamps(results["results"]),
                            "cache": _multi_server_cache_listing(cache),
                        },
                    )
                raise
        rejection = _catalog_rejection.take()
        if rejection is not None:
            # Fix c changes catalog_* metadata and so config ids; multi-server cases must not hold such a format.
            print(f"multi-server case holds a format the Python catalog rejects (fix c): {rejection}", file=sys.stderr)
            return 3
        entries = _multi_server_cache_listing(cache)
    finally:
        ms._emit_json_line = writer
        shutil.rmtree(cache, ignore_errors=True)

    payload = json.loads(json.dumps(response))
    _respell_roots(payload)
    record: dict[str, object] = {"progress": list(progress), "response": _replace_timestamps(payload), "cache": entries}
    record["key_order"] = _key_order(record)
    _emit(record)
    if records_path:
        _write_multi_server_records(records_path, instruments[-1], port_xml, None)
    return 0


def _multi_server_cache_listing(cache: Path) -> list[dict[str, object]]:
    """Every cache entry's file name, stored signature and SHA-256. An entry that is not a JSON object (the empty file a
    ``DiffCache.save`` leaves when its encode raises after ``write_text`` truncated it) has signature None, as ``DiffCache.load``
    treats it as missing."""

    entries = []
    for entry in sorted(cache.iterdir()):
        raw = entry.read_bytes()
        try:
            stored = json.loads(raw.decode("utf-8"))
        except ValueError:
            stored = None
        entries.append(
            {
                "file": entry.name,
                "signature": stored.get("signature") if isinstance(stored, dict) else None,
                "sha256": hashlib.sha256(raw).hexdigest(),
            }
        )
    return entries


def _write_multi_server_records(records_path: str, instrument: _MultiServerFixes, port_xml: _PortXmlCanonical, abort: object) -> None:
    """The records file ``multi_server_stock.py`` and ``multi_server_surrogates.py`` read: the id of every record, the fix b skips
    and refused roots, the path each stock host failed on, every ``scan_path`` call with its match count (each entry naming its
    plan's position in the request), the fix d substitutions and, for a run that raised, where (``stage``: ``build_plans`` or
    ``run``; ``run``: which run, from 1), the exception's type, text and file name with the progress, host results and cache
    entries it left."""

    records = {
        "records": instrument.records,
        "skipped": instrument.skipped,
        "root_errors": instrument.root_errors,
        "stock_errors": instrument.stock_errors,
        "scans": instrument.scans,
        "port_xml_canonical": port_xml.substituted,
        "abort": abort,
    }
    Path(records_path).write_text(json.dumps(_escape_lone_surrogates(records), sort_keys=True, ensure_ascii=False), encoding="utf-8")


def _error_payload(exc: BaseException) -> dict[str, object]:
    """The exception as ``{"type", "message"}``: ``type(exc).__name__`` and ``str(exc)`` (``SystemExit`` its code as text)."""

    message = str(exc.code) if isinstance(exc, SystemExit) else str(exc)
    return {"type": type(exc).__name__, "message": message}


def _emit_keyed(record: dict[str, object]) -> None:
    record["key_order"] = _key_order(record)
    _emit(record)


def _stage(record: dict[str, object], key: str, produce) -> None:
    """``record[key] = produce()``, or ``{"error": ...}`` when it raises."""

    try:
        record[key] = produce()
    except Exception as exc:  # one stage never aborts the record
        record[key] = {"error": _error_payload(exc)}


def _applied_payload(match) -> dict[str, object]:
    return {
        "profile": match.profile.name,
        "config": match.config.identifier,
        "path": match.config.path,
        "path_glob": match.config.path_glob,
        "expected_format": match.config.expected_format,
        "expected_variant": match.config.expected_variant,
    }


def cmd_profile_store(args: argparse.Namespace) -> int:
    from driftbuster import profile_cli

    record: dict[str, object] = {}
    try:
        store = profile_cli._store_from_payload(profile_cli._load_json(Path(args.payload)))
    except Exception as exc:
        record["error"] = _error_payload(exc)
        _emit_keyed(record)
        return 0
    normalise = profile_cli._normalise_payload
    tags = None if args.tags is None else args.tags.split(",")
    _stage(record, "summary", lambda: normalise(store.summary()))
    _stage(record, "applicable_profiles", lambda: [profile.name for profile in store.applicable_profiles(tags)])
    _stage(record, "matching_configs", lambda: [_applied_payload(match) for match in store.matching_configs(tags, relative_path=args.path)])
    snapshot = normalise(store.to_dict())
    identifiers = [config["id"] for profile in snapshot["profiles"] for config in profile["configs"]] + ["<no-such-config>"]
    record["find_config"] = [
        {"id": identifier, "matches": [_applied_payload(match) for match in store.find_config(identifier)]} for identifier in identifiers
    ]
    record["to_dict"] = snapshot
    _stage(record, "round_trip", lambda: normalise(profile_cli._store_from_payload(json.loads(json.dumps(snapshot))).to_dict()))
    _emit_keyed(record)
    return 0


def cmd_profile_diff(args: argparse.Namespace) -> int:
    from driftbuster import profile_cli

    record: dict[str, object] = {}
    try:
        baseline = profile_cli._load_json(Path(args.baseline))
        current = profile_cli._load_json(Path(args.current))
        record["diff"] = profile_cli._normalise_payload(profile_cli.diff_summary_snapshots(baseline, current))
    except Exception as exc:
        record["error"] = _error_payload(exc)
    _emit_keyed(record)
    return 0


WORKDIR_TOKEN = "<workdir>"
RUN_PROFILE_TIMESTAMP = "20250102T030405Z"


def _respell(value: object, prefix: str) -> object:
    """Every str with the scratch working directory replaced by ``<workdir>``."""

    if isinstance(value, str):
        return value.replace(prefix, WORKDIR_TOKEN)
    if isinstance(value, dict):
        return {key: _respell(item, prefix) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_respell(item, prefix) for item in value]
    return value


def _profiles_listing(work: Path, prefix: str) -> list[dict[str, object]]:
    """Every file under ``Profiles/`` by code point order of its posix path: size, SHA-256 and, for profile.json and
    metadata.json, the text."""

    root = work / "Profiles"
    entries: list[dict[str, object]] = []
    if not root.is_dir():
        return entries
    for directory, _dirs, names in os.walk(root):
        for name in names:
            path = Path(directory) / name
            raw = path.read_bytes()
            entry: dict[str, object] = {
                "path": path.relative_to(work).as_posix(),
                "size": len(raw),
                "sha256": hashlib.sha256(raw).hexdigest(),
            }
            if name in {"profile.json", "metadata.json"}:
                entry["text"] = raw.decode("utf-8", "replace").replace(prefix, WORKDIR_TOKEN)
            entries.append(entry)
    return sorted(entries, key=lambda entry: str(entry["path"]))


class _GuardedCopy:
    """Fix g seam for run-profile: each ``copy_with_secret_filter`` call gets ``PARITY_SECRETS_TIMEOUT`` seconds; a copy that has not
    returned is redone with ``secrets_guard.py``'s reference model (findings and log lines of the abandoned copy discarded), whose
    guards are recorded. Each substitution is reported on stderr as ``fix g: <display path>``."""

    def __init__(self) -> None:
        self.original = secret_scanning.copy_with_secret_filter
        self.guards: list[dict[str, object]] = []
        self.timeout = float(os.environ.get("PARITY_SECRETS_TIMEOUT") or 0)

    def __enter__(self):
        secret_scanning.copy_with_secret_filter = self  # type: ignore[assignment]
        return self

    def __exit__(self, *exc_info) -> None:
        secret_scanning.copy_with_secret_filter = self.original

    def __call__(self, source, destination, *, display_path, context, log, binary_detector=None):
        if self.timeout <= 0:
            return self.original(source, destination, display_path=display_path, context=context, log=log)
        kept = len(context.findings)
        buffered: list[str] = []
        previous = signal.signal(signal.SIGALRM, _raise_timeout)
        signal.setitimer(signal.ITIMER_REAL, self.timeout)
        try:
            result = self.original(source, destination, display_path=display_path, context=context, log=buffered.append)
        except _SecretTimeout:
            result = None
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, previous)
        if result is None:
            del context.findings[kept:]
            buffered = []
            print(f"fix g: {display_path}", file=sys.stderr)
            result = self._model_copy(Path(source), Path(destination), display_path, context, buffered.append)
        for message in buffered:
            log(message)
        return result

    def _model_copy(self, source: Path, destination: Path, display_path: str, context, log) -> tuple[int, str]:
        import secrets_guard

        destination.parent.mkdir(parents=True, exist_ok=True)
        sanitized: list[str] = []
        matches = 0
        with source.open("r", encoding="utf-8", errors="replace") as handle:
            for lineno, line in enumerate(handle, 1):
                guards: list[dict[str, object]] = []
                working, findings, logs = secrets_guard._redact_line(line, lineno, display_path, context, guards)
                self.guards.extend({"path": display_path, **guard} for guard in guards)
                context.findings.extend(findings)
                for message in logs:
                    log(message)
                matches += len(logs)
                sanitized.append(working if logs else line)
        if matches:
            destination.write_text("".join(sanitized), encoding="utf-8")
            log(f"scrubbed {matches} potential secret line(s) from {display_path}")
        else:
            shutil.copy2(source, destination)
        return destination.stat().st_size, secret_scanning.hash_file(destination)


class _SurrogateNameLookups:
    """Decision R seam for run-profile (``PARITY_RUN_PROFILE_SURROGATE_NAMES=1``): a ``$name`` / ``${name}`` lookup in ``os.environ`` or a
    ``~name`` lookup in ``pwd.getpwnam`` whose name cannot be encoded (it holds an unpaired surrogate) finds nothing instead of raising
    ``UnicodeEncodeError``, which is how ``PythonOsPath.ExpandVars`` / ``ExpandUser`` treat such a name, so the rest of the run is Python's
    own. Each lookup handled so is reported on stderr as ``surrogate name lookup: <ascii name>``."""

    MISSING_KEY = b"\0parity-unencodable-name"

    def __init__(self) -> None:
        import pwd

        self.pwd = pwd
        self.original_encodekey = os.environ.encodekey  # type: ignore[attr-defined]
        self.original_getpwnam = pwd.getpwnam

    def __enter__(self):
        def encodekey(key):
            try:
                return self.original_encodekey(key)
            except UnicodeEncodeError:
                print(f"surrogate name lookup: {ascii(key)}", file=sys.stderr)
                return self.MISSING_KEY

        def getpwnam(name):
            try:
                return self.original_getpwnam(name)
            except UnicodeEncodeError:
                print(f"surrogate name lookup: {ascii(name)}", file=sys.stderr)
                raise KeyError(name) from None

        os.environ.encodekey = encodekey  # type: ignore[attr-defined]
        self.pwd.getpwnam = getpwnam
        return self

    def __exit__(self, *exc_info) -> None:
        os.environ.encodekey = self.original_encodekey  # type: ignore[attr-defined]
        self.pwd.getpwnam = self.original_getpwnam


def _structured_entry(entry: object) -> bool:
    """A structured source: a mapping naming a registry scan or SQL snapshot, one ``OfflineCollectionSource.from_dict`` refuses, or one
    that sets an alias, optional or exclude patterns (the port's ``RunProfile.IsStructuredPayload``)."""

    from driftbuster import offline_runner

    if not isinstance(entry, dict):
        return False
    if "registry_scan" in entry or "sql_snapshot" in entry:
        return True
    try:
        source = offline_runner.OfflineCollectionSource.from_dict(entry)
    except Exception:
        return True
    return bool(source.alias or source.optional or source.exclude)


def _structured(payload: object) -> bool:
    sources = payload.get("sources") if isinstance(payload, dict) else None
    return isinstance(sources, list) and any(_structured_entry(entry) for entry in sources)


def _path_only_sources(payload: object) -> object:
    """A mapping source holding only a path is a string source (plan decision): each becomes ``from_dict(entry).path``."""

    from driftbuster import offline_runner

    if not isinstance(payload, dict) or not isinstance(payload.get("sources"), list):
        return payload
    sources = [
        offline_runner.OfflineCollectionSource.from_dict(entry).path if isinstance(entry, dict) else entry for entry in payload["sources"]
    ]
    return {**payload, "sources": sources}


def _structured_profile_json(config) -> str:
    """``profile.json`` as the port writes a structured profile: ``RunProfile.to_dict()`` of the profile's name, description, baseline,
    options and secret scanner, with each source a bare path when it sets nothing else, otherwise ``{path, alias?, optional?, exclude?}``,
    through ``json.dumps(indent=2, sort_keys=True)``."""

    from driftbuster.core import run_profiles

    profile = config.profile
    model = run_profiles.RunProfile(
        name=profile.name,
        description=profile.description,
        sources=(),
        baseline=profile.baseline,
        options=profile.options,
        secret_scanner=profile.secret_scanner,
    )
    payload = dict(model.to_dict())
    sources: list[object] = []
    for source in profile.sources:
        if not (source.alias or source.optional or source.exclude):
            sources.append(source.path)
            continue
        entry: dict[str, object] = {"path": source.path}
        if source.alias:
            entry["alias"] = source.alias
        if source.optional:
            entry["optional"] = True
        if source.exclude:
            entry["exclude"] = list(source.exclude)
        sources.append(entry)
    payload["sources"] = sources
    return json.dumps(payload, indent=2, sort_keys=True)


def _collected_sort_key(entry: dict[str, object]) -> tuple[str, str, str]:
    return (str(entry["source"]), str(entry["directory"] or ""), str(entry["relative"]))


def _offline_run(payload: dict[str, object], scratch: Path, guarded: _GuardedCopy) -> dict[str, object]:
    """What ``offline_runner.execute_config`` collects for the profile (plan decision, structured sources): ``collected``, the secret
    ``findings`` and fix g ``redaction_guard`` entries with each display path under its source directory (the offline runner's first part,
    the source's directory under ``data/``, dropped, as the port reports paths under the source directory), both sorted, and
    ``profile_json``, the text the port writes for the profile."""

    from driftbuster import offline_runner

    config = offline_runner.OfflineRunnerConfig.from_dict(
        {
            "profile": payload,
            "runner": {
                "compress": False,
                "cleanup_staging": False,
                "include_logs": False,
                "include_manifest": False,
                "include_config": False,
                "output_directory": str(scratch / "offline"),
            },
        }
    )
    contexts: list[object] = []
    original_build = offline_runner._build_secret_context

    def capture(options, secret_scanner):
        context = original_build(options, secret_scanner)
        contexts.append(context)
        return context

    offline_runner._build_secret_context = capture
    try:
        result = offline_runner.execute_config(config, timestamp=RUN_PROFILE_TIMESTAMP)
    finally:
        offline_runner._build_secret_context = original_build
    aliases = {}
    for source in config.profile.sources:
        aliases.setdefault(source.path, getattr(source, "alias", None))
    collected = [
        {
            "source": entry.source,
            "directory": entry.relative_path.parts[0] if aliases.get(entry.source) else None,
            "relative": Path(*entry.relative_path.parts[1:]).as_posix(),
            "size": entry.size,
            "sha256": entry.sha256,
        }
        for entry in result.files
    ]

    def under_source(path: str) -> str:
        return path.split("/", 1)[1] if "/" in path else path

    findings = [
        {"path": under_source(finding.path), "rule": finding.rule, "line": finding.line, "snippet": finding.snippet}
        for context in contexts
        for finding in context.findings
    ]
    guards = [{**guard, "path": under_source(str(guard["path"]))} for guard in guarded.guards]
    return {
        "collected": sorted(collected, key=_collected_sort_key),
        "findings": _sorted_findings(findings),
        "redaction_guard": _sorted_findings(guards),
        "profile_json": _structured_profile_json(config),
    }


def _sorted_findings(entries: list[dict[str, object]]) -> list[dict[str, object]]:
    return sorted(entries, key=lambda entry: json.dumps(entry, sort_keys=True, ensure_ascii=False))


def cmd_run_profile(args: argparse.Namespace) -> int:
    from driftbuster.core import run_profiles

    profile_path = Path(args.profile).resolve()
    workdir = Path(args.workdir).resolve()
    scratch = Path(args.scratch) if args.scratch else Path(tempfile.mkdtemp(prefix="driftbuster-parity-run-profile-"))
    scratch.mkdir(parents=True, exist_ok=True)
    work = scratch / "work"
    if workdir.is_dir():
        shutil.copytree(workdir, work, symlinks=True)
    else:
        work.mkdir()
    previous = os.getcwd()
    os.chdir(work)
    prefix = os.getcwd()
    record: dict[str, object] = {}
    lookups = _SurrogateNameLookups() if os.environ.get("PARITY_RUN_PROFILE_SURROGATE_NAMES") == "1" else contextlib.nullcontext()
    try:
        payload = json.loads(profile_path.read_text(encoding="utf-8"))
        if _structured(payload):
            record["mode"] = "offline-runner"
            with _GuardedCopy() as guarded, lookups:
                try:
                    record.update(_offline_run(payload, scratch, guarded))
                except Exception as exc:
                    record["error"] = _error_payload(exc)
            _emit_keyed(_respell(record, prefix))  # type: ignore[arg-type]
            return 0
        with _GuardedCopy() as guarded, lookups:
            try:
                profile = run_profiles.RunProfile.from_dict(_path_only_sources(payload))
                result = run_profiles.execute_profile(profile, timestamp=RUN_PROFILE_TIMESTAMP)
                record["result"] = json.loads(json.dumps(result.to_dict()))
                record["collected"] = _run_collected(result)
            except Exception as exc:
                record["error"] = _error_payload(exc)
            if guarded.guards:
                record["redaction_guard"] = guarded.guards
        record["profiles"] = _profiles_listing(work, prefix)
        _emit_keyed(_respell(record, prefix))  # type: ignore[arg-type]
    finally:
        os.chdir(previous)
        shutil.rmtree(scratch, ignore_errors=True)
    return 0


def _run_collected(result) -> list[dict[str, object]]:
    """``collected`` over an ``execute_profile`` result, as the port computes it: the file's first part under the run is its source
    directory, named only when the source has an alias (never, for string sources)."""

    collected = []
    for entry in result.files:
        parts = entry.destination.relative_to(result.output_dir).parts
        collected.append(
            {"source": entry.source, "directory": None, "relative": Path(*parts[1:]).as_posix(), "size": entry.size, "sha256": entry.sha256}
        )
    return sorted(collected, key=_collected_sort_key)


class _JsonCallBudget:
    """Interpreter-limit seam for schedule (``PARITY_SCHEDULE_JSON_CALL_BUDGET=1``, "CPython limits inside json.loads" in
    expected_divergences.md). At the deepest nesting ``json.loads`` decodes (9998 containers under this dump), the C scanner has no
    recursion budget left for the call it makes to decode ``NaN``, ``Infinity`` or ``-Infinity`` (``parse_constant``) or an integer
    literal of more than 4300 digits, and raises ``RecursionError: maximum recursion depth exceeded while calling a Python object``.
    The port decodes the constant and raises the digit limit's ``ValueError`` there. Only when ``json.loads`` raises exactly that
    ``RecursionError``, this decodes the text again with every such literal in a value position replaced by a string sentinel; when
    that decode succeeds, the sentinels become the floats and the first integer literal past the limit raises ``int()``'s own
    ``ValueError``, as a decode with one more call of budget would; otherwise the original ``RecursionError`` is raised. Each decode
    handled so is reported on stderr as ``json call budget: <n> literal(s)``."""

    MESSAGE = "maximum recursion depth exceeded while calling a Python object"
    SENTINEL = "\0parity-json-call-budget-"
    _NUMBER = re.compile(r"-?(?:0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?")
    _CONSTANTS = {"NaN": float("nan"), "Infinity": float("inf"), "-Infinity": float("-inf")}

    def __enter__(self):
        self.original = json.loads
        json.loads = self.loads  # type: ignore[assignment]
        return self

    def __exit__(self, *exc_info) -> None:
        json.loads = self.original  # type: ignore[assignment]

    def loads(self, text, *args, **kwargs):
        try:
            return self.original(text, *args, **kwargs)
        except RecursionError as exc:
            if args or kwargs or not isinstance(text, str) or str(exc) != self.MESSAGE:
                raise
            literals = self._value_literals(text)
            if not literals:
                raise
            pieces, last = [], 0
            for index, (start, end, _) in enumerate(literals):
                pieces += [text[last:start], json.dumps(self.SENTINEL + str(index))]
                last = end
            try:
                value = self.original("".join(pieces) + text[last:])
            except (ValueError, RecursionError):
                raise exc from None
            print(f"json call budget: {len(literals)} literal(s)", file=sys.stderr)
            for _, _, literal in literals:
                if literal not in self._CONSTANTS:
                    int(literal)
            return self._restore(value, literals)

    def _restore(self, value, literals):
        # Iterative: the decoded value is nested as deep as the decoder allows.
        def swap(item):
            if isinstance(item, str) and item.startswith(self.SENTINEL):
                return self._CONSTANTS[literals[int(item[len(self.SENTINEL):])][2]]
            return item

        stack = [value]
        while stack:
            container = stack.pop()
            keys = container.keys() if isinstance(container, dict) else range(len(container)) if isinstance(container, list) else ()
            for key in keys:
                container[key] = swap(container[key])
                if isinstance(container[key], (dict, list)):
                    stack.append(container[key])
        return swap(value)

    def _value_literals(self, text: str) -> list[tuple[int, int, str]]:
        """``(start, end, literal)`` of every ``NaN`` / ``Infinity`` / ``-Infinity`` and integer literal of more than 4300 digits in a
        value position (after ``[``, after ``:``, after ``,`` inside an array, or at the top level), outside strings."""

        literals: list[tuple[int, int, str]] = []
        stack: list[str] = []
        expecting_value = True
        index = 0
        while index < len(text):
            char = text[index]
            if char == '"':
                index += 1
                while index < len(text) and text[index] != '"':
                    index += 2 if text[index] == "\\" else 1
                expecting_value = False
                index += 1
                continue
            if char in "[{":
                stack.append(char)
                expecting_value = char == "["
            elif char in "]}":
                if stack:
                    stack.pop()
                expecting_value = False
            elif char == ":":
                expecting_value = True
            elif char == ",":
                expecting_value = bool(stack) and stack[-1] == "["
            elif expecting_value and not char.isspace():
                constant = next((name for name in self._CONSTANTS if text.startswith(name, index)), None)
                number = None if constant else self._NUMBER.match(text, index)
                if constant:
                    literals.append((index, index + len(constant), constant))
                    index += len(constant)
                elif number:
                    whole = number.group(0)
                    if number.group(1) is None and number.group(2) is None and len(whole.lstrip("-")) > 4300:
                        literals.append((index, number.end(), whole))
                    index = number.end()
                else:
                    index += 1
                expecting_value = False
                continue
            index += 1
        return literals


def cmd_schedule(args: argparse.Namespace) -> int:
    from datetime import datetime

    from driftbuster import run_profiles_cli, scheduler

    now = datetime.fromisoformat(args.now)
    original_now, original_print = scheduler.ProfileScheduler._now, run_profiles_cli._print_json
    scratch = Path(tempfile.mkdtemp(prefix="driftbuster-parity-schedule-"))
    state = scratch / "state.json"
    if Path(args.state).is_file():
        shutil.copyfile(args.state, state)
    captured: list[object] = []
    argv = ["schedule", args.command, "--config", args.config, "--state", str(state)]
    for flag, value in (("--at", args.at), ("--name", args.name), ("--completed-at", args.completed_at), ("--resume-at", args.resume_at)):
        if value is not None:
            argv += [flag, value]
    record: dict[str, object] = {}
    scheduler.ProfileScheduler._now = staticmethod(lambda: now)  # type: ignore[method-assign]
    run_profiles_cli._print_json = captured.append
    budget = _JsonCallBudget() if os.environ.get("PARITY_SCHEDULE_JSON_CALL_BUDGET") == "1" else contextlib.nullcontext()
    try:
        try:
            with budget:
                run_profiles_cli.main(argv)
            record["output"] = json.loads(json.dumps(captured[0])) if captured else None
        except (Exception, SystemExit) as exc:
            record["error"] = _error_payload(exc)
        record["state"] = state.read_text(encoding="utf-8") if state.is_file() else None
    finally:
        scheduler.ProfileScheduler._now = original_now  # type: ignore[method-assign]
        run_profiles_cli._print_json = original_print
        shutil.rmtree(scratch, ignore_errors=True)
    _emit_keyed(record)
    return 0


SQL_EXPORT_TIMESTAMP = "2025-04-05T06:07:08.090807+00:00"


def _fixed_datetime(moment):
    """A ``datetime`` subclass whose ``now`` returns ``moment`` (a module's ``datetime`` name swapped for it)."""

    from datetime import datetime

    class _Fixed(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return moment

    return _Fixed


@contextlib.contextmanager
def _patched(target, name, value):
    original = getattr(target, name)
    setattr(target, name, value)
    try:
        yield
    finally:
        setattr(target, name, original)


def _file_entry(path: Path, relative: str, prefix: str | None = None) -> dict[str, object]:
    """``{"path", "size", "sha256", "text"}``: the bytes' size and digest and their text decoded as UTF-8 with replacement (the scratch
    working directory respelled ``<workdir>`` when ``prefix`` is given)."""

    raw = path.read_bytes()
    text = raw.decode("utf-8", "replace")
    return {
        "path": relative,
        "size": len(raw),
        "sha256": hashlib.sha256(raw).hexdigest(),
        "text": text.replace(prefix, WORKDIR_TOKEN) if prefix else text,
    }


def _build_sqlite(target: Path, database: dict[str, object], case_dir: Path) -> None:
    """The case's database at ``target``: ``script`` (a file beside the case, its bytes decoded as strict UTF-8 with the line endings
    untouched, as ``ParityDump.BuildSqlite`` reads it, run with ``executescript`` in autocommit mode), ``hex`` (the file's bytes),
    ``kind: directory`` or ``kind: missing``."""

    import sqlite3

    target.parent.mkdir(parents=True, exist_ok=True)
    if "script" in database:
        connection = sqlite3.connect(target, isolation_level=None)
        try:
            connection.executescript((case_dir / str(database["script"])).read_bytes().decode("utf-8"))
        finally:
            connection.close()
    elif "hex" in database:
        target.write_bytes(bytes.fromhex(str(database["hex"])))
    elif database.get("kind") == "directory":
        target.mkdir()
    elif database.get("kind") != "missing":
        raise SystemExit(f"unsupported database spec {database!r}")


def cmd_sql_export(args: argparse.Namespace) -> int:
    """``write_sqlite_snapshot(Path(case["path"]), Path("snapshot.json"), **case["options"])`` in a fresh scratch working directory holding
    the case's database, with ``datetime.now`` fixed at ``case["timestamp"]``: ``snapshot`` (``to_dict()``) and ``file`` (the written
    bytes), or ``error``."""

    from driftbuster.sql import snapshots

    case_path = Path(args.case).resolve()
    case = json.loads(case_path.read_text(encoding="utf-8"))
    scratch = Path(args.scratch) if args.scratch else Path(tempfile.mkdtemp(prefix="driftbuster-parity-sql-export-"))
    work = scratch / "work"
    work.mkdir(parents=True)
    previous = os.getcwd()
    record: dict[str, object] = {}
    try:
        _build_sqlite(work / str(case.get("file", "case.sqlite")), case.get("database", {}), case_path.parent)
        os.chdir(work)
        from datetime import datetime

        moment = datetime.fromisoformat(str(case.get("timestamp", SQL_EXPORT_TIMESTAMP)))
        destination = Path("snapshot.json")
        with _patched(snapshots, "datetime", _fixed_datetime(moment)):
            try:
                source = Path(str(case.get("path", "case.sqlite")))
                snapshot = snapshots.write_sqlite_snapshot(source, destination, **case.get("options", {}))
                record["snapshot"] = snapshot.to_dict()
                record["file"] = _file_entry(destination, destination.name)
            except Exception as exc:
                record["error"] = _error_payload(exc)
    finally:
        os.chdir(previous)
        shutil.rmtree(scratch, ignore_errors=True)
    _emit_keyed(record)
    return 0


REPORT_TIMESTAMP = "2026-09-15T18:22:05.123456+00:00"


def _report_match(spec: dict[str, object]):
    """A ``DetectionMatch`` from a case spec, its confidence ``float()`` as the port's typed model holds it; ``validate`` enriches the
    metadata with the catalog."""

    from driftbuster.catalog import DETECTION_CATALOG
    from driftbuster.core.types import DetectionMatch, validate_detection_metadata

    match = DetectionMatch(
        plugin_name=spec["plugin"],  # type: ignore[arg-type]
        format_name=spec["format"],  # type: ignore[arg-type]
        variant=spec.get("variant"),  # type: ignore[arg-type]
        confidence=float(spec["confidence"]),  # type: ignore[arg-type]
        reasons=list(spec.get("reasons", [])),  # type: ignore[call-overload]
        metadata=spec.get("metadata"),  # type: ignore[arg-type]
    )
    if spec.get("validate"):
        match.metadata = validate_detection_metadata(match, DETECTION_CATALOG)
    return match


def _report_text(value: object) -> str:
    """A diff side: the text, or ``{"repeat": text, "count": n}`` for ``text * n`` (keeps large inputs out of the case file)."""

    if isinstance(value, dict):
        return str(value["repeat"]) * int(value["count"])
    return value  # type: ignore[return-value]


def _report_inputs(case_text: str) -> dict[str, object]:
    """The case rebuilt from its text (every stage gets fresh inputs: the adapters merge ``run_metadata`` into mappings they are handed and
    a redactor keeps its counts): matches, diffs (mappings, ``build_unified_diff`` results or ``build_binary_diff`` results), hunt hits
    (mappings or ``HuntHit`` values) and the redaction arguments."""

    from driftbuster.hunt import HuntHit, HuntRule
    from driftbuster.reporting.diff import build_binary_diff, build_unified_diff
    from driftbuster.reporting.redaction import RedactionFilter

    case = json.loads(case_text)
    diffs: list[object] = []
    for entry in case.get("diffs", []):
        if entry["kind"] == "mapping":
            diffs.append(entry["value"])
        elif entry["kind"] == "binary":
            diffs.append(
                build_binary_diff(
                    bytes.fromhex(entry["before_hex"]),
                    bytes.fromhex(entry["after_hex"]),
                    from_label=entry.get("from_label", "before"),
                    to_label=entry.get("to_label", "after"),
                    label=entry.get("label"),
                    reason=entry.get("reason"),
                )
            )
        else:
            diffs.append(
                build_unified_diff(
                    _report_text(entry["before"]),
                    _report_text(entry["after"]),
                    content_type=entry.get("content_type", "text"),
                    from_label=entry.get("from_label", "before"),
                    to_label=entry.get("to_label", "after"),
                    label=entry.get("label"),
                    mask_tokens=tuple(entry["mask_tokens"]) if "mask_tokens" in entry else None,
                    context_lines=entry.get("context_lines", 3),
                )
            )
    hits: list[object] = []
    for entry in case.get("hunt_hits", []):
        if entry["kind"] == "mapping":
            hits.append(entry["value"])
            continue
        rule = entry["rule"]
        hits.append(
            HuntHit(
                rule=HuntRule(
                    name=rule["name"],
                    description=rule["description"],
                    token_name=rule.get("token_name"),
                    keywords=tuple(rule.get("keywords", ())),
                    patterns=tuple(rule.get("patterns", ())),
                ),
                path=Path(entry["path"]),
                line_number=entry["line_number"],
                excerpt=entry["excerpt"],
            )
        )
    redaction: dict[str, object] = {}
    if "redactor" in case:
        redaction["redactor"] = RedactionFilter(tokens=tuple(case["redactor"]["tokens"]), placeholder=case["redactor"]["placeholder"])
    if "mask_tokens" in case:
        redaction["mask_tokens"] = tuple(case["mask_tokens"])
    if "placeholder" in case:
        redaction["placeholder"] = case["placeholder"]
    return {
        "case": case,
        "matches": [_report_match(spec) for spec in case.get("matches", [])],
        "diffs": diffs,
        "hunt_hits": hits,
        "redaction": redaction,
    }


def cmd_report(args: argparse.Namespace) -> int:
    """``render_html_report`` (``html``), ``render_json_lines`` with sorted and insertion-ordered keys (``json_lines``,
    ``json_lines_unsorted``), ``summarise_detections`` (``summary``) and ``build_snapshot_manifest`` (``manifest``) over the case, with
    ``datetime.now`` fixed at ``case["timestamp"]``; a stage that raises is that stage's ``error``. ``PARITY_REPORT_RECURSION_LIMIT``
    raises ``sys.setrecursionlimit`` first, the oracle run ``run_parity.sh report`` makes for a stage the stock limit ends with
    ``RecursionError`` (``phase7_divergences.py``)."""

    from datetime import datetime

    if os.environ.get("PARITY_REPORT_RECURSION_LIMIT"):
        sys.setrecursionlimit(int(os.environ["PARITY_REPORT_RECURSION_LIMIT"]))

    from driftbuster.reporting import html as html_module
    from driftbuster.reporting import snapshot as snapshot_module
    from driftbuster.reporting.json_lines import render_json_lines
    from driftbuster.reporting.summary import summarise_detections

    case_text = Path(args.case).read_text(encoding="utf-8")
    moment = datetime.fromisoformat(str(json.loads(case_text).get("timestamp", REPORT_TIMESTAMP)))
    record: dict[str, object] = {}

    def html() -> object:
        inputs = _report_inputs(case_text)
        case = inputs["case"]
        kwargs = {key: case[key] for key in ("title", "profile_summary", "extra_metadata", "warnings", "legal_notice") if key in case}
        if "diffs" in case:
            kwargs["diffs"] = inputs["diffs"]
        if "hunt_hits" in case:
            kwargs["hunt_hits"] = inputs["hunt_hits"]
        return html_module.render_html_report(inputs["matches"], **kwargs, **inputs["redaction"])

    def json_lines(sort_keys: bool) -> object:
        inputs = _report_inputs(case_text)
        case = inputs["case"]
        return render_json_lines(
            inputs["matches"],
            profile_summary=case.get("profile_summary"),
            hunt_hits=inputs["hunt_hits"] or None,
            extra_metadata=case.get("extra_metadata"),
            sort_keys=sort_keys,
            **inputs["redaction"],
        )

    def manifest() -> object:
        inputs = _report_inputs(case_text)
        snapshot = inputs["case"].get("snapshot", {})
        kwargs = {key: snapshot[key] for key in ("output_name", "operator", "legal_metadata", "extra_metadata") if key in snapshot}
        return snapshot_module.build_snapshot_manifest(inputs["matches"], **kwargs, **inputs["redaction"])

    with _patched(html_module, "datetime", _fixed_datetime(moment)), _patched(snapshot_module, "datetime", _fixed_datetime(moment)):
        _stage(record, "html", html)
        _stage(record, "json_lines", lambda: json_lines(True))
        _stage(record, "json_lines_unsorted", lambda: json_lines(False))
        _stage(record, "summary", lambda: summarise_detections(_report_inputs(case_text)["matches"]))
        _stage(record, "manifest", manifest)
    _emit_keyed(record)
    return 0


def _registry_decode(value: object) -> object:
    """A case value as ``winreg`` hands it back: ``{"$bytes": hex}`` is bytes, containers are walked."""

    if isinstance(value, dict):
        if set(value) == {"$bytes"}:
            return bytes.fromhex(value["$bytes"])
        return {key: _registry_decode(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_registry_decode(item) for item in value]
    return value


_REGISTRY_ERRORS = {"RuntimeError": RuntimeError, "ValueError": ValueError}


def _registry_backend(tree: list[dict[str, object]]):
    """The ``scan._Backend`` a case's ``tree`` describes. The first key whose hive and path equal the lookup and whose ``view`` (default
    ``"*"``) is ``"*"`` or the requested view answers; a key with ``denied`` lists nothing (``winreg.OpenKeyEx`` refused, as the Windows
    backend treats it); a key with ``error`` raises ``OSError(errno, os.strerror(errno))`` for ``{"errno"}`` (``PermissionError`` for
    13), or ``RuntimeError`` / ``ValueError`` for ``{"type", "message"}``, from ``enum_values`` (and from ``enum_subkeys`` when
    ``error_on`` is ``"subkeys"`` or ``"both"``); a lookup that finds no key lists nothing. A value is ``[name, data]`` or ``[name, data,
    registry type]`` (the type documents which ``winreg`` value the data stands for)."""

    from driftbuster.registry import scan

    class _Fake(scan._Backend):
        def _find(self, hive, path, view):
            for key in tree:
                if key["hive"] == hive and key["path"] == path and key.get("view", "*") in {"*", view}:
                    return key
            return None

        def _raise(self, key, operation):
            error = key.get("error")
            if not error or key.get("error_on", "values") not in {operation, "both"}:
                return
            if "errno" in error:
                raise OSError(error["errno"], os.strerror(error["errno"]))
            raise _REGISTRY_ERRORS[error["type"]](error["message"])

        def enum_subkeys(self, hive, path, view):
            key = self._find(hive, path, view)
            if not key or key.get("denied"):
                return []
            self._raise(key, "subkeys")
            return list(key.get("subkeys", []))

        def enum_values(self, hive, path, view):
            key = self._find(hive, path, view)
            if not key or key.get("denied"):
                return []
            self._raise(key, "values")
            return [(entry[0], _registry_decode(entry[1])) for entry in key.get("values", [])]

    return _Fake()


def _registry_outcome(produce) -> dict[str, object]:
    try:
        return {"result": produce()}
    except Exception as exc:
        return {"error": _error_payload(exc)}


def _registry_value(value: object) -> object:
    """A dataclass (``RegistryApp``, ``RegistryHit``, ``RemoteRegistryTarget``, ``OfflineRegistryScanSource``) as its fields, bytes as
    ``{"$bytes": hex}``, tuples as lists."""

    import dataclasses

    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        return {field.name: _registry_value(getattr(value, field.name)) for field in dataclasses.fields(value)}
    if isinstance(value, bytes):
        return {"$bytes": value.hex()}
    if isinstance(value, (list, tuple)):
        return [_registry_value(item) for item in value]
    if isinstance(value, dict):
        return {key: _registry_value(item) for key, item in value.items()}
    return value


def _registry_spec(entry: dict[str, object]):
    from driftbuster.registry import SearchSpec

    kwargs: dict[str, object] = {
        "keywords": tuple(entry.get("keywords", ())),  # type: ignore[arg-type]
        "patterns": tuple(re.compile(pattern) for pattern in entry.get("patterns", ())),  # type: ignore[union-attr]
    }
    for key in ("max_depth", "max_hits", "time_budget_s"):
        if key in entry:
            kwargs[key] = entry[key]
    return SearchSpec(**kwargs)  # type: ignore[arg-type]


def cmd_registry_scan(args: argparse.Namespace) -> int:
    """A case's fake registry (``_registry_backend``) through the instrumented ``driftbuster.registry`` operations: ``enumerate``
    (``enumerate_installed_apps``), ``find_roots`` (``find_app_registry_roots`` over the enumerated applications or the case's own),
    ``searches`` (``search_registry`` over explicit roots or the roots suggested for a token), ``descriptors``
    (``parse_registry_root_descriptor``), ``remote_targets`` (``RemoteRegistryTarget.from_payload``), ``scan_sources``
    (``OfflineRegistryScanSource.from_dict`` and ``destination_name(fallback_index=1)``), ``remote_target_args``
    (``registry_cli._parse_remote_target_arg``), ``cli`` (``registry_cli.main(argv)`` with ``is_windows`` true and the registry calls on
    the fake: the return code or exception and stdout) and ``usage`` (``registry_summary()`` without durations and timestamps)."""

    import io

    import driftbuster.registry as registry
    import driftbuster.registry_cli as registry_cli
    from driftbuster.offline_runner import OfflineRegistryScanSource, RemoteRegistryTarget
    from driftbuster.registry.scan import RegistryApp

    case = json.loads(Path(args.case).read_text(encoding="utf-8"))
    backend = _registry_backend(case.get("tree", []))
    record: dict[str, object] = {}
    apps: object = ()
    if case.get("enumerate", True):
        outcome = _registry_outcome(lambda: registry.enumerate_installed_apps(backend=backend))
        apps = outcome.get("result", ())
        record["enumerate"] = _registry_value(outcome)

    def installed(entry: dict[str, object]) -> object:
        given = entry.get("installed", "enumerated")
        return apps if given == "enumerated" else tuple(RegistryApp(**app) for app in given)  # type: ignore[union-attr]

    def find_roots(entry: dict[str, object]) -> object:
        return [list(root) for root in registry.find_app_registry_roots(entry["token"], installed=installed(entry))]  # type: ignore[arg-type]

    record["find_roots"] = [_registry_outcome(lambda entry=entry: find_roots(entry)) for entry in case.get("find_roots", [])]

    def search(entry: dict[str, object]) -> object:
        given = entry["roots"]
        roots = (
            registry.find_app_registry_roots(given, installed=apps)  # type: ignore[arg-type]
            if isinstance(given, str)
            else tuple(tuple(root) for root in given)  # type: ignore[union-attr]
        )
        return _registry_value(registry.search_registry(roots, _registry_spec(entry), backend=backend))

    record["searches"] = [_registry_outcome(lambda entry=entry: search(entry)) for entry in case.get("searches", [])]
    record["descriptors"] = [
        _registry_outcome(lambda text=text: list(registry.parse_registry_root_descriptor(text).as_tuple()))
        for text in case.get("descriptors", [])
    ]
    record["remote_targets"] = [
        _registry_outcome(lambda payload=payload: _registry_value(RemoteRegistryTarget.from_payload(payload)))
        for payload in case.get("remote_targets", [])
    ]

    def scan_source(payload: object) -> object:
        source = OfflineRegistryScanSource.from_dict(payload)  # type: ignore[arg-type]
        return {"source": _registry_value(source), "destination_name": source.destination_name(fallback_index=1)}

    record["scan_sources"] = [_registry_outcome(lambda payload=payload: scan_source(payload)) for payload in case.get("scan_sources", [])]
    record["remote_target_args"] = [
        _registry_outcome(lambda text=text: registry_cli._parse_remote_target_arg(text)) for text in case.get("remote_target_args", [])
    ]
    runs = []
    for argv in case.get("cli", []):
        stdout = io.StringIO()
        with contextlib.ExitStack() as stack:
            stack.enter_context(_patched(registry_cli, "is_windows", lambda: True))
            enumerate_fake = lambda: registry.enumerate_installed_apps(backend=backend)  # noqa: E731
            stack.enter_context(_patched(registry_cli, "enumerate_installed_apps", enumerate_fake))
            stack.enter_context(
                _patched(registry_cli, "search_registry", lambda roots, spec: registry.search_registry(roots, spec, backend=backend))
            )
            stack.enter_context(contextlib.redirect_stdout(stdout))
            try:
                run: dict[str, object] = {"exit_code": registry_cli.main(argv)}
            except (Exception, SystemExit) as exc:
                run = {"error": _error_payload(exc)}
        run["stdout"] = stdout.getvalue()
        runs.append(run)
    record["cli"] = runs
    record["usage"] = [
        {key: entry[key] for key in ("operation", "calls", "successes", "errors", "last_error")} for entry in registry.registry_summary()
    ]
    _emit_keyed(record)
    return 0


CAPTURE_NOW = "2025-03-12T01:02:03.456789+00:00"


class _RepeatingQueue:
    """Each call takes the next value; once the list is spent the last value repeats (both dumps read the clocks the same way)."""

    def __init__(self, values: list[object], default: object) -> None:
        self.values = list(values) or [default]
        self.index = 0

    def pop(self) -> object:
        value = self.values[min(self.index, len(self.values) - 1)]
        self.index += 1
        return value


_CAPTURE_DEFAULTS = {
    "run": {
        "root": ".", "profiles": None, "profile_tags": [], "glob": "**/*", "hunt_glob": "**/*", "hunt_exclude": [], "skip_hunt": False,
        "sample_size": 128 * 1024, "output_dir": "captures", "capture_id": None, "operator": None, "environment": None, "reason": None,
        "mask_tokens": [], "placeholder": "[REDACTED]", "allow_unmasked": False, "registry_scan": [],
    },
    "export-sql": {
        "database": [], "output_dir": "sql-exports", "table": [], "exclude_table": [], "mask_column": [], "hash_column": [],
        "placeholder": "[REDACTED]", "hash_salt": "", "limit": None, "prefix": "",
    },
    "compare": {"baseline": None, "current": None},
}


def _capture_step(capture, snapshots, step: dict[str, object]) -> dict[str, object]:
    """One ``scripts/capture.py`` command over ``argparse.Namespace`` built from the parser defaults and ``step["args"]``, with
    ``datetime.now`` (``now``, shared by the script and ``driftbuster.sql.snapshots``), ``time.monotonic`` (``monotonic``),
    ``socket.gethostname`` (``host``) and ``os.getenv`` (``env``) pinned, and the hunt's ``default_rules()`` carrying fix e as the port
    ships it (stock rules with ``PARITY_CAPTURE_FIX_E=0``): the return code or exception, stdout and stderr."""

    import io
    from datetime import datetime
    from types import SimpleNamespace

    command = str(step["command"])
    namespace = dict(_CAPTURE_DEFAULTS[command])
    namespace.update(step.get("args", {}))  # type: ignore[arg-type]
    handler = {"run": capture.run_capture, "export-sql": capture.run_sql_export, "compare": capture.compare_snapshots}[command]
    now = _RepeatingQueue(step.get("now", []), CAPTURE_NOW)  # type: ignore[arg-type]
    clock = _RepeatingQueue(step.get("monotonic", []), 100.0)  # type: ignore[arg-type]
    env = dict(step.get("env", {}))  # type: ignore[arg-type]
    host = str(step.get("host", "capture-host.example"))

    class _Now(datetime):
        @classmethod
        def now(cls, tz=None):  # type: ignore[override]
            return datetime.fromisoformat(str(now.pop()))

    stdout, stderr = io.StringIO(), io.StringIO()
    result: dict[str, object] = {"command": command}
    with contextlib.ExitStack() as stack:
        stack.enter_context(_patched(capture, "datetime", _Now))
        stack.enter_context(_patched(snapshots, "datetime", _Now))
        stack.enter_context(_patched(capture, "time", SimpleNamespace(monotonic=lambda: float(clock.pop()))))  # type: ignore[arg-type]
        stack.enter_context(_patched(capture, "socket", SimpleNamespace(gethostname=lambda: host)))
        stack.enter_context(_patched(capture, "os", SimpleNamespace(getenv=lambda key, default=None: env.get(key, default))))
        fix_e = os.environ.get("PARITY_CAPTURE_FIX_E", "1") == "1"
        stack.enter_context(_patched(capture, "default_rules", lambda: _hunt_rules(fix_e=fix_e)))
        stack.enter_context(contextlib.redirect_stdout(stdout))
        stack.enter_context(contextlib.redirect_stderr(stderr))
        try:
            result["exit_code"] = handler(argparse.Namespace(**namespace))
        except Exception as exc:
            result["error"] = _error_payload(exc)
    result["stdout"] = stdout.getvalue()
    result["stderr"] = stderr.getvalue()
    return result


def _tree_files(root: Path) -> dict[str, bytes]:
    return {path.relative_to(root).as_posix(): path.read_bytes() for path in root.rglob("*") if path.is_file() and not path.is_symlink()}


def cmd_capture(args: argparse.Namespace) -> int:
    """Copies the case's ``workdir/`` to ``<scratch>/work``, builds its ``databases`` (``{relative path: script}``, each script run with
    ``executescript``), makes it the working directory and runs each of ``steps`` (``_capture_step``); prints ``steps`` and ``files``: every
    file under the working directory the steps created or changed, by code point order of its posix path, with size, SHA-256 and text, the
    working directory respelled ``<workdir>`` throughout."""

    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
    from driftbuster.sql import snapshots
    from scripts import capture

    case_path = Path(args.case).resolve()
    case = json.loads(case_path.read_text(encoding="utf-8"))
    scratch = Path(args.scratch) if args.scratch else Path(tempfile.mkdtemp(prefix="driftbuster-parity-capture-"))
    scratch.mkdir(parents=True, exist_ok=True)
    work = scratch / "work"
    workdir = case_path.parent / "workdir"
    if workdir.is_dir():
        shutil.copytree(workdir, work, symlinks=True)
    else:
        work.mkdir()
    previous = os.getcwd()
    try:
        for relative, script in case.get("databases", {}).items():
            _build_sqlite(work / relative, {"script": script}, work)
        os.chdir(work)
        prefix = os.getcwd()
        before = _tree_files(work)
        steps = [_capture_step(capture, snapshots, step) for step in case.get("steps", [])]
        after = _tree_files(work)
        files = [
            _file_entry(work / relative, relative, prefix)
            for relative in sorted(after)
            if before.get(relative) != after[relative]
        ]
        record: dict[str, object] = {"steps": steps, "files": files}
        _emit_keyed(_respell(record, prefix))  # type: ignore[arg-type]
    finally:
        os.chdir(previous)
        shutil.rmtree(scratch, ignore_errors=True)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    detect = sub.add_parser("detect")
    detect.add_argument("path")
    detect.add_argument("--plugins", default=None, help="comma-separated plugin names to keep")
    detect.add_argument("--sample-size", type=int, default=None)
    detect.add_argument("--max-total-sample-bytes", type=int, default=None)
    detect.set_defaults(func=cmd_detect)

    decode = sub.add_parser("decode")
    decode.add_argument("path")
    decode.set_defaults(func=cmd_decode)

    diff = sub.add_parser("diff")
    diff.add_argument("before", nargs="?")
    diff.add_argument("after", nargs="?")
    diff.add_argument("--pairs", default=None, help="tab-separated before/after lines")
    diff.add_argument("--content-type", default=None, choices=("text", "json", "xml"))
    diff.add_argument("--context", type=int, default=3)
    diff.add_argument("--mask", action="append", default=[])
    diff.add_argument("--placeholder", default="[REDACTED]")
    diff.add_argument("--label-from", default=None)
    diff.add_argument("--label-to", default=None)
    diff.set_defaults(func=cmd_diff)

    canon = sub.add_parser("canon")
    canon.add_argument("path")
    canon.add_argument("--content-type", required=True, choices=("text", "json", "xml"))
    canon.set_defaults(func=cmd_canon)

    hunt = sub.add_parser("hunt")
    hunt.add_argument("path")
    hunt.add_argument("--glob", default="**/*")
    hunt.add_argument("--exclude", action="append", default=[])
    hunt.set_defaults(func=cmd_hunt)

    secrets = sub.add_parser("secrets")
    secrets.add_argument("path")
    secrets.add_argument("--ruleset", default=None, help="JSON ruleset used as secret_scanner['ruleset']")
    secrets.set_defaults(func=cmd_secrets)

    secrets_context = sub.add_parser("secrets-context")
    secrets_context.add_argument("path")
    secrets_context.set_defaults(func=cmd_secrets_context)

    multi_server = sub.add_parser("multi-server")
    multi_server.add_argument("plans")
    multi_server.add_argument("--sample-budget", type=int, default=None)
    multi_server.add_argument("--sample-size", type=int, default=None)
    multi_server.add_argument("--runs", type=int, default=1)
    multi_server.set_defaults(func=cmd_multi_server)

    profile_store = sub.add_parser("profile-store")
    profile_store.add_argument("payload")
    profile_store.add_argument("--tags", default=None, help="comma-separated activation tags")
    profile_store.add_argument("--path", default=None, help="relative path for matching_configs")
    profile_store.set_defaults(func=cmd_profile_store)

    profile_diff = sub.add_parser("profile-diff")
    profile_diff.add_argument("baseline")
    profile_diff.add_argument("current")
    profile_diff.set_defaults(func=cmd_profile_diff)

    run_profile = sub.add_parser("run-profile")
    run_profile.add_argument("profile")
    run_profile.add_argument("workdir")
    run_profile.add_argument("--scratch", default=None, help="directory created for the run and removed after it")
    run_profile.set_defaults(func=cmd_run_profile)

    schedule = sub.add_parser("schedule")
    schedule.add_argument("config")
    schedule.add_argument("state")
    schedule.add_argument("command", choices=("list", "due", "mark-complete", "skip-until"))
    schedule.add_argument("--at", default=None)
    schedule.add_argument("--name", default=None)
    schedule.add_argument("--completed-at", default=None)
    schedule.add_argument("--resume-at", default=None)
    schedule.add_argument("--now", default="2025-03-01T12:00:00+00:00", help="ProfileScheduler._now")
    schedule.set_defaults(func=cmd_schedule)

    sql_export = sub.add_parser("sql-export")
    sql_export.add_argument("case")
    sql_export.add_argument("--scratch", default=None, help="directory created for the run and removed after it")
    sql_export.set_defaults(func=cmd_sql_export)

    report = sub.add_parser("report")
    report.add_argument("case")
    report.set_defaults(func=cmd_report)

    registry_scan = sub.add_parser("registry-scan")
    registry_scan.add_argument("case")
    registry_scan.set_defaults(func=cmd_registry_scan)

    capture = sub.add_parser("capture")
    capture.add_argument("case")
    capture.add_argument("--scratch", default=None, help="directory created for the run and removed after it")
    capture.set_defaults(func=cmd_capture)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
