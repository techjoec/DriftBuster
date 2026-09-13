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
``PARITY_SECRETS_TIMEOUT`` set, a file whose copy has not returned after that many seconds is ``{"path", "error": "Timeout"}``. ``secrets-context`` prints ``build_context`` and
``manifest_secret_scanner`` for ``{"options": ..., "secret_scanner": ...}`` files.

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
    original = normalisers[content_type] if content_type in normalisers else None
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

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
