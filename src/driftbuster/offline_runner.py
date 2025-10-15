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
import importlib.resources as resources
import json
import os
import platform
import re
from hashlib import sha256
from pathlib import Path
import shutil
from typing import Any, Callable, Iterable, Mapping, MutableMapping, Sequence
import zipfile
from glob import glob


MANIFEST_SCHEMA = "https://driftbuster.dev/offline-runner/manifest/v1"
CONFIG_SCHEMA = "https://driftbuster.dev/offline-runner/config/v1"
SECRET_RULES_RESOURCE = "secret_rules.json"


@dataclass(frozen=True)
class SecretDetectionRule:
    name: str
    pattern: re.Pattern[str]
    description: str | None = None


@dataclass(frozen=True)
class SecretFinding:
    path: str
    rule: str
    line: int
    snippet: str


@dataclass
class SecretDetectionContext:
    rules: Sequence[SecretDetectionRule]
    version: str
    ignore_rules: frozenset[str]
    ignore_patterns: Sequence[re.Pattern[str]]
    ignore_pattern_text: Sequence[str]
    findings: list[SecretFinding]
    rules_loaded: bool


_SECRET_RULE_CACHE: tuple[SecretDetectionRule, ...] | None = None
_SECRET_RULE_VERSION: str | None = None


def _timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _compile_ruleset_from_mapping(
    payload: Mapping[str, Any] | None,
) -> tuple[tuple[SecretDetectionRule, ...], str] | None:
    if not payload:
        return None
    if not isinstance(payload, Mapping):
        return None

    rules_payload = payload.get("rules")
    if not isinstance(rules_payload, Sequence):
        return None

    compiled_rules: list[SecretDetectionRule] = []
    for entry in rules_payload:
        if not isinstance(entry, Mapping):
            continue
        name = str(entry.get("name") or "").strip()
        pattern_text = entry.get("pattern")
        if not name or not pattern_text:
            continue
        flags_text = str(entry.get("flags") or "").lower()
        flags = 0
        if "i" in flags_text:
            flags |= re.IGNORECASE
        try:
            pattern = re.compile(str(pattern_text), flags)
        except re.error:
            continue
        compiled_rules.append(
            SecretDetectionRule(
                name=name,
                pattern=pattern,
                description=str(entry.get("description")) if entry.get("description") else None,
            )
        )

    if not compiled_rules:
        return None

    version = str(payload.get("version", "embedded"))
    return tuple(compiled_rules), version


def _load_secret_rules() -> tuple[tuple[SecretDetectionRule, ...], str, bool]:
    global _SECRET_RULE_CACHE, _SECRET_RULE_VERSION
    if _SECRET_RULE_CACHE is not None and _SECRET_RULE_VERSION is not None:
        return _SECRET_RULE_CACHE, _SECRET_RULE_VERSION, True

    try:
        resource = resources.files(__package__).joinpath(SECRET_RULES_RESOURCE)
    except FileNotFoundError:
        _SECRET_RULE_CACHE = ()
        _SECRET_RULE_VERSION = "none"
        return _SECRET_RULE_CACHE, _SECRET_RULE_VERSION, False

    try:
        with resource.open("r", encoding="utf-8") as handle:
            payload = json.load(handle)
    except FileNotFoundError:
        _SECRET_RULE_CACHE = ()
        _SECRET_RULE_VERSION = "none"
        return _SECRET_RULE_CACHE, _SECRET_RULE_VERSION, False

    version = str(payload.get("version", "unknown"))
    compiled = _compile_ruleset_from_mapping(payload)
    if compiled is None:
        _SECRET_RULE_CACHE = ()
        _SECRET_RULE_VERSION = version
        return _SECRET_RULE_CACHE, _SECRET_RULE_VERSION, True

    rules, compiled_version = compiled
    _SECRET_RULE_CACHE = rules
    _SECRET_RULE_VERSION = compiled_version or version
    return _SECRET_RULE_CACHE, _SECRET_RULE_VERSION, True


def _secret_option_values(value: Any) -> tuple[str, ...]:
    if not value:
        return ()
    if isinstance(value, str):
        parts = re.split(r"[\s,;]+", value)
        return tuple(part.strip() for part in parts if part and part.strip())
    if isinstance(value, Sequence):
        result: list[str] = []
        for item in value:
            if item is None:
                continue
            text = str(item).strip()
            if text:
                result.append(text)
        return tuple(result)
    return ()


def _build_secret_context(
    options: Mapping[str, Any] | None,
    secret_scanner: Mapping[str, Any] | None,
) -> SecretDetectionContext:
    ruleset_payload = None
    if secret_scanner and isinstance(secret_scanner, Mapping):
        ruleset_payload = secret_scanner.get("ruleset")

    compiled_from_config = _compile_ruleset_from_mapping(
        ruleset_payload if isinstance(ruleset_payload, Mapping) else None
    )
    if compiled_from_config is not None:
        rules, version = compiled_from_config
        loaded = bool(rules)
    else:
        rules, version, loaded = _load_secret_rules()
    ignore_rule_values: set[str] = set()
    if options:
        ignore_rule_values.update(_secret_option_values(options.get("secret_ignore_rules")))
    if secret_scanner:
        ignore_rule_values.update(_secret_option_values(secret_scanner.get("ignore_rules")))

    pattern_sources: list[str] = []
    if options:
        pattern_sources.extend(_secret_option_values(options.get("secret_ignore_patterns")))
    if secret_scanner:
        pattern_sources.extend(_secret_option_values(secret_scanner.get("ignore_patterns")))

    ignore_pattern_text: list[str] = []
    ignore_patterns: list[re.Pattern[str]] = []
    seen_patterns: set[str] = set()
    for pattern_text in pattern_sources:
        if pattern_text in seen_patterns:
            continue
        seen_patterns.add(pattern_text)
        ignore_pattern_text.append(pattern_text)
        try:
            ignore_patterns.append(re.compile(pattern_text))
        except re.error:
            continue

    return SecretDetectionContext(
        rules=rules,
        version=version,
        ignore_rules=frozenset(ignore_rule_values),
        ignore_patterns=tuple(ignore_patterns),
        ignore_pattern_text=tuple(ignore_pattern_text),
        findings=[],
        rules_loaded=loaded and bool(rules),
    )


def _manifest_secret_scanner(
    options: Mapping[str, Any],
    secret_scanner: Mapping[str, Any],
    context: SecretDetectionContext,
) -> Mapping[str, Any]:
    ignore_rules: set[str] = set()
    ignore_rules.update(_secret_option_values(options.get("secret_ignore_rules")))
    ignore_rules.update(_secret_option_values(secret_scanner.get("ignore_rules")))

    ignore_patterns: set[str] = set()
    ignore_patterns.update(_secret_option_values(options.get("secret_ignore_patterns")))
    ignore_patterns.update(_secret_option_values(secret_scanner.get("ignore_patterns")))

    return {
        "ignore_rules": sorted(ignore_rules),
        "ignore_patterns": sorted(ignore_patterns),
        "ruleset_version": context.version,
    }


def _looks_binary(path: Path) -> bool:
    try:
        with path.open("rb") as handle:
            chunk = handle.read(1024)
    except OSError:
        return False
    return b"\0" in chunk


def _copy_with_secret_filter(
    source: Path,
    destination: Path,
    *,
    display_path: str,
    context: SecretDetectionContext,
    log: Callable[[str], None],
) -> tuple[int, str]:
    destination.parent.mkdir(parents=True, exist_ok=True)

    if not context.rules_loaded or not context.rules:
        shutil.copy2(source, destination)
        size = destination.stat().st_size
        return size, _hash_file(destination)

    if _looks_binary(source):
        shutil.copy2(source, destination)
        size = destination.stat().st_size
        return size, _hash_file(destination)

    buffered_lines: list[str] = []
    sanitized_lines: list[str] | None = None
    sanitized_matches = 0

    with source.open("r", encoding="utf-8", errors="replace") as input_handle:
        for lineno, line in enumerate(input_handle, 1):
            triggered_rule: SecretDetectionRule | None = None
            match_obj: re.Match[str] | None = None
            for rule in context.rules:
                if rule.name in context.ignore_rules:
                    continue
                match = rule.pattern.search(line)
                if not match:
                    continue
                if any(pattern.search(line) for pattern in context.ignore_patterns):
                    continue
                triggered_rule = rule
                match_obj = match
                break

            if triggered_rule is not None and match_obj is not None:
                if sanitized_lines is None:
                    sanitized_lines = list(buffered_lines)
                sanitized_matches += 1
                start, end = match_obj.span()
                masked = (line[:start] + "[SECRET]" + line[end:]).rstrip("\n\r")
                masked_preview = masked[:120]
                if len(masked) > 120:
                    masked_preview = masked[:117] + "..."
                context.findings.append(
                    SecretFinding(
                        path=display_path,
                        rule=triggered_rule.name,
                        line=lineno,
                        snippet=masked[:200],
                    )
                )
                log(
                    f"secret candidate removed ({triggered_rule.name}) from {display_path}:{lineno} -> {masked_preview}"
                )
                continue

            if sanitized_lines is not None:
                sanitized_lines.append(line)
            else:
                buffered_lines.append(line)

    if sanitized_lines is None:
        shutil.copy2(source, destination)
        size = destination.stat().st_size
        return size, _hash_file(destination)

    destination.write_text("".join(sanitized_lines), encoding="utf-8")
    try:
        shutil.copystat(source, destination, follow_symlinks=False)
    except OSError:
        pass

    log(
        f"scrubbed {sanitized_matches} potential secret line(s) from {display_path}"
    )

    size = destination.stat().st_size
    return size, _hash_file(destination)


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


def _path_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


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
    secret_scanner: Mapping[str, Any]

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

        secret_scanner_payload = payload.get("secret_scanner", {})
        if secret_scanner_payload and not isinstance(secret_scanner_payload, Mapping):
            raise ValueError("Profile 'secret_scanner' must be a mapping if provided.")
        secret_scanner = {str(key): secret_scanner_payload[key] for key in secret_scanner_payload} if isinstance(secret_scanner_payload, Mapping) else {}

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
            secret_scanner=secret_scanner,
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
    cleanup_staging: bool = True

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
            cleanup_staging=bool(payload.get("cleanup_staging", True)),
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
    staging_dir: Path | None
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

    secret_context = _build_secret_context(config.profile.options, config.profile.secret_scanner)
    if not secret_context.rules_loaded:
        log("secret detection rules unavailable; copying files without scrubbing")

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

        processed_directories: list[Path] = []

        for match in sorted(matches, key=lambda item: item.as_posix()):
            if match.is_symlink():
                log(f"skipping symlink: {match}")
                continue

            try:
                resolved_match = match.resolve()
            except FileNotFoundError:
                resolved_match = match

            if any(_path_within(resolved_match, processed) for processed in processed_directories):
                log(f"skipping already collected: {match}")
                continue

            if match.is_dir():
                processed_directories.append(resolved_match)
                walker = sorted(
                    (candidate for candidate in match.rglob("*") if candidate.is_file()),
                    key=lambda item: item.as_posix(),
                )
                for file in walker:
                    relative = _relative_path(match, file)
                    if _should_exclude(relative, source.exclude):
                        log(f"excluded {file} by pattern")
                        continue
                    original_size = file.stat().st_size
                    if (
                        settings.max_total_bytes is not None
                        and total_bytes + original_size > settings.max_total_bytes
                    ):
                        raise ValueError("Collection exceeds configured max_total_bytes limit.")
                    destination = destination_root / relative
                    relative_to_data = _relative_path(data_root, destination)
                    size, digest = _copy_with_secret_filter(
                        file,
                        destination,
                        display_path=relative_to_data.as_posix(),
                        context=secret_context,
                        log=log,
                    )
                    total_bytes += size
                    files.append(
                        CollectedFile(
                            alias=alias,
                            source=source.path,
                            destination=destination,
                            relative_path=relative_to_data,
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
                original_size = match.stat().st_size
                if (
                    settings.max_total_bytes is not None
                    and total_bytes + original_size > settings.max_total_bytes
                ):
                    raise ValueError("Collection exceeds configured max_total_bytes limit.")
                destination = destination_root / relative
                relative_to_data = _relative_path(data_root, destination)
                size, digest = _copy_with_secret_filter(
                    match,
                    destination,
                    display_path=relative_to_data.as_posix(),
                    context=secret_context,
                    log=log,
                )
                total_bytes += size
                files.append(
                    CollectedFile(
                        alias=alias,
                        source=source.path,
                        destination=destination,
                        relative_path=relative_to_data,
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
                "secret_scanner": _manifest_secret_scanner(
                    config.profile.options,
                    config.profile.secret_scanner,
                    secret_context,
                ),
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
            "secrets": {
                "ruleset_version": secret_context.version,
                "findings": [
                    {
                        "path": finding.path,
                        "rule": finding.rule,
                        "line": finding.line,
                        "snippet": finding.snippet,
                    }
                    for finding in secret_context.findings
                ],
                "ignored_rules": sorted(secret_context.ignore_rules),
                "ignored_patterns": list(secret_context.ignore_pattern_text),
            },
            "metadata": dict(config.metadata),
            "package": {
                "staging_directory": str(staging_dir),
                "data_directory": str(data_root),
                "logs_directory": str(logs_root),
                "compressed": settings.compress,
                "cleanup_staging": settings.cleanup_staging,
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
    staging_dir_on_disk: Path | None = staging_dir
    manifest_on_disk = manifest_path
    log_on_disk = log_path
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

        if settings.cleanup_staging:
            shutil.rmtree(staging_dir, ignore_errors=True)
            staging_dir_on_disk = None
            manifest_on_disk = None
            log_on_disk = None

    return OfflineRunnerResult(
        config=config,
        staging_dir=staging_dir_on_disk,
        manifest_path=manifest_on_disk,
        log_path=log_on_disk,
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
