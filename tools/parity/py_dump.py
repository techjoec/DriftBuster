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

Every string in a record has each unpaired surrogate (a lone ``\\ud83d`` escape in a JSON key, say) rewritten to the
six characters ``\\uXXXX`` before serialisation, exactly as the port's ``CanonicalJson`` does: a UTF-8 stdout cannot
encode the code point and jq rejects the JSON escape of a lone surrogate. A record the dump itself cannot produce
is emitted as ``{"path", "error": "DumpError: ..."}`` so one file never aborts the run.
"""

from __future__ import annotations

import argparse
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
    """Rewrites every unpaired surrogate in every str (keys included) to the literal text ``\\uXXXX``."""

    if isinstance(value, str):
        return _LONE_SURROGATE.sub(lambda match: f"\\u{ord(match.group()):04x}", value)
    if isinstance(value, dict):
        return {_escape_lone_surrogates(key): _escape_lone_surrogates(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_escape_lone_surrogates(item) for item in value]
    return value


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
    of children for a sequence, None for a scalar."""

    if isinstance(value, dict):
        return [[str(key), _key_order(item)] for key, item in value.items()]
    if isinstance(value, (list, tuple)):
        return [_key_order(item) for item in value]
    return None


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


def _hunt_rules() -> tuple[hunt_module.HuntRule, ...]:
    rules = hunt_module.default_rules()
    if os.environ.get("PARITY_HUNT_FIX_E") != "1":
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

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
