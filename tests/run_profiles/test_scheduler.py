from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from driftbuster.scheduler import (
    ProfileScheduler,
    ScheduleError,
    ScheduleSpec,
    ScheduleWindow,
    parse_interval,
)


def test_parse_interval_supports_numeric_iso_and_compact_tokens() -> None:
    assert parse_interval(90) == timedelta(seconds=90)
    assert parse_interval("15m") == timedelta(minutes=15)
    assert parse_interval("1h30m") == timedelta(hours=1, minutes=30)
    assert parse_interval("PT45M") == timedelta(minutes=45)
    with pytest.raises(ScheduleError):
        parse_interval("0m")


def test_schedule_spec_aligns_to_window_and_rolls_forward() -> None:
    window = ScheduleWindow.from_dict({
        "start": "22:00",
        "end": "02:00",
        "timezone": "UTC",
    })
    start_at = datetime(2025, 1, 1, 21, 0, tzinfo=timezone.utc)
    spec = ScheduleSpec(
        name="overnight",
        profile="profiles/nightly.json",
        interval=timedelta(days=1),
        start_at=start_at,
        window=window,
    )
    initial = spec.initial_run()
    assert initial == datetime(2025, 1, 1, 22, 0, tzinfo=timezone.utc)
    rolled = spec.next_after(initial)
    assert rolled == datetime(2025, 1, 2, 22, 0, tzinfo=timezone.utc)


def test_profile_scheduler_tracks_pending_runs_until_completion() -> None:
    spec = ScheduleSpec(
        name="backup",
        profile="profiles/backup.json",
        interval=timedelta(hours=12),
        start_at=datetime(2025, 3, 1, 8, 0, tzinfo=timezone.utc),
    )
    scheduler = ProfileScheduler([spec])
    now = datetime(2025, 3, 1, 9, 0, tzinfo=timezone.utc)

    due = scheduler.due(reference=now)
    assert len(due) == 1
    run = due[0]
    assert run.name == "backup"
    assert run.scheduled_for == datetime(2025, 3, 1, 8, 0, tzinfo=timezone.utc)

    # Subsequent polls keep surfacing the pending run until it is marked complete.
    repeat = scheduler.due(reference=now)
    assert repeat[0].scheduled_for == run.scheduled_for

    scheduler.mark_complete("backup", completed_at=run.scheduled_for)
    peeked = scheduler.peek("backup")
    assert peeked == datetime(2025, 3, 1, 20, 0, tzinfo=timezone.utc)

    later = scheduler.due(reference=datetime(2025, 3, 1, 21, 0, tzinfo=timezone.utc))
    assert later[0].scheduled_for == datetime(2025, 3, 1, 20, 0, tzinfo=timezone.utc)


def test_skip_until_resets_schedule_anchor() -> None:
    spec = ScheduleSpec(
        name="cleanup",
        profile="profiles/cleanup.json",
        interval=timedelta(days=1),
        start_at=datetime(2025, 4, 1, 2, 0, tzinfo=timezone.utc),
    )
    scheduler = ProfileScheduler([spec])
    scheduler.skip_until("cleanup", datetime(2025, 4, 3, 6, 30, tzinfo=timezone.utc))
    # Windowless schedules align directly to the supplied resume timestamp.
    assert scheduler.peek("cleanup") == datetime(2025, 4, 3, 6, 30, tzinfo=timezone.utc)


def test_schedule_window_contains_handles_overnight_bounds() -> None:
    window = ScheduleWindow.from_dict({
        "start": "21:30",
        "end": "01:30",
        "timezone": "UTC",
    })
    inside = datetime(2025, 5, 1, 23, 0, tzinfo=timezone.utc)
    after_midnight = datetime(2025, 5, 2, 1, 0, tzinfo=timezone.utc)
    outside = datetime(2025, 5, 1, 12, 0, tzinfo=timezone.utc)
    assert window.contains(inside)
    assert window.contains(after_midnight)
    assert not window.contains(outside)
