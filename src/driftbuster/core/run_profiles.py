from __future__ import annotations

import json
import os
import shutil
from dataclasses import dataclass, field
from datetime import datetime, timezone
from glob import glob
from hashlib import sha256
from pathlib import Path
from types import MappingProxyType
from typing import Any, Callable, Iterable, List, Mapping, MutableMapping, Sequence

from .. import secret_scanning


def _timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _expand_path(text: str) -> str:
    expanded = os.path.expandvars(os.path.expanduser(text))
    return os.path.abspath(expanded)


def _has_magic(pattern: str) -> bool:
    special = set("*?[]")
    return any(char in pattern for char in special)


def _safe_name(text: str) -> str:
    return "".join(
        char if char.isalnum() or char in ("-", "_") else "-"
        for char in text
    )


def _glob_base_directory(pattern: str) -> Path:
    expanded = Path(pattern)
    parts = expanded.parts
    base_parts: List[str] = []

    for part in parts:
        if _has_magic(part):
            break
        base_parts.append(part)

    if not base_parts:
        return Path.cwd()

    base = Path(*base_parts)
    if not base.is_absolute():
        base = Path.cwd() / base
    return base


def _normalise_options(options: Mapping[str, Any] | None) -> Mapping[str, str]:
    if not options:
        return {}
    return {str(key): "" if value is None else str(value) for key, value in options.items()}


def _normalise_secret_scanner(
    secret_scanner: Mapping[str, Any] | None,
) -> Mapping[str, Any]:
    if not secret_scanner:
        return MappingProxyType({})

    normalised: dict[str, Any] = {}
    for key, value in secret_scanner.items():
        key_text = str(key)
        if key_text in {"ignore_rules", "ignore_patterns"}:
            normalised[key_text] = tuple(
                secret_scanning.secret_option_values(value)
            )
        elif key_text == "ruleset" and isinstance(value, Mapping):
            normalised[key_text] = dict(value)
        else:
            normalised[key_text] = value
    return MappingProxyType(normalised)


def _serialise_secret_scanner(secret_scanner: Mapping[str, Any]) -> Mapping[str, Any]:
    if not secret_scanner:
        return {}

    payload: dict[str, Any] = {}
    for key, value in secret_scanner.items():
        if key in {"ignore_rules", "ignore_patterns"}:
            payload[key] = list(value)
        else:
            payload[key] = value
    return payload


def _validate_profile(profile: "RunProfile") -> None:
    if not profile.sources:
        raise ValueError("At least one source must be provided.")

    for source in profile.sources:
        if not source or not str(source).strip():
            raise ValueError("Source paths must not be empty.")

        expanded = _expand_path(source)
        candidate = Path(expanded)

        if _has_magic(expanded):
            base_dir = _glob_base_directory(expanded)
            if not base_dir.exists():
                raise FileNotFoundError(f"Glob base directory not found: {base_dir}")
        else:
            if not candidate.exists():
                raise FileNotFoundError(f"Path does not exist: {expanded}")

    if profile.baseline:
        if profile.baseline not in profile.sources:
            raise ValueError("Baseline must be one of the sources.")

        expanded_baseline = _expand_path(profile.baseline)
        baseline_path = Path(expanded_baseline)
        if _has_magic(expanded_baseline):
            base_dir = _glob_base_directory(expanded_baseline)
            if not base_dir.exists():
                raise FileNotFoundError(f"Glob base directory not found: {base_dir}")  # pragma: no cover - validated via sources loop
        elif not baseline_path.exists():
            # The sources loop above already validates each source path, including the baseline
            # string if present in sources, so this branch is redundant in practice.
            raise FileNotFoundError(
                f"Baseline path does not exist: {expanded_baseline}"
            )  # pragma: no cover - validated via sources loop


@dataclass
class RunProfile:
    name: str
    description: str | None = None
    sources: Sequence[str] = field(default_factory=tuple)
    baseline: str | None = None
    options: Mapping[str, str] = field(default_factory=dict)
    secret_scanner: Mapping[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        object.__setattr__(self, "name", str(self.name))
        if self.description is not None:
            object.__setattr__(self, "description", str(self.description))
        if self.baseline is not None:
            object.__setattr__(self, "baseline", str(self.baseline))

        object.__setattr__(self, "sources", tuple(str(entry) for entry in self.sources))
        object.__setattr__(self, "options", dict(_normalise_options(self.options)))
        object.__setattr__(self, "secret_scanner", _normalise_secret_scanner(self.secret_scanner))

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "RunProfile":
        return cls(
            name=str(payload["name"]),
            description=payload.get("description"),
            sources=tuple(payload.get("sources", ())),
            baseline=payload.get("baseline"),
            options=_normalise_options(payload.get("options", {})),
            secret_scanner=payload.get("secret_scanner", {}),
        )

    def to_dict(self) -> Mapping[str, Any]:
        return {
            "name": self.name,
            "description": self.description,
            "sources": list(self.sources),
            "baseline": self.baseline,
            "options": dict(self.options),
            "secret_scanner": _serialise_secret_scanner(self.secret_scanner),
        }


@dataclass
class ProfileFile:
    source: str
    destination: Path
    size: int
    sha256: str


@dataclass
class ProfileRunResult:
    profile: RunProfile
    timestamp: str
    output_dir: Path
    files: Sequence[ProfileFile]
    secrets: Mapping[str, Any] | None = None

    def to_dict(self) -> Mapping[str, Any]:
        payload = {
            "profile": self.profile.to_dict(),
            "timestamp": self.timestamp,
            "output_dir": str(self.output_dir),
            "files": [
                {
                    "source": entry.source,
                    "destination": entry.destination.as_posix(),
                    "size": entry.size,
                    "sha256": entry.sha256,
                }
                for entry in self.files
            ],
        }
        if self.secrets is not None:
            payload["secrets"] = self.secrets
        return payload


def profiles_root(base_dir: Path | None = None) -> Path:
    root = Path(base_dir or Path.cwd()) / "Profiles"
    root.mkdir(parents=True, exist_ok=True)
    return root


def profile_directory(profile_name: str, base_dir: Path | None = None) -> Path:
    return profiles_root(base_dir) / _safe_name(profile_name)


def load_profile(
    profile_name: str,
    *,
    base_dir: Path | None = None,
) -> RunProfile:
    profile_dir = profile_directory(profile_name, base_dir=base_dir)
    config_path = profile_dir / "profile.json"
    if not config_path.exists():
        raise FileNotFoundError(f"Profile not found: {profile_name}")
    payload = json.loads(config_path.read_text(encoding="utf-8"))
    return RunProfile.from_dict(payload)


def save_profile(profile: RunProfile, *, base_dir: Path | None = None) -> Path:
    _validate_profile(profile)

    profile_dir = profile_directory(profile.name, base_dir=base_dir)
    profile_dir.mkdir(parents=True, exist_ok=True)
    config_path = profile_dir / "profile.json"
    config_path.write_text(
        json.dumps(profile.to_dict(), indent=2, sort_keys=True),
        encoding="utf-8",
    )
    return profile_dir


def list_profiles(*, base_dir: Path | None = None) -> Sequence[RunProfile]:
    root = profiles_root(base_dir)
    profiles: List[RunProfile] = []
    for entry in sorted(root.glob("*/profile.json")):
        payload = json.loads(entry.read_text(encoding="utf-8"))
        profiles.append(RunProfile.from_dict(payload))
    return profiles


def execute_profile(
    profile: RunProfile,
    *,
    base_dir: Path | None = None,
    timestamp: str | None = None,
) -> ProfileRunResult:
    profile_dir = save_profile(profile, base_dir=base_dir)
    run_timestamp = timestamp or _timestamp()
    run_root = profile_dir / "raw" / run_timestamp
    run_root.mkdir(parents=True, exist_ok=True)

    secret_context = secret_scanning.build_context(profile.options, profile.secret_scanner)
    secret_logs: list[str] = []

    def _secret_log(message: str) -> None:
        secret_logs.append(message)

    files: List[ProfileFile] = []

    source_strings = list(profile.sources)
    if profile.baseline and profile.baseline in source_strings:
        source_strings.remove(profile.baseline)
        source_strings.insert(0, profile.baseline)

    for index, source in enumerate(source_strings):
        expanded = _expand_path(source)
        source_label = f"source_{index:02d}"
        destination_root = run_root / source_label
        destination_root.mkdir(parents=True, exist_ok=True)

        matches = _collect_matches(expanded)
        for match in matches:
            if match.is_dir():
                for file in match.rglob("*"):
                    if file.is_file():
                        files.append(
                            _copy_file(
                                source=source,
                                file=file,
                                base=match,
                                destination_root=destination_root,
                                secret_context=secret_context,
                                secret_log=_secret_log,
                            )
                        )
            elif match.is_file():
                files.append(
                    _copy_file(
                        source=source,
                        file=match,
                        base=match.parent,
                        destination_root=destination_root,
                        secret_context=secret_context,
                        secret_log=_secret_log,
                    )
                )

    secret_metadata = {
        "ruleset_version": secret_context.version,
        "rules_loaded": bool(secret_context.rules) and secret_context.rules_loaded,
        "ignored_rules": sorted(secret_context.ignore_rules),
        "ignored_patterns": list(secret_context.ignore_pattern_text),
        "findings": [
            {
                "path": finding.path,
                "rule": finding.rule,
                "line": finding.line,
                "snippet": finding.snippet,
            }
            for finding in secret_context.findings
        ],
    }
    if secret_logs:
        secret_metadata["messages"] = list(secret_logs)

    metadata_path = run_root / "metadata.json"
    metadata_path.write_text(
        json.dumps(
            {
                "profile": profile.to_dict(),
                "timestamp": run_timestamp,
                "baseline": (
                    profile.baseline or source_strings[0]
                    if source_strings
                    else None
                ),
                "files": [
                    {
                        "source": entry.source,
                        "destination": entry.destination.as_posix(),
                        "size": entry.size,
                        "sha256": entry.sha256,
                    }
                    for entry in files
                ],
                "secrets": secret_metadata,
            },
            indent=2,
            sort_keys=True,
        ),
        encoding="utf-8",
    )

    return ProfileRunResult(
        profile=profile,
        timestamp=run_timestamp,
        output_dir=run_root,
        files=files,
        secrets=secret_metadata,
    )


def _collect_matches(path_text: str) -> Sequence[Path]:
    candidate = Path(path_text)
    if candidate.exists():
        return [candidate]

    if _has_magic(path_text):
        return [Path(match) for match in glob(path_text, recursive=True)]

    raise FileNotFoundError(f"Path does not exist: {path_text}")


def _copy_file(
    *,
    source: str,
    file: Path,
    base: Path,
    destination_root: Path,
    secret_context: secret_scanning.SecretDetectionContext | None = None,
    secret_log: Callable[[str], None] | None = None,
) -> ProfileFile:
    if file.is_relative_to(base):
        relative = file.relative_to(base)
    else:
        relative = Path(file.name)
    destination = destination_root / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    if secret_context is not None and secret_log is not None:
        size, digest_hex = secret_scanning.copy_with_secret_filter(
            file,
            destination,
            display_path=relative.as_posix(),
            context=secret_context,
            log=secret_log,
        )
    else:
        shutil.copy2(file, destination)
        size = destination.stat().st_size
        digest = sha256()
        with destination.open("rb") as handle:
            for chunk in iter(lambda: handle.read(64 * 1024), b""):
                digest.update(chunk)
        digest_hex = digest.hexdigest()

    return ProfileFile(
        source=source,
        destination=destination,
        size=size,
        sha256=digest_hex,
    )
