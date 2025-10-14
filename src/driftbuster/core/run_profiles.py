from __future__ import annotations

import json
import os
import shutil
from dataclasses import dataclass, field
from datetime import datetime, timezone
from glob import glob
from hashlib import sha256
from pathlib import Path
from typing import Any, Iterable, List, Mapping, MutableMapping, Sequence


def _timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _expand_path(text: str) -> str:
    return os.path.expandvars(os.path.expanduser(text))


def _has_magic(pattern: str) -> bool:
    special = set("*?[]")
    return any(char in pattern for char in special)


def _safe_name(text: str) -> str:
    return "".join(
        char if char.isalnum() or char in ("-", "_") else "-"
        for char in text
    )


@dataclass
class RunProfile:
    name: str
    description: str | None = None
    sources: Sequence[str] = field(default_factory=tuple)
    baseline: str | None = None
    options: Mapping[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "RunProfile":
        return cls(
            name=str(payload["name"]),
            description=payload.get("description"),
            sources=tuple(payload.get("sources", ())),
            baseline=payload.get("baseline"),
            options=payload.get("options", {}),
        )

    def to_dict(self) -> Mapping[str, Any]:
        return {
            "name": self.name,
            "description": self.description,
            "sources": list(self.sources),
            "baseline": self.baseline,
            "options": dict(self.options),
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

    def to_dict(self) -> Mapping[str, Any]:
        return {
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
                            )
                        )
            elif match.is_file():
                files.append(
                    _copy_file(
                        source=source,
                        file=match,
                        base=match.parent,
                        destination_root=destination_root,
                    )
                )

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
) -> ProfileFile:
    if file.is_relative_to(base):
        relative = file.relative_to(base)
    else:
        relative = file.name
    destination = destination_root / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(file, destination)

    digest = sha256()
    with destination.open("rb") as handle:
        for chunk in iter(lambda: handle.read(64 * 1024), b""):
            digest.update(chunk)

    return ProfileFile(
        source=source,
        destination=destination,
        size=destination.stat().st_size,
        sha256=digest.hexdigest(),
    )
