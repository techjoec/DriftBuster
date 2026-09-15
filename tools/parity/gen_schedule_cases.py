"""Generate the CPython oracle data the C# scheduling tests compare against.

Temporary; deleted together with the Python package. Re-run from the repository root after editing a case table:
    python tools/parity/gen_schedule_cases.py

Writes gui/DriftBuster.Backend.Tests/Scheduling/Data/schedule_cases.json with ``ensure_ascii=True`` (unpaired surrogates survive as
``\\uXXXX`` escapes; the C# tests read the file with ``PythonJson``). Every result is what CPython 3.13 returns on this host's
tzdata: ``datetime.fromisoformat``, ``scheduler.parse_interval``, ``scheduler._parse_time``, ``scheduler._build_timezone``,
``ZoneInfo.fromutc`` / ``utcoffset`` around every offset change of the zones below, ``ScheduleWindow.align`` / ``contains``,
``ScheduleSpec.from_dict`` and ``initial_run`` / ``next_after`` chains, and ``ProfileScheduler`` due / complete / skip sequences.
Zone data before 1970 is left out: local mean time offsets carry seconds that ``TimeZoneInfo`` rounds to minutes.
"""

from __future__ import annotations

import json
import random
from datetime import UTC, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

from driftbuster import scheduler

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[1] / "gui" / "DriftBuster.Backend.Tests" / "Scheduling" / "Data" / "schedule_cases.json"

ZONES = (
    "America/Chicago",
    "Europe/London",
    "Australia/Lord_Howe",
    "Asia/Kolkata",
    "UTC",
    "Europe/Dublin",
    "America/St_Johns",
    "Asia/Kathmandu",
    "Pacific/Apia",
    "America/Sao_Paulo",
    "Antarctica/Troll",
)

YEARS = (1970, 1971, 2011, 2012, 2024, 2025, 2026, 2040)

# Zones probed only in the years that exercise them: POSIX footer rules whose transition hour lies outside 0..24 (after the TZif's
# explicit transitions, which end in 2037), and offsets with seconds (local mean time and standard mean times).
EXTRA_ZONE_YEARS = {
    "Asia/Jerusalem": (2038, 2039, 2059),
    "America/Santiago": (1918, 1927, 2038, 2050),
    "Africa/Cairo": (2038, 2040),
    "America/Nuuk": (2038, 2045),
    "Africa/Monrovia": (1970, 1971, 1972),
    "Europe/Paris": (1911,),
    "Eire": (1916,),
}

WINDOWS = (
    ("00:00", "23:59:59"),
    ("01:00", "01:59"),
    ("01:30", "03:00"),
    ("01:45", "02:15"),
    ("02:00", "02:30"),
    ("02:30", "04:00"),
    ("22:00", "02:30"),
    ("23:00", "01:00"),
    ("09:00", "17:00"),
    ("03:00", "02:00"),
    ("12:00", "12:00"),
)


def _error(exc: BaseException) -> dict[str, str]:
    return {"type": type(exc).__name__, "message": str(exc)}


def _dt(value: datetime) -> list[object]:
    offset = value.utcoffset()
    offset_us = None if offset is None else (offset.days * 86400 + offset.seconds) * 1_000_000 + offset.microseconds
    return [
        value.year,
        value.month,
        value.day,
        value.hour,
        value.minute,
        value.second,
        value.microsecond,
        value.fold,
        offset_us,
        value.isoformat(),
    ]


def _delta(value: timedelta) -> list[object]:
    return [value.days, value.seconds, value.microseconds, value.total_seconds(), repr(value)]


def _capture(func, *args):
    try:
        return {"result": func(*args)}
    except Exception as exc:  # noqa: BLE001 - every raise is oracle data
        return {"error": _error(exc)}


def isoformat_cases() -> list[dict[str, object]]:
    texts = [
        "2025-01-01T00:00:00Z",
        "2025-01-01",
        "20250101",
        "20250101T01",
        "2025-01-01T01:02:03.1234567",
        "2025-01-01T01:02:03,5",
        "2025-01-01T01:02:03.",
        "2025-01-01T01:02:03.+05:00",
        "2025-01-01T01:02:03.5+05:00",
        "2025-W01-1",
        "2025W011",
        "2025W01",
        "2025-W01",
        "2025-W53-1",
        "2026-W53-1",
        "2020-W53-7",
        "2025-W00-1",
        "2025-W01-8",
        "2020-W01-0000",
        "2025-W01-",
        "2025-01-01 24:00",
        "2025-01-01T24:00:00",
        "2025-01-01T01:02:03+0530",
        "2025-01-01T01:02:03+05:30:15.5",
        "2025-01-01T01:02:03-00:00",
        "2025-01-01T01:02:03-00:00:00.000001",
        "2025-01-01T01:02:03+00:00:00.000001",
        "2025-01-01T01:02:03+23:59:59.999999",
        "2025-01-01T01:02:03-23:59:59.999999",
        "2025-01-01T01:02:03+24:00",
        "2025-01-01T01:02:03-24:00",
        "2025-01-01T01:02:03+23:99",
        "2025-01-01X01",
        "2025-01-01T1",
        "2025-01-01T01Z",
        "2025-01-01T01Zx",
        "2025-01-01T0102",
        "2025-01-01T010203",
        "2025-01-01T01:0203",
        "2025-01-01T0102:03",
        "2025-01-01T01:02:03:04",
        "٢٠٢٥-01-01",
        "2025-01-01\ud80001:00",
        "2025-01-01é01:00",
        "2025-01-01€01:00",
        "2025-01-01\U0001f60001:00",
        "2025-01-01é",
        "2025-01-01T01\x00",
        "2025-01-01T01:00\x00+01:00",
        "0000-01-01",
        "0001-01-01T00:00:00",
        "9999-12-31T23:59:59.999999",
        "2025-13-01",
        "2025-02-29",
        "2024-02-29T12:00",
        "2025-00-10",
        "2025-01-32",
        "2025-01-01T25:00",
        "2025-01-01T23:60",
        "2025-01-01T23:59:60",
        "2025-13-01T00:00+24:00",
        "2025-01",
        "202501",
        "2025-1-01",
        "short",
        "",
        "2025-01-01T",
        "2025-01-01T01:02:03.123456789012",
        "2025-01-01T01:02:03.12a",
        "2025-01-01T01:02+01:02:03,5",
        "2025-01-01T01:02+01:02:03.",
        "2025-01-01T01:02+0102",
        "2025-01-01T01:02+01:0",
        "2025-01-01T01:02+1",
        "2025-01-01T01:02+",
        "2025-01-01T01:02Z+01",
        "2025-01-01T01:02-",
        "2025-01-01T12:34:56.789-05:00",
        "2025-01-01T12:34:56.000000Z",
        "20250101T123456Z",
        "2025-01-01T12:34:56 ",
        " 2025-01-01T12:34:56",
        "2025-01-01t12:34",
        "2025-01-0112:34",
        "2025-01-01112:34",
        "2025W0112:34",
        "2025W01112:34",
        "2025W011123456",
        "2025W01123456",
        "2025-W01112:34",
        "2025-W01-112:34",
        "2025-W01T12:34",
        "2025-W01-1T12",
    ]
    for year in ("0000", "0001", "0004", "0100", "0400", "1900", "2000", "2020", "2024", "2025", "2026", "9998", "9999"):
        for week in ("00", "01", "09", "22", "52", "53"):
            for day in "01467":
                texts.append(f"{year}-W{week}-{day}")
                texts.append(f"{year}W{week}{day}T08")
    texts.extend(["2025-W01+1", "2025W01-1", "2025-01-01T01:00\ud800", "\ud8002025-01-01", "2025-01\ud800-01", "20250101\udc00T01"])
    rng = random.Random(20250914)
    alphabet = "0123456789-:T.,+Z W"
    for _ in range(400):
        length = rng.randrange(6, 30)
        texts.append("".join(rng.choice(alphabet) for _ in range(length)))
    for _ in range(300):
        base = "2025-03-09T02:30:00.123456+05:30"
        chars = list(base)
        for _ in range(rng.randrange(1, 4)):
            position = rng.randrange(len(chars))
            chars[position] = rng.choice(alphabet)
        texts.append("".join(chars)[: rng.randrange(7, len(chars) + 1)])
    cases = []
    for text in dict.fromkeys(texts):
        cases.append({"input": text, **_capture(lambda value: _dt(datetime.fromisoformat(value)), text)})
    return cases


def interval_cases() -> list[dict[str, object]]:
    values: list[object] = [
        90, 0, -1, 1.5, 0.0000001, 0.0000005, 0.0000015, 0.0000025, 1e-6, 2.5e-6, 3.5e-6, 86399.9999995, 1e20, 1e14,
        86400 * 999999999, 86400 * 1000000000, float("inf"), float("-inf"), float("nan"), True, False, None, [1], {"a": 1}, 10**400,
        "15m", "1h30m", "PT45M", "0m", "1e3s", "99999999999999999999d", "999999999d", "1000000000d", "0.0000001s",
        "0.0000005s", "0.0000015s", "0.0000025s", "1.5h", "PT1.5H", "PT", "pt0h", "PT1H2M3.5S", " 15M ", "١٥m",
        "15m 3s", "", "   ", "abc", "1x", "1", "1.s", ".5s", "1.5.5s", "PT1M1H", "PT1S1M", "PT-1H", "P1D", "pt1h ",
        "1d1d1d", "999999999d1d", "999999999d86399s", "999999999d86400s", "0.5d0.5d", "1h-1m", " 15m ",
        "İh", "1H", "1D", "1S", "1M", "PT0.0000005S", "PT0.0000015S", "PT99999999999999999999H", "9" * 400 + "s",
        "PT" + "9" * 400 + "S", "1_0m", "+1m", "1m\x00", "１m",
        "0.0000004s0.0000004s",
    ]
    rng = random.Random(7)
    for _ in range(200):
        tokens = []
        for _ in range(rng.randrange(1, 4)):
            number = str(rng.randrange(0, 100000))
            if rng.random() < 0.5:
                number += "." + "".join(rng.choice("0123456789") for _ in range(rng.randrange(1, 12)))
            tokens.append(number + rng.choice("smhd"))
        text = "".join(tokens)
        if rng.random() < 0.3:
            text = "PT" + text.upper().replace("D", "H")
        values.append(text)
    cases = []
    for value in values:
        cases.append({"input": value, **_capture(lambda item: _delta(scheduler.parse_interval(item)), value)})
    return cases


def time_cases() -> list[dict[str, object]]:
    texts = [
        "22:00", "02:00:30", "2", "1:2:3:4", "24:00", "23:60", "23:59:60", "-1:00", "+1:00", " 1 : 2 ", "1_0:00",
        "١:٢", "99999999999:00", "5:99999999999", "25:99999999999", "5:5:99999999999", "a:00", "00:b",
        "00:00:c", "", ":", "::", "1::", "None", "08:00", "17:00", "23:59:59", "00:00:00", "-0:-0", "2147483647:0",
        "2147483648:0", "-2147483648:0", "-2147483649:0", "9223372036854775807:0", "9223372036854775808:0", "0:-9223372036854775809",
    ]
    return [
        {"input": text, **_capture(lambda value: scheduler._parse_time(value).isoformat(), text)}  # noqa: SLF001
        for text in texts
    ]


def timezone_cases() -> list[dict[str, object]]:
    names = [
        "", "UTC", "utc", "Etc/UTC", "America/Chicago", "America/Chicago/", "./UTC", "../UTC", "America//Chicago",
        "zone.tab", "Central Standard Time", "a\x00b", "/etc/localtime", "US/Central", "Factory", "posixrules", "None",
        "Europe/London", "Australia/Lord_Howe", "Asia/Kolkata", "Asia/Calcutta", "EST5EDT", "GMT+0", "Etc/GMT-14",
        "America", "Nowhere/Special", "America/Chicago ", " UTC",
    ]
    cases = []
    for name in names:
        outcome = _capture(scheduler._build_timezone, name)  # noqa: SLF001
        if "result" in outcome:
            tz = outcome["result"]
            outcome = {"result": getattr(tz, "key", None) or str(tz)}
        cases.append({"input": name, **outcome})
    return cases


def _offset_seconds(moment: datetime) -> int:
    offset = moment.utcoffset()
    assert offset is not None
    return int(offset.total_seconds())


def _transitions(zone: ZoneInfo, year: int) -> list[tuple[int, int, int]]:
    """Every UTC second in the year at which the offset changes, with the offsets before and after."""
    found = []
    start = datetime(year, 1, 1, tzinfo=UTC)
    previous = _offset_seconds(start.astimezone(zone))
    for hour in range(1, 24 * 366 + 1):
        moment = start + timedelta(hours=hour)
        current = _offset_seconds(moment.astimezone(zone))
        if current != previous:
            low, high = moment - timedelta(hours=1), moment
            while (high - low) > timedelta(seconds=1):
                middle = low + (high - low) / 2
                middle = middle.replace(microsecond=0)
                if _offset_seconds(middle.astimezone(zone)) == previous:
                    low = middle
                else:
                    high = middle
            found.append((int(high.timestamp()), previous, current))
            previous = current
    return found


def zone_cases() -> list[dict[str, object]]:
    cases = []
    for key, years in [*((key, YEARS) for key in ZONES), *EXTRA_ZONE_YEARS.items()]:
        zone = ZoneInfo(key)
        transitions = []
        for year in years:
            transitions.extend(_transitions(zone, year))
        fromutc = []
        utcoffset = []
        anchors = [(int(datetime(year, 6, 15, 12, tzinfo=UTC).timestamp()), 0, 0) for year in (1975, 2025)]
        for when, before, after in transitions + anchors:
            shift = abs(before - after)
            for delta in sorted({-shift - 1, -3601, -3600, -1801, -1, 0, 1, 1799, 1800, shift - 1, shift, shift + 1, 3600, 7200}):
                moment = datetime.fromtimestamp(when + delta, UTC)
                fromutc.append([moment.isoformat(), _dt(moment.astimezone(zone))])
            for wall_offset in {max(before, after), min(before, after)}:
                wall = datetime.fromtimestamp(when + wall_offset, UTC).replace(tzinfo=None)
                for delta in sorted({-shift - 1, -3600, -1801, -1, 0, 1, 1800, shift - 1, shift, shift + 1, 3600}):
                    local = wall + timedelta(seconds=delta)
                    for fold in (0, 1):
                        aware = local.replace(tzinfo=zone, fold=fold)
                        utcoffset.append([local.isoformat(), fold, _offset_seconds(aware), aware.astimezone(UTC).isoformat()])
        cases.append({"zone": key, "transitions": transitions, "fromutc": fromutc, "utcoffset": utcoffset})
    return cases


def _candidates(zone: ZoneInfo) -> list[datetime]:
    moments = []
    for year in (2025, 2040):
        for when, _, _ in _transitions(zone, year):
            for step in range(-8, 9):
                moments.append(datetime.fromtimestamp(when + step * 900, UTC))
            moments.append(datetime.fromtimestamp(when, UTC) + timedelta(microseconds=500000))
    if not moments:
        base = datetime(2025, 3, 9, 0, tzinfo=UTC)
        moments = [base + timedelta(minutes=37 * step) for step in range(40)]
    return moments


def window_cases() -> list[dict[str, object]]:
    cases = []
    for key in ("America/Chicago", "Europe/London", "Australia/Lord_Howe", "Asia/Kolkata", "UTC", "Europe/Dublin", "Pacific/Apia"):
        zone = ZoneInfo(key)
        rows = []
        for start, end in WINDOWS:
            window = scheduler.ScheduleWindow.from_dict({"start": start, "end": end, "timezone": key})
            for candidate in _candidates(zone):
                rows.append([start, end, candidate.isoformat(), window.align(candidate).isoformat(), window.contains(candidate)])
        cases.append({"zone": key, "rows": rows})
    return cases


def chain_cases() -> list[dict[str, object]]:
    cases = []
    for key in ("America/Chicago", "Europe/London", "Australia/Lord_Howe", "Asia/Kolkata", "UTC"):
        zone = ZoneInfo(key)
        for when, _, _ in _transitions(zone, 2025):
            for every in ("15m", "1h", "90m", "1d", "23h", "PT25H"):
                for window in (None, ("01:30", "03:00"), ("22:00", "02:30"), ("02:00", "02:30")):
                    payload: dict[str, object] = {
                        "name": "chain",
                        "profile": "p",
                        "every": every,
                        "start_at": datetime.fromtimestamp(when - 3 * 3600 + 17, UTC).isoformat(),
                    }
                    if window:
                        payload["window"] = {"start": window[0], "end": window[1], "timezone": key}
                    spec = scheduler.ScheduleSpec.from_dict(payload)
                    moment = spec.initial_run()
                    runs = [moment.isoformat()]
                    for _ in range(12):
                        moment = spec.next_after(moment)
                        runs.append(moment.isoformat())
                    cases.append({"payload": payload, "runs": runs})
    return cases


def spec_cases() -> list[dict[str, object]]:
    payloads: list[object] = [
        {"name": "n", "profile": "p", "every": "1h"},
        {"profile": "p", "every": "1h"},
        {"name": "n", "every": "1h"},
        {"name": "n", "profile": "p"},
        {"name": " ", "profile": "p", "every": "1h"},
        {"name": "n", "profile": "\t", "every": "1h"},
        {"name": None, "profile": 5, "every": 60},
        {"name": "n", "profile": "p", "every": "0s"},
        {"name": "n", "profile": "p", "every": "bad"},
        {"name": "n", "profile": "p", "every": "1h", "start_at": "not a date"},
        {"name": "n", "profile": "p", "every": "1h", "start_at": ""},
        {"name": "n", "profile": "p", "every": "1h", "start_at": 0},
        {"name": "n", "profile": "p", "every": "1h", "start_at": 5},
        {"name": "n", "profile": "p", "every": "1h", "start_at": "2025-01-01T05:00:00+05:30"},
        {"name": "n", "profile": "p", "every": "1h", "start_at": "2025-01-01T05:00:00"},
        {"name": "n", "profile": "p", "every": "1h", "start_at": "0001-01-01T00:30:00+01:00"},
        {"name": "n", "profile": "p", "every": "bad", "start_at": "not a date"},
        {"name": "n", "profile": "p", "every": "1h", "window": {"start": "22:00"}},
        {"name": "n", "profile": "p", "every": "1h", "window": {"end": "22:00"}},
        {"name": "n", "profile": "p", "every": "1h", "window": {"start": "22:00", "end": "02:00"}},
        {"name": "n", "profile": "p", "every": "1h", "window": {"start": "22:00", "end": "02:00", "timezone": None}},
        {"name": "n", "profile": "p", "every": "1h", "window": {"start": "22:00", "end": "02:00", "timezone": ""}},
        {"name": "n", "profile": "p", "every": "1h", "window": {"start": "25:00", "end": "02:00", "timezone": "Nowhere"}},
        {"name": "n", "profile": "p", "every": "1h", "window": {"start": "25:00", "end": "xx", "timezone": "UTC"}},
        {"name": "n", "profile": "p", "every": "1h", "window": {"start": "22", "end": "02:00", "timezone": "UTC"}},
        {"name": "n", "profile": "p", "every": "1h", "window": "22:00-02:00"},
        {"name": "n", "profile": "p", "every": "1h", "window": None},
        {"name": "n", "profile": "p", "every": "1h", "tags": ["b", " a ", "", 3, None, "b"]},
        {"name": "n", "profile": "p", "every": "1h", "tags": "single "},
        {"name": "n", "profile": "p", "every": "1h", "tags": " "},
        {"name": "n", "profile": "p", "every": "1h", "tags": ""},
        {"name": "n", "profile": "p", "every": "1h", "tags": {"k": 1}},
        {"name": "n", "profile": "p", "every": "1h", "tags": {}},
        {"name": "n", "profile": "p", "every": "1h", "tags": 0},
        {"name": "n", "profile": "p", "every": "1h", "tags": 7},
        {"name": "n", "profile": "p", "every": "1h", "tags": None},
        {"name": "n", "profile": "p", "every": "1h", "tags": ["\U0001f600", "￿", "Z", "a"]},
        {"name": "n", "profile": "p", "every": "1h", "metadata": None},
        {"name": "n", "profile": "p", "every": "1h", "metadata": []},
        {"name": "n", "profile": "p", "every": "bad", "metadata": []},
        {"name": "n", "profile": "p", "every": "1h", "metadata": {"b": [1, 2.5], "a": None}},
        {"name": "n", "profile": "p", "every": True},
        {"name": "n", "profile": "p", "every": 1.5e-7},
        {"name": ["x"], "profile": {"y": 1}, "every": "1h"},
        {"name": "n", "profile": "p", "every": None},
    ]
    cases = []
    for payload in payloads:
        def build(item=payload):
            spec = scheduler.ScheduleSpec.from_dict(item)
            return {
                "name": spec.name,
                "profile": spec.profile,
                "interval": _delta(spec.interval),
                "start_at": spec.start_at.isoformat() if spec.start_at else None,
                "window": None
                if spec.window is None
                else [spec.window.start.isoformat(), spec.window.end.isoformat(), getattr(spec.window.timezone, "key", None) or str(spec.window.timezone)],
                "tags": list(spec.tags),
                "metadata": dict(spec.metadata),
            }

        cases.append({"payload": payload, **_capture(build)})
    return cases


def scheduler_cases() -> list[dict[str, object]]:
    scenarios = [
        {
            "name": "chicago-daily-window-across-spring-forward",
            "specs": [{"name": "nightly", "profile": "p", "every": "1d", "start_at": "2025-03-07T07:00:00Z",
                       "window": {"start": "02:00", "end": "03:00", "timezone": "America/Chicago"}}],
            "start": "2025-03-07T00:00:00+00:00", "hours": 96, "step_minutes": 30, "complete_after_minutes": 20,
        },
        {
            "name": "chicago-hourly-across-fall-back",
            "specs": [{"name": "hourly", "profile": "p", "every": "1h", "start_at": "2025-11-02T04:10:00Z",
                       "window": {"start": "00:30", "end": "02:30", "timezone": "America/Chicago"}}],
            "start": "2025-11-02T04:00:00+00:00", "hours": 30, "step_minutes": 20, "complete_after_minutes": 0,
        },
        {
            "name": "london-overnight-both-transitions",
            "specs": [
                {"name": "spring", "profile": "p", "every": "12h", "start_at": "2025-03-29T20:00:00Z",
                 "window": {"start": "23:30", "end": "01:30", "timezone": "Europe/London"}},
                {"name": "autumn", "profile": "q", "every": "90m", "start_at": "2025-10-25T22:00:00Z",
                 "window": {"start": "00:45", "end": "01:15", "timezone": "Europe/London"}, "tags": ["b", "a"]},
            ],
            "start": "2025-03-29T18:00:00+00:00", "hours": 60, "step_minutes": 45, "complete_after_minutes": 5,
        },
        {
            "name": "lord-howe-half-hour-dst",
            "specs": [
                {"name": "back", "profile": "p", "every": "30m", "start_at": "2025-04-05T13:00:00Z",
                 "window": {"start": "01:30", "end": "02:00", "timezone": "Australia/Lord_Howe"}},
                {"name": "forward", "profile": "p", "every": "1d", "start_at": "2025-10-03T14:00:00Z",
                 "window": {"start": "02:00", "end": "02:30", "timezone": "Australia/Lord_Howe"}},
            ],
            "start": "2025-04-05T12:00:00+00:00", "hours": 30, "step_minutes": 15, "complete_after_minutes": 1,
        },
        {
            "name": "kolkata-and-utc-no-dst",
            "specs": [
                {"name": "kolkata", "profile": "p", "every": "7h", "start_at": "2025-03-09T00:00:00Z",
                 "window": {"start": "22:00", "end": "05:30", "timezone": "Asia/Kolkata"}},
                {"name": "utc", "profile": "p", "every": "45m", "start_at": "2025-03-09T00:05:00Z",
                 "window": {"start": "09:00", "end": "17:00", "timezone": "UTC"}},
                {"name": "plain", "profile": "p", "every": "5h"},
            ],
            "start": "2025-03-09T00:00:00+00:00", "hours": 48, "step_minutes": 60, "complete_after_minutes": 30,
        },
    ]
    now = datetime(2025, 3, 8, 23, 59, 30, 250000, tzinfo=UTC)
    original_now = scheduler.ProfileScheduler._now
    scheduler.ProfileScheduler._now = staticmethod(lambda: now)  # type: ignore[method-assign]
    try:
        cases = []
        for scenario in scenarios:
            specs = [scheduler.ScheduleSpec.from_dict(item) for item in scenario["specs"]]
            instance = scheduler.ProfileScheduler(specs)
            steps = []
            reference = datetime.fromisoformat(scenario["start"])
            end = reference + timedelta(hours=scenario["hours"])
            while reference <= end:
                due = instance.due(reference=reference)
                step = {"at": reference.isoformat(), "due": [[run.name, run.scheduled_for.isoformat()] for run in due]}
                for run in due:
                    completed = run.scheduled_for + timedelta(minutes=scenario["complete_after_minutes"])
                    instance.mark_complete(run.name, completed_at=completed)
                step["state"] = instance.snapshot_state()
                steps.append(step)
                reference += timedelta(minutes=scenario["step_minutes"])
            for spec in specs:
                resume = datetime.fromisoformat(scenario["start"]) + timedelta(hours=7, minutes=13)
                instance.skip_until(spec.name, resume)
            steps.append({"skip": resume.isoformat(), "state": instance.snapshot_state()})
            cases.append({"name": scenario["name"], "scenario": scenario, "steps": steps})
        return cases
    finally:
        scheduler.ProfileScheduler._now = original_now  # type: ignore[method-assign]


def main() -> None:
    payload = {
        "fromisoformat": isoformat_cases(),
        "parse_interval": interval_cases(),
        "parse_time": time_cases(),
        "build_timezone": timezone_cases(),
        "zones": zone_cases(),
        "windows": window_cases(),
        "chains": chain_cases(),
        "specs": spec_cases(),
        "schedulers": scheduler_cases(),
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(payload, ensure_ascii=True, separators=(",", ":")) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
