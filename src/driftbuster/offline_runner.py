from __future__ import annotations

"""Offline collection runner helpers used by the portable PowerShell tool.

The portable runner is designed for air-gapped or disconnected
environments where the GUI cannot execute collections locally.  The
PowerShell implementation mirrors the behaviour that is exercised in the
unit tests below – this module exists so we can validate config parsing
and collection semantics without requiring PowerShell inside our CI.

The public entry points exported here intentionally match what the
PowerShell script expects:

```
>>> from driftbuster import offline_runner
>>> config = offline_runner.load_config(path_to_config)
>>> result = offline_runner.execute_config(config, config_path=path_to_config)
```

The resulting package directory contains a manifest, runner logs, the
original configuration file, and the collected payload.  The manifest
structure is shared with the PowerShell runner so the two paths remain
consistent.
"""

from dataclasses import dataclass, field
from datetime import datetime, timezone
import fnmatch
import getpass
import json
import os
import platform
from hashlib import sha256
from pathlib import Path
import shutil
from typing import Any, Iterable, Mapping, MutableMapping, Sequence
import zipfile
from glob import glob


MANIFEST_SCHEMA = "https://driftbuster.dev/offline-runner/manifest/v1"
CONFIG_SCHEMA = "https://driftbuster.dev/offline-runner/config/v1"


def _timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _expand_path(text: str) -> Path:
    expanded = os.path.expanduser(os.path.expandvars(text))
    return Path(expanded)


def _has_magic(pattern: str) -> bool:
    return any(char in pattern for char in "*?[]")


def _safe_name(text: str) -> str:
    sanitized = [
        char if char.isalnum() or char in {"-", "_"} else "-"
        for char in text
    ]
    safe = "".join(sanitized)
    return safe or "data"


def _relative_path(base: Path, target: Path) -> Path:
    try:
        return target.relative_to(base)
    except ValueError:
        return Path(target.name)


@dataclass(frozen=True)
class OfflineCollectionSource:
    path: str
    alias: str | None = None
    optional: bool = False
    exclude: Sequence[str] = field(default_factory=tuple)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "OfflineCollectionSource":
        path = payload.get("path")
        if not path or not str(path).strip():
            raise ValueError("Source entry requires a non-empty 'path'.")

        alias = payload.get("alias")
        if alias is not None and not str(alias).strip():
            alias = None

        optional = bool(payload.get("optional", False))
        exclude_payload = payload.get("exclude", ())
        if isinstance(exclude_payload, str):
            exclude = (exclude_payload,)
        else:
            exclude = tuple(str(entry) for entry in exclude_payload or ())

        return cls(path=str(path), alias=str(alias) if alias else None, optional=optional, exclude=exclude)

    def destination_name(self, *, fallback_index: int) -> str:
        if self.alias:
            return _safe_name(self.alias)
        expanded = _expand_path(self.path)
        name = expanded.name or expanded.stem
        if name:
            return _safe_name(name)
        return f"source_{fallback_index:02d}"


@dataclass(frozen=True)
class OfflineRunnerProfile:
    name: str
    description: str | None
    sources: Sequence[OfflineCollectionSource]
    baseline: str | None
    tags: Sequence[str]
    options: Mapping[str, Any]

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "OfflineRunnerProfile":
        name = payload.get("name")
        if not name or not str(name).strip():
            raise ValueError("Profile requires a non-empty 'name'.")

        raw_sources = payload.get("sources", ())
        if not raw_sources:
            raise ValueError("Profile must define at least one source.")

        sources: list[OfflineCollectionSource] = []
        for entry in raw_sources:
            if isinstance(entry, Mapping):
                sources.append(OfflineCollectionSource.from_dict(entry))
            else:
                sources.append(
                    OfflineCollectionSource.from_dict({"path": str(entry)})
                )

        baseline = payload.get("baseline")
        if baseline is not None:
            baseline = str(baseline)
            if baseline not in {source.path for source in sources}:
                raise ValueError(
                    "Profile baseline must reference one of the declared sources."
                )

        tags_payload = payload.get("tags", ())
        if isinstance(tags_payload, str):
            tags = (tags_payload,)
        else:
            tags = tuple(str(tag) for tag in tags_payload or ())

        options_payload = payload.get("options", {})
        if not isinstance(options_payload, Mapping):
            raise ValueError("Profile 'options' must be a mapping if provided.")
        options = {str(key): options_payload[key] for key in options_payload}

        description = payload.get("description")
        if description is not None:
            description = str(description)

        return cls(
            name=str(name),
            description=description,
            sources=tuple(sources),
            baseline=baseline,
            tags=tags,
            options=options,
        )


@dataclass(frozen=True)
class OfflineRunnerSettings:
    output_directory: Path | None = None
    package_name: str | None = None
    compress: bool = True
    include_config: bool = True
    include_logs: bool = True
    include_manifest: bool = True
    manifest_name: str = "manifest.json"
    log_name: str = "runner.log"
    data_directory_name: str = "data"
    logs_directory_name: str = "logs"
    max_total_bytes: int | None = None

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any] | None) -> "OfflineRunnerSettings":
        if not payload:
            return cls()

        directory = payload.get("output_directory")
        output_directory = Path(os.path.expandvars(directory)).expanduser() if directory else None

        package_name = payload.get("package_name")
        if package_name is not None and not str(package_name).strip():
            package_name = None

        manifest_name = payload.get("manifest_name", "manifest.json")
        log_name = payload.get("log_name", "runner.log")
        data_directory_name = payload.get("data_directory_name", "data")
        logs_directory_name = payload.get("logs_directory_name", "logs")

        max_total_bytes = payload.get("max_total_bytes")
        if max_total_bytes is not None:
            max_total_bytes = int(max_total_bytes)
            if max_total_bytes <= 0:
                raise ValueError("max_total_bytes must be positive if provided.")

        return cls(
            output_directory=output_directory,
            package_name=str(package_name) if package_name else None,
            compress=bool(payload.get("compress", True)),
            include_config=bool(payload.get("include_config", True)),
            include_logs=bool(payload.get("include_logs", True)),
            include_manifest=bool(payload.get("include_manifest", True)),
            manifest_name=str(manifest_name),
            log_name=str(log_name),
            data_directory_name=str(data_directory_name),
            logs_directory_name=str(logs_directory_name),
            max_total_bytes=max_total_bytes,
        )


@dataclass(frozen=True)
class OfflineRunnerConfig:
    profile: OfflineRunnerProfile
    settings: OfflineRunnerSettings
    metadata: Mapping[str, Any]
    schema: str
    version: str
    raw: Mapping[str, Any]

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "OfflineRunnerConfig":
        if not isinstance(payload, Mapping):
            raise TypeError("Config payload must be a mapping.")

        schema = str(payload.get("schema", CONFIG_SCHEMA))
        version = str(payload.get("version", "1"))
        profile_payload = payload.get("profile")
        if not isinstance(profile_payload, Mapping):
            raise ValueError("Config requires a 'profile' object.")

        settings_payload = payload.get("runner") or payload.get("settings")
        metadata_payload = payload.get("metadata", {})
        if metadata_payload and not isinstance(metadata_payload, Mapping):
            raise ValueError("Metadata must be a mapping if provided.")

        profile = OfflineRunnerProfile.from_dict(profile_payload)
        settings = OfflineRunnerSettings.from_dict(settings_payload)

        return cls(
            profile=profile,
            settings=settings,
            metadata=dict(metadata_payload),
            schema=schema,
            version=version,
            raw=dict(payload),
        )

    def default_package_name(self, *, timestamp: str | None = None) -> str:
        stamp = timestamp or _timestamp()
        return f"{_safe_name(self.profile.name)}-{stamp}"


@dataclass(frozen=True)
class CollectedFile:
    alias: str
    source: str
    destination: Path
    relative_path: Path
    size: int
    sha256: str


@dataclass(frozen=True)
class OfflineRunnerResult:
    config: OfflineRunnerConfig
    staging_dir: Path
    manifest_path: Path | None
    log_path: Path | None
    package_path: Path | None
    files: Sequence[CollectedFile]
    timestamp: str


def load_config(path: Path | str) -> OfflineRunnerConfig:
    path = Path(path)
    payload = json.loads(path.read_text(encoding="utf-8"))
    return OfflineRunnerConfig.from_dict(payload)


def _write_log(path: Path, entries: Sequence[str]) -> None:
    path.write_text("\n".join(entries) + ("\n" if entries else ""), encoding="utf-8")


def _hash_file(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _should_exclude(relative: Path, patterns: Sequence[str]) -> bool:
    if not patterns:
        return False
    text = relative.as_posix()
    for pattern in patterns:
        if fnmatch.fnmatch(text, pattern) or fnmatch.fnmatch(relative.name, pattern):
            return True
    return False


def _iter_source_matches(path_text: str) -> Iterable[Path]:
    candidate = _expand_path(path_text)
    if candidate.exists():
        yield candidate
        return

    if _has_magic(path_text):
        expanded_pattern = os.path.expanduser(os.path.expandvars(path_text))
        for match in sorted(glob(expanded_pattern, recursive=True)):
            yield Path(match)
        return

    raise FileNotFoundError(f"Path does not exist: {path_text}")


def execute_config(
    config: OfflineRunnerConfig,
    *,
    config_path: Path | None = None,
    base_dir: Path | None = None,
    timestamp: str | None = None,
) -> OfflineRunnerResult:
    run_timestamp = timestamp or _timestamp()
    settings = config.settings

    output_root = settings.output_directory or base_dir or Path.cwd()
    output_root = Path(output_root)
    output_root.mkdir(parents=True, exist_ok=True)

    staging_dir = output_root / f"{_safe_name(config.profile.name)}-{run_timestamp}"
    data_root = staging_dir / settings.data_directory_name
    logs_root = staging_dir / settings.logs_directory_name
    data_root.mkdir(parents=True, exist_ok=True)
    logs_root.mkdir(parents=True, exist_ok=True)

    log_entries: list[str] = []

    def log(message: str) -> None:
        stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        log_entries.append(f"[{stamp}] {message}")

    log("offline collection started")

    files: list[CollectedFile] = []
    total_bytes = 0
    source_summaries: list[MutableMapping[str, Any]] = []

    for index, source in enumerate(config.profile.sources):
        alias = source.destination_name(fallback_index=index)
        destination_root = data_root / alias
        destination_root.mkdir(parents=True, exist_ok=True)

        matches: list[Path] = []
        try:
            for candidate in _iter_source_matches(source.path):
                matches.append(candidate)
        except FileNotFoundError:
            if source.optional:
                log(f"optional source skipped: {source.path}")
                source_summaries.append(
                    {
                        "path": source.path,
                        "alias": alias,
                        "optional": True,
                        "matched": [],
                        "skipped": True,
                        "reason": "missing",
                        "exclude": list(source.exclude),
                    }
                )
                continue
            log(f"required source missing: {source.path}")
            raise

        collected: list[str] = []
        if not matches:
            if source.optional:
                log(f"optional source skipped: {source.path}")
                source_summaries.append(
                    {
                        "path": source.path,
                        "alias": alias,
                        "optional": True,
                        "matched": [],
                        "skipped": True,
                        "reason": "no-matches",
                        "exclude": list(source.exclude),
                    }
                )
                continue
            raise FileNotFoundError(f"Path does not exist: {source.path}")

        for match in sorted(matches, key=lambda item: item.as_posix()):
            if match.is_symlink():
                log(f"skipping symlink: {match}")
                continue

            if match.is_dir():
                walker = sorted(
                    (candidate for candidate in match.rglob("*") if candidate.is_file()),
                    key=lambda item: item.as_posix(),
                )
                for file in walker:
                    relative = _relative_path(match, file)
                    if _should_exclude(relative, source.exclude):
                        log(f"excluded {file} by pattern")
                        continue
                    size = file.stat().st_size
                    if settings.max_total_bytes is not None and total_bytes + size > settings.max_total_bytes:
                        raise ValueError("Collection exceeds configured max_total_bytes limit.")
                    destination = destination_root / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file, destination)
                    digest = _hash_file(destination)
                    total_bytes += size
                    files.append(
                        CollectedFile(
                            alias=alias,
                            source=source.path,
                            destination=destination,
                            relative_path=_relative_path(data_root, destination),
                            size=size,
                            sha256=digest,
                        )
                    )
                    collected.append(relative.as_posix())
            elif match.is_file():
                relative = Path(match.name)
                if _should_exclude(relative, source.exclude):
                    log(f"excluded {match} by pattern")
                    continue
                size = match.stat().st_size
                if settings.max_total_bytes is not None and total_bytes + size > settings.max_total_bytes:
                    raise ValueError("Collection exceeds configured max_total_bytes limit.")
                destination = destination_root / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(match, destination)
                digest = _hash_file(destination)
                total_bytes += size
                files.append(
                    CollectedFile(
                        alias=alias,
                        source=source.path,
                        destination=destination,
                        relative_path=_relative_path(data_root, destination),
                        size=size,
                        sha256=digest,
                    )
                )
                collected.append(relative.as_posix())

        source_summaries.append(
            {
                "path": source.path,
                "alias": alias,
                "optional": source.optional,
                "matched": collected,
                "skipped": False,
                "exclude": list(source.exclude),
            }
        )
        log(f"collected {len(collected)} items from {source.path}")

    log("offline collection finished")

    log_path: Path | None = None
    if settings.include_logs:
        logs_root.mkdir(parents=True, exist_ok=True)
        log_path = logs_root / settings.log_name
        _write_log(log_path, log_entries)

    manifest_path: Path | None = None
    if settings.include_manifest:
        manifest_payload: MutableMapping[str, Any] = {
            "schema": MANIFEST_SCHEMA,
            "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "timestamp": run_timestamp,
            "host": {
                "computer_name": platform.node(),
                "user": getpass.getuser(),
                "platform": platform.platform(),
            },
            "profile": {
                "name": config.profile.name,
                "description": config.profile.description,
                "baseline": config.profile.baseline,
                "tags": list(config.profile.tags),
                "options": dict(config.profile.options),
            },
            "runner": {
                "version": config.version,
                "schema": config.schema,
            },
            "sources": source_summaries,
            "files": [
                {
                    "alias": entry.alias,
                    "source": entry.source,
                    "relative_path": entry.relative_path.as_posix(),
                    "size": entry.size,
                    "sha256": entry.sha256,
                }
                for entry in files
            ],
            "metadata": dict(config.metadata),
            "package": {
                "staging_directory": str(staging_dir),
                "data_directory": str(data_root),
                "logs_directory": str(logs_root),
                "compressed": settings.compress,
            },
        }

        if config_path and config_path.exists():
            manifest_payload["config"] = {
                "path": str(config_path),
                "sha256": _hash_file(config_path),
            }

        manifest_path = staging_dir / settings.manifest_name
        manifest_path.write_text(
            json.dumps(manifest_payload, indent=2, sort_keys=True),
            encoding="utf-8",
        )

    if settings.include_config and config_path and config_path.exists():
        shutil.copy2(config_path, staging_dir / Path(config_path.name))

    package_path: Path | None = None
    if settings.compress:
        package_name = settings.package_name or f"{_safe_name(config.profile.name)}-{run_timestamp}.zip"
        if not package_name.lower().endswith(".zip"):
            package_name = f"{package_name}.zip"
        package_path = output_root / package_name
        with zipfile.ZipFile(package_path, mode="w", compression=zipfile.ZIP_DEFLATED) as zf:
            for path in sorted(staging_dir.rglob("*")):
                if path.is_dir():
                    continue
                arcname = path.relative_to(staging_dir).as_posix()
                zf.write(path, arcname)

    return OfflineRunnerResult(
        config=config,
        staging_dir=staging_dir,
        manifest_path=manifest_path,
        log_path=log_path,
        package_path=package_path,
        files=tuple(files),
        timestamp=run_timestamp,
    )


def execute_config_path(
    config_path: Path | str,
    *,
    base_dir: Path | None = None,
    timestamp: str | None = None,
) -> OfflineRunnerResult:
    config_path = Path(config_path)
    config = load_config(config_path)
    return execute_config(config, config_path=config_path, base_dir=base_dir, timestamp=timestamp)


__all__ = [
    "CONFIG_SCHEMA",
    "MANIFEST_SCHEMA",
    "CollectedFile",
    "OfflineCollectionSource",
    "OfflineRunnerConfig",
    "OfflineRunnerProfile",
    "OfflineRunnerResult",
    "OfflineRunnerSettings",
    "execute_config",
    "execute_config_path",
    "load_config",
]
