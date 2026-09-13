"""Python side of the parity harness: canonical JSON dumps of engine surfaces.

Temporary; deleted together with the Python package once the C# port is proven.

Usage:
    py_dump.py detect <path> [--plugins name,name] [--sample-size N] [--max-total-sample-bytes N]
    py_dump.py decode <path>

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

Every string in a record has each unpaired surrogate (a lone ``\\ud83d`` escape in a JSON key, say) rewritten to the
six characters ``\\uXXXX`` before serialisation, exactly as the port's ``CanonicalJson`` does: a UTF-8 stdout cannot
encode the code point and jq rejects the JSON escape of a lone surrogate. A record the dump itself cannot produce
is emitted as ``{"path", "error": "DumpError: ..."}`` so one file never aborts the run.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

from driftbuster.core import detector as detector_module
from driftbuster.core.detector import Detector, DetectorIOError
from driftbuster.core.types import MetadataValidationError, _json_safe, validate_detection_metadata
from driftbuster.formats import format_registry as registry

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
    """(relative posix path, path, errored) in Detector.scan_path order; a root failure yields one "." entry."""

    enumerator = _TolerantDetector()
    results = enumerator.scan_path(root)
    errored = set(enumerator.errors)
    if root in errored and not results:
        return [(ROOT_ERROR_PATH, root, True)]
    return [(_relative(root, path), path, path in errored) for path, _ in results]


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

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
