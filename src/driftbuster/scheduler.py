from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import datetime, time, timedelta, timezone
from types import MappingProxyType
from typing import Callable, Iterable, Mapping, MutableMapping, Sequence

try:  # pragma: no cover - optional dependency on Windows
    from zoneinfo import ZoneInfo
except ImportError:  # pragma: no cover - Python <3.9 fallback
    ZoneInfo = None  # type: ignore

from .core.run_profiles import RunProfile

ProfileLoader = Callable[[str], RunProfile]

_INTERVAL_RE = re.compile(r"(?P<value>\d+(?:\.\d+)?)(?P<unit>[smhd])")


class ScheduleError(ValueError):
    """Raised when schedule inputs are invalid."""


def _ensure_aware(moment: datetime) -> datetime:
    if moment.tzinfo is None:
        return moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(timezone.utc)


def _parse_timestamp(value: object) -> datetime:
    try:
        text = str(value)
    except Exception as exc:  # pragma: no cover - defensive guard
        raise ScheduleError(f"Invalid timestamp payload: {value!r}") from exc
    if not text:
        raise ScheduleError("Timestamp payload must not be empty.")
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as exc:  # pragma: no cover - defensive guard
        raise ScheduleError(f"Unable to parse timestamp: {value!r}") from exc
    return _ensure_aware(parsed)


def parse_interval(value: float | int | str | timedelta) -> timedelta:
    """Normalise the scheduling interval to a ``timedelta``.

    Strings accept compact tokens such as ``"15m"``, ``"1h"``,
    ``"1h30m"``, or ISO-8601 duration snippets like ``"PT45M"``.
    Numeric inputs are treated as seconds.
    """

    if isinstance(value, timedelta):
        if value.total_seconds() <= 0:
            raise ScheduleError("Interval must be positive.")
        return value

    if isinstance(value, (int, float)):
        seconds = float(value)
        if seconds <= 0:
            raise ScheduleError("Interval must be positive.")
        return timedelta(seconds=seconds)

    text = str(value).strip().lower()
    if not text:
        raise ScheduleError("Interval text must not be empty.")

    if text.startswith("pt"):
        # Basic ISO-8601 duration parsing (PT#H#M#S)
        hours = minutes = seconds = 0.0
        iso = text[2:]
        match = re.fullmatch(r"(?:(?P<h>\d+(?:\.\d+)?)h)?(?:(?P<m>\d+(?:\.\d+)?)m)?(?:(?P<s>\d+(?:\.\d+)?)s)?", iso)
        if not match or not match.group(0):
            raise ScheduleError(f"Unsupported ISO-8601 interval: {value!r}")
        if match.group("h"):
            hours = float(match.group("h"))
        if match.group("m"):
            minutes = float(match.group("m"))
        if match.group("s"):
            seconds = float(match.group("s"))
        duration = timedelta(hours=hours, minutes=minutes, seconds=seconds)
        if duration.total_seconds() <= 0:
            raise ScheduleError("Interval must be positive.")
        return duration

    cursor = 0
    total = timedelta()
    while cursor < len(text):
        match = _INTERVAL_RE.match(text, cursor)
        if not match:
            raise ScheduleError(f"Unsupported interval fragment near: {text[cursor:]}")
        value_part = float(match.group("value"))
        unit = match.group("unit")
        if unit == "s":
            total += timedelta(seconds=value_part)
        elif unit == "m":
            total += timedelta(minutes=value_part)
        elif unit == "h":
            total += timedelta(hours=value_part)
        elif unit == "d":
            total += timedelta(days=value_part)
        else:  # pragma: no cover - defensive guard
            raise ScheduleError(f"Unsupported interval unit: {unit}")
        cursor = match.end()

    if total.total_seconds() <= 0:
        raise ScheduleError("Interval must be positive.")
    return total


def _parse_time(text: str) -> time:
    parts = text.split(":")
    if not 2 <= len(parts) <= 3:
        raise ScheduleError("Time must be HH:MM or HH:MM:SS")
    hour = int(parts[0])
    minute = int(parts[1])
    second = int(parts[2]) if len(parts) == 3 else 0
    return time(hour=hour, minute=minute, second=second)


def _build_timezone(name: str | None) -> timezone | ZoneInfo:
    if not name:
        return timezone.utc
    if ZoneInfo is None:
        raise ScheduleError("Time zone support requires Python 3.9+ with zoneinfo")
    try:
        return ZoneInfo(name)
    except Exception as exc:  # pragma: no cover - defensive
        raise ScheduleError(f"Unknown time zone: {name}") from exc


@dataclass(frozen=True)
class ScheduleWindow:
    """Restricts schedule execution to a daily window."""

    start: time
    end: time
    timezone: timezone | ZoneInfo = field(default_factory=lambda: timezone.utc)

    def __post_init__(self) -> None:
        if not isinstance(self.start, time) or not isinstance(self.end, time):
            raise ScheduleError("Window bounds must be datetime.time instances")

    @classmethod
    def from_dict(cls, payload: Mapping[str, object]) -> "ScheduleWindow":
        try:
            start_text = str(payload["start"])  # type: ignore[index]
            end_text = str(payload["end"])  # type: ignore[index]
        except KeyError as exc:
            raise ScheduleError("Window requires start and end fields") from exc
        tz = _build_timezone(str(payload.get("timezone", "UTC")))
        return cls(start=_parse_time(start_text), end=_parse_time(end_text), timezone=tz)

    def contains(self, moment: datetime) -> bool:
        aware = moment.astimezone(self.timezone)
        current = aware.timetz().replace(tzinfo=None)
        start = self.start
        end = self.end
        if start <= end:
            return start <= current <= end
        # Overnight window (e.g., 22:00 - 02:00)
        return current >= start or current <= end

    def align(self, candidate: datetime) -> datetime:
        aware = candidate.astimezone(self.timezone)
        base = aware.replace(microsecond=0)
        start_time = self.start
        end_time = self.end
        current = base.timetz().replace(tzinfo=None)
        if start_time <= end_time:
            if current < start_time:
                base = base.replace(
                    hour=start_time.hour,
                    minute=start_time.minute,
                    second=start_time.second,
                )
            elif current > end_time:
                base = (base + timedelta(days=1)).replace(
                    hour=start_time.hour,
                    minute=start_time.minute,
                    second=start_time.second,
                )
        else:  # overnight window
            if current > end_time and current < start_time:
                base = base.replace(
                    hour=start_time.hour,
                    minute=start_time.minute,
                    second=start_time.second,
                )
        return base.astimezone(timezone.utc)


@dataclass(frozen=True)
class ScheduleSpec:
    """Schedule configuration for a profile run."""

    name: str
    profile: str
    interval: timedelta
    start_at: datetime | None = None
    window: ScheduleWindow | None = None
    tags: tuple[str, ...] = ()
    metadata: Mapping[str, object] = field(default_factory=dict)
    loader: ProfileLoader | None = field(default=None, repr=False, compare=False)

    def __post_init__(self) -> None:
        if self.interval.total_seconds() <= 0:
            raise ScheduleError("Interval must be positive.")
        if not self.name.strip():
            raise ScheduleError("Schedule name must not be empty.")
        if not self.profile.strip():
            raise ScheduleError("Profile reference must not be empty.")

    @classmethod
    def from_dict(
        cls,
        payload: Mapping[str, object],
        *,
        profile_loader: ProfileLoader | None = None,
    ) -> "ScheduleSpec":
        try:
            name = str(payload["name"])
            profile = str(payload["profile"])
            every = payload["every"]
        except KeyError as exc:
            raise ScheduleError("Schedule entries require name, profile, and every") from exc
        start_at_raw = payload.get("start_at")
        start_at = None
        if start_at_raw:
            start_at = _ensure_aware(datetime.fromisoformat(str(start_at_raw)))
        window_payload = payload.get("window")
        window = None
        if isinstance(window_payload, Mapping):
            window = ScheduleWindow.from_dict(window_payload)
        tags_raw = payload.get("tags", ())
        tags: tuple[str, ...]
        if isinstance(tags_raw, Sequence) and not isinstance(tags_raw, (str, bytes)):
            tags = tuple(sorted(str(tag).strip() for tag in tags_raw if str(tag).strip()))
        else:
            tags = () if not tags_raw else (str(tags_raw).strip(),)
        metadata_raw = payload.get("metadata", {})
        if isinstance(metadata_raw, Mapping):
            metadata = MappingProxyType(dict(metadata_raw))
        else:
            raise ScheduleError("Metadata must be a mapping when provided.")
        interval = parse_interval(every)  # type: ignore[arg-type]
        return cls(
            name=name,
            profile=profile,
            interval=interval,
            start_at=start_at,
            window=window,
            tags=tags,
            metadata=metadata,
            loader=profile_loader,
        )

    def align_to(self, reference: datetime) -> datetime:
        candidate = _ensure_aware(reference)
        if self.window:
            candidate = self.window.align(candidate)
        return candidate

    def initial_run(self, reference: datetime | None = None) -> datetime:
        base = self.start_at or reference or datetime.now(timezone.utc)
        return self.align_to(base)

    def next_after(self, moment: datetime) -> datetime:
        candidate = _ensure_aware(moment) + self.interval
        return self.align_to(candidate)

    def load_profile(self) -> RunProfile:
        if not self.loader:
            raise ScheduleError("Profile loader not configured for this schedule")
        return self.loader(self.profile)


@dataclass(frozen=True)
class ScheduledRun:
    name: str
    profile: str
    scheduled_for: datetime
    tags: tuple[str, ...]
    metadata: Mapping[str, object]

    def load_profile(self, loader: ProfileLoader | None = None) -> RunProfile:
        if loader is None:
            raise ScheduleError("A profile loader is required to hydrate the run.")
        return loader(self.profile)


@dataclass
class _ScheduleState:
    next_run: datetime
    pending: datetime | None = None


class ProfileScheduler:
    """Lightweight in-process scheduler for run profiles."""

    def __init__(self, specs: Iterable[ScheduleSpec] | None = None) -> None:
        self._specs: dict[str, ScheduleSpec] = {}
        self._state: MutableMapping[str, _ScheduleState] = {}
        if specs:
            for spec in specs:
                self.register(spec)

    @staticmethod
    def _now() -> datetime:
        return datetime.now(timezone.utc)

    def register(self, spec: ScheduleSpec) -> None:
        if spec.name in self._specs:
            raise ScheduleError(f"Schedule already registered: {spec.name}")
        start = spec.initial_run(self._now())
        self._specs[spec.name] = spec
        self._state[spec.name] = _ScheduleState(next_run=start)

    def schedules(self) -> tuple[ScheduleSpec, ...]:
        return tuple(self._specs[name] for name in sorted(self._specs))

    def due(self, reference: datetime | None = None) -> list[ScheduledRun]:
        now = _ensure_aware(reference or self._now())
        runs: list[ScheduledRun] = []
        for name, spec in self._specs.items():
            state = self._state[name]
            if state.pending is not None:
                if state.pending <= now:
                    runs.append(
                        ScheduledRun(
                            name=name,
                            profile=spec.profile,
                            scheduled_for=state.pending,
                            tags=spec.tags,
                            metadata=spec.metadata,
                        )
                    )
                continue
            if state.next_run <= now:
                state.pending = state.next_run
                runs.append(
                    ScheduledRun(
                        name=name,
                        profile=spec.profile,
                        scheduled_for=state.pending,
                        tags=spec.tags,
                        metadata=spec.metadata,
                    )
                )
        runs.sort(key=lambda item: item.scheduled_for)
        return runs

    def peek(self, name: str) -> datetime:
        if name not in self._state:
            raise ScheduleError(f"Unknown schedule: {name}")
        state = self._state[name]
        return state.pending or state.next_run

    def mark_complete(self, name: str, completed_at: datetime | None = None) -> None:
        if name not in self._state:
            raise ScheduleError(f"Unknown schedule: {name}")
        state = self._state[name]
        spec = self._specs[name]
        if state.pending is None:
            raise ScheduleError(f"Schedule {name} is not pending.")
        completed = _ensure_aware(completed_at or state.pending)
        state.pending = None
        state.next_run = spec.next_after(completed)

    def skip_until(self, name: str, resume_at: datetime) -> None:
        if name not in self._state:
            raise ScheduleError(f"Unknown schedule: {name}")
        state = self._state[name]
        spec = self._specs[name]
        state.pending = None
        state.next_run = spec.align_to(_ensure_aware(resume_at))

    def cancel(self, name: str) -> None:
        if name not in self._specs:
            raise ScheduleError(f"Unknown schedule: {name}")
        del self._specs[name]
        del self._state[name]

    def snapshot_state(self) -> Mapping[str, Mapping[str, str | None]]:
        snapshot: dict[str, dict[str, str | None]] = {}
        for name, state in self._state.items():
            entry: dict[str, str | None] = {
                "next_run": state.next_run.isoformat(),
            }
            entry["pending"] = state.pending.isoformat() if state.pending else None
            snapshot[name] = entry
        return snapshot

    def apply_state(self, state: Mapping[str, Mapping[str, object]]) -> None:
        for name, payload in state.items():
            if name not in self._state:
                continue
            entry = self._state[name]
            next_run_raw = payload.get("next_run")
            if next_run_raw:
                entry.next_run = _parse_timestamp(next_run_raw)
            pending_raw = payload.get("pending")
            if pending_raw:
                entry.pending = _parse_timestamp(pending_raw)
            else:
                entry.pending = None


__all__ = [
    "ProfileScheduler",
    "ScheduleError",
    "ScheduleSpec",
    "ScheduleWindow",
    "ScheduledRun",
    "parse_interval",
]
