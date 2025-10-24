"""Core detection orchestration for DriftBuster.

Examples
--------
>>> from pathlib import Path
>>> from driftbuster.core.types import DetectionMatch
>>> from driftbuster.formats import format_registry as registry
>>> class StaticPlugin:
...     name = "static-fixture"
...     priority = 0
...     def detect(self, path, sample, text):
...         with path.open("rb") as handle:  # context manager best practice
...             _ = handle.read(1)
...         return DetectionMatch(
...             plugin_name=self.name,
...             format_name="fixture",
...             variant=None,
...             confidence=1.0,
...             reasons=["Static match for doctest"],
...         )
>>> result = scan_file(
...     Path(__file__), sample_size=64, plugins=(StaticPlugin(),)
... )
>>> result.format_name
'fixture'
>>> class PriorityPlugin:
...     name = "priority-fixture"
...     priority = 100
...     def detect(self, path, sample, text):
...         return DetectionMatch(
...             plugin_name=self.name,
...             format_name="fixture",
...             variant="ordered",
...             confidence=0.9,
...             reasons=["Manual ordering"],
...         )
>>> manual = Detector(
...     plugins=(PriorityPlugin(), StaticPlugin()),
...     sample_size=32,
...     sort_plugins=False,
... )
>>> manual.scan_file(Path(__file__)).plugin_name
'priority-fixture'
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Callable, Iterable, List, Optional, Sequence

from ..catalog import DETECTION_CATALOG
from ..formats import format_registry as registry
from .profiles import ProfileStore, ProfiledDetection, normalize_tags
from .types import DetectionMatch, validate_detection_metadata

logger = logging.getLogger(__name__)

_DEFAULT_SAMPLE_SIZE = 128 * 1024  # 128 KiB default clamp.
_MAX_SAMPLE_SIZE = 512 * 1024  # Guardrail against excessive reads.

_DEFAULT_TOTAL_SAMPLE_BUDGET = 16 * 1024 * 1024  # 16 MiB aggregate guardrail.


class DetectorIOError(Exception):
    """Represents an I/O failure that occurred while scanning."""

    def __init__(self, path: Path, reason: str) -> None:
        self.path = Path(path)
        self.reason = reason
        super().__init__(f"{self.path}: {self.reason}")

    def __repr__(self) -> str:  # pragma: no cover - trivial wrapper
        path_repr = str(self.path)
        return f"DetectorIOError(path={path_repr!r}, reason={self.reason!r})"


def _wrap_io_error(path: Path, exc: OSError) -> DetectorIOError:
    """Convert an ``OSError`` into ``DetectorIOError``."""

    return DetectorIOError(path=Path(path), reason=str(exc))


def _validate_sample_size(sample_size: int) -> int:
    """Clamp and validate the requested sample size."""

    if sample_size <= 0:
        raise ValueError("sample_size must be a positive integer")
    if sample_size > _MAX_SAMPLE_SIZE:
        logger.warning(
            "Sample size %s exceeds %s bytes; clamping to guardrail.",
            sample_size,
            _MAX_SAMPLE_SIZE,
        )
        return _MAX_SAMPLE_SIZE
    return sample_size


def _validate_total_sample_budget(value: Optional[int]) -> Optional[int]:
    if value is None:
        return None
    if value <= 0:
        raise ValueError("max_total_sample_bytes must be a positive integer")
    return value


def _titleise_component(component: str) -> str:
    if not component:
        return component
    for index, char in enumerate(component):
        if char.isalpha():
            break
    else:
        return component
    prefix = component[:index]
    alpha = component[index].upper()
    suffix = component[index + 1:]
    return f"{prefix}{alpha}{suffix}"


def _normalise_reason_token(token: str) -> str:
    parts = token.split("-")
    normalised_parts = []
    for part in parts:
        subparts = part.split(":")
        normalised_subparts = [_titleise_component(sub) for sub in subparts]
        normalised_parts.append(":".join(normalised_subparts))
    return "-".join(normalised_parts)


def _normalise_reasons(reasons: Iterable[str]) -> List[str]:
    seen: set[str] = set()
    normalised: List[str] = []
    for raw in reasons:
        text = str(raw).strip()
        if not text:
            continue
        collapsed = " ".join(text.split())
        tokens = [
            _normalise_reason_token(token)
            for token in collapsed.split(" ")
        ]
        formatted = " ".join(tokens)
        if formatted not in seen:
            seen.add(formatted)
            normalised.append(formatted)
    return normalised


class Detector:
    """Coordinates registered format plugins to detect configuration types.

    Example
    -------
    >>> from pathlib import Path
    >>> from driftbuster.formats import format_registry as registry
    >>> class DummyPlugin(registry.FormatPlugin):
    ...     name = "dummy"
    ...     priority = 10
    ...     def detect(self, path, sample, text):
    ...         return None
    >>> detector = Detector(plugins=(DummyPlugin(),), sample_size=64)
    >>> detector.scan_file(Path(__file__)) is None
    True
    """

    def __init__(
        self,
        plugins: Optional[Sequence[registry.FormatPlugin]] = None,
        *,
        sample_size: Optional[int] = None,
        max_total_sample_bytes: Optional[int] = None,
        sort_plugins: bool = True,
        on_error: Optional[Callable[[Path, Exception], None]] = None,
    ) -> None:
        """Create a detector.

        Args:
            plugins: Optional explicit plugin sequence. When omitted the
                registry provided defaults are used.
            sample_size: Optional number of bytes to read from each file for
                detection heuristics. ``None`` uses ``_DEFAULT_SAMPLE_SIZE``.
            max_total_sample_bytes: Optional aggregate sampling budget. When
                ``None`` the default guardrail of ``_DEFAULT_TOTAL_SAMPLE_BUDGET``
                (16 MiB) is applied. Use a larger value when scanning very large
                directories.
            sort_plugins: Controls whether plugins are re-sorted by priority.
                Disable this when you inject a custom ordered sequence and want
                registration order to win over priority values.
            on_error: Optional callback invoked with ``(path, exception)`` when
                file system access fails. The exception passed will be a
                :class:`DetectorIOError`.
        """
        selected_plugins = list(plugins) if plugins is not None else list(
            registry.get_plugins()
        )
        if sort_plugins:
            selected_plugins = sorted(
                selected_plugins,
                key=lambda plugin: plugin.priority,
            )
        self._plugins = list(selected_plugins)
        validated_sample = _validate_sample_size(
            _DEFAULT_SAMPLE_SIZE if sample_size is None else sample_size
        )
        self._sample_size = validated_sample
        self._max_total_sample_bytes = _validate_total_sample_budget(
            max_total_sample_bytes
        )
        if self._max_total_sample_bytes is None:
            self._max_total_sample_bytes = _DEFAULT_TOTAL_SAMPLE_BUDGET
        self._consumed_sample_bytes = 0
        self._budget_exhausted = False
        self._on_error = on_error

    def _handle_error(
        self,
        path: Path,
        error: DetectorIOError,
        *,
        cause: Optional[BaseException] = None,
    ) -> None:
        if self._on_error is not None:
            try:
                self._on_error(path, error)
            except Exception:  # pragma: no cover - defensive
                logger.exception("on_error handler raised during scan")
        if cause is not None:
            raise error from cause
        raise error

    def reset_sample_budget(self) -> None:
        """Reset aggregate sampling counters for a fresh scan."""

        self._consumed_sample_bytes = 0
        self._budget_exhausted = False

    @property
    def sample_budget_exhausted(self) -> bool:
        return self._budget_exhausted

    @property
    def sample_budget_remaining(self) -> Optional[int]:
        if self._max_total_sample_bytes is None:
            return None
        remaining = self._max_total_sample_bytes - self._consumed_sample_bytes
        return max(0, remaining)

    def scan_file(self, path: Path) -> Optional[DetectionMatch]:
        path = Path(path)
        if not path.is_file():
            raise FileNotFoundError(f"Expected file path, got: {path}")

        if (
            self._max_total_sample_bytes is not None
            and self._consumed_sample_bytes >= self._max_total_sample_bytes
        ):
            self._budget_exhausted = True
            logger.warning(
                "Sample budget exhausted before scanning: %s", path
            )
            return None

        try:
            with path.open("rb") as handle:
                read_size = self._sample_size + 1
                remaining = None
                if self._max_total_sample_bytes is not None:
                    remaining = (
                        self._max_total_sample_bytes - self._consumed_sample_bytes
                    )
                    read_size = min(read_size, remaining + 1)
                raw = handle.read(read_size)
        except OSError as exc:
            self._handle_error(path, _wrap_io_error(path, exc), cause=exc)
            return None
        if (
            self._max_total_sample_bytes is not None
            and remaining is not None
        ):
            sample = raw[: min(self._sample_size, remaining)]
        else:
            sample = raw[: self._sample_size]
        truncated = len(raw) > self._sample_size
        text: Optional[str] = None
        encoding: Optional[str] = None
        if registry.looks_text(sample):
            text, encoding = registry.decode_text(sample)

        if self._max_total_sample_bytes is not None:
            self._consumed_sample_bytes += len(sample)
            if self._consumed_sample_bytes >= self._max_total_sample_bytes:
                self._budget_exhausted = True

        for plugin in self._plugins:
            match = plugin.detect(path, sample, text)
            if match is not None:
                if match.metadata is not None:
                    metadata = dict(match.metadata)
                else:
                    metadata = {}
                if "bytes_sampled" not in metadata:
                    metadata["bytes_sampled"] = len(sample)
                if encoding is not None and "encoding" not in metadata:
                    metadata["encoding"] = encoding
                    reason = f"Decoded content using {encoding} encoding"
                    if reason not in match.reasons:
                        match.reasons.append(reason)
                if truncated:
                    metadata["sample_truncated"] = True
                    reason = f"Truncated sample to {self._sample_size}B"
                    if reason not in match.reasons:
                        match.reasons.append(reason)
                if (
                    self._max_total_sample_bytes is not None
                    and self._budget_exhausted
                ):
                    metadata["sample_budget_exhausted"] = True
                    reason = (
                        f"Sampling budget exhausted after {len(sample)}B"
                    )
                    if reason not in match.reasons:
                        match.reasons.append(reason)
                match.metadata = metadata or None
                match.metadata = validate_detection_metadata(
                    match,
                    DETECTION_CATALOG,
                )
                match.reasons = _normalise_reasons(match.reasons)
                return match
        return None

    def scan_path(
        self,
        root: Path,
        glob: str = "**/*",
        *,
        reset_budget: bool = True,
    ) -> List[tuple[Path, Optional[DetectionMatch]]]:
        """Scan ``root`` while enforcing the aggregate sampling budget.

        Args:
            reset_budget: When ``True`` the detector resets its aggregate
                sampling counter before scanning. Set to ``False`` to continue
                consuming the existing budget across multiple directory roots.
        """
        root = Path(root)
        try:
            if root.is_file():
                if reset_budget:
                    self.reset_sample_budget()
                return [(root, self.scan_file(root))]

            if not root.is_dir():
                raise FileNotFoundError(f"Path does not exist: {root}")
        except OSError as exc:
            self._handle_error(root, _wrap_io_error(root, exc), cause=exc)
            return []

        if reset_budget:
            self.reset_sample_budget()
        results: List[tuple[Path, Optional[DetectionMatch]]] = []
        try:
            iterable = sorted(root.glob(glob))
        except OSError as exc:
            self._handle_error(root, _wrap_io_error(root, exc), cause=exc)
            return results
        for path in iterable:
            try:
                if path.is_file():
                    results.append((path, self.scan_file(path)))
                    if (
                        self._max_total_sample_bytes is not None
                        and self._budget_exhausted
                    ):
                        logger.warning(
                            "Sample budget exhausted while scanning %s;"
                            " skipping remaining paths under %s",
                            path,
                            root,
                        )
                        break
            except OSError as exc:
                self._handle_error(path, _wrap_io_error(path, exc), cause=exc)
        return results

    def scan_with_profiles(
        self,
        root: Path,
        *,
        profile_store: ProfileStore,
        tags: Optional[Sequence[str]] = None,
        glob: str = "**/*",
    ) -> List[ProfiledDetection]:
        """Scan ``root`` and annotate matches with configuration profiles.

        Args:
            root: File or directory to scan.
            profile_store: Registry providing configuration profiles.
            tags: Tag collection used to select relevant profiles/configs.
            glob: Optional glob pattern when scanning directories.
        """

        if profile_store is None:
            raise ValueError("profile_store must be provided")

        normalized_tags = normalize_tags(tags)
        root_path = Path(root)
        scan_results = self.scan_path(root_path, glob=glob)
        profiled: List[ProfiledDetection] = []

        root_is_dir = root_path.is_dir()

        for path, detection in scan_results:
            relative: Optional[str]
            if root_is_dir:
                try:
                    relative = path.relative_to(root_path).as_posix()
                except ValueError:
                    relative = path.name
            else:
                relative = path.name

            applied = profile_store.matching_configs(
                normalized_tags,
                relative_path=relative,
            )
            # If any matching config sets metadata flag to ignore review, annotate detection
            if detection and detection.metadata:
                try:
                    ignore = any(
                        bool(getattr(cfg.config, "metadata", {}).get("ignore_review_flags"))
                        for cfg in applied
                    )
                except Exception:
                    ignore = False
                if ignore and detection.metadata.get("needs_review"):
                    detection.metadata["review_ignored"] = True
                    detection.metadata["needs_review"] = False
            profiled.append(
                ProfiledDetection(
                    path=path,
                    detection=detection,
                    profiles=applied,
                )
            )

        return profiled


def scan_file(
    path: Path,
    *,
    sample_size: Optional[int] = None,
    plugins: Optional[Sequence[registry.FormatPlugin]] = None,
    sort_plugins: bool = True,
    on_error: Optional[Callable[[Path, Exception], None]] = None,
) -> Optional[DetectionMatch]:
    """Convenience wrapper that uses the default detector instance."""

    detector = Detector(
        plugins=plugins,
        sample_size=sample_size,
        sort_plugins=sort_plugins,
        on_error=on_error,
    )
    return detector.scan_file(Path(path))


def scan_path(
    root: Path,
    glob: str = "**/*",
    *,
    sample_size: Optional[int] = None,
    plugins: Optional[Sequence[registry.FormatPlugin]] = None,
    sort_plugins: bool = True,
    on_error: Optional[Callable[[Path, Exception], None]] = None,
) -> List[tuple[Path, Optional[DetectionMatch]]]:
    """Convenience wrapper mirroring :meth:`Detector.scan_path`."""

    detector = Detector(
        plugins=plugins,
        sample_size=sample_size,
        sort_plugins=sort_plugins,
        on_error=on_error,
    )
    return detector.scan_path(Path(root), glob=glob)
