using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>The profile scheduler.</summary>
public sealed class SchedulerTests
{
    internal static OrderedDictionary<string, object?> Payload(params (string Key, object? Value)[] items)
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            payload[key] = value;
        }

        return payload;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void ParseIntervalSupportsNumericIsoAndCompactTokens()
    {
        ScheduleParsing.ParseInterval(90).Should().Be(TimeSpan.FromSeconds(90));
        ScheduleParsing.ParseInterval("15m").Should().Be(TimeSpan.FromMinutes(15));
        ScheduleParsing.ParseInterval("1h30m").Should().Be(TimeSpan.FromMinutes(90));
        ScheduleParsing.ParseInterval("PT45M").Should().Be(TimeSpan.FromMinutes(45));
        FluentActions.Invoking(() => ScheduleParsing.ParseInterval("0m")).Should().Throw<ScheduleException>();
    }

    [Fact]
    public void ScheduleSpecAlignsToWindowAndRollsForward()
    {
        var window = ScheduleWindow.FromDict(Payload(("start", "22:00"), ("end", "02:00"), ("timezone", "UTC")));
        var startAt = Utc(2025, 1, 1, 21);
        var spec = new ScheduleSpec("overnight", "profiles/nightly.json", TimeSpan.FromDays(1), startAt: startAt, window: window);

        var initial = spec.InitialRun();
        IsoTimestamp.Format(initial).Should().Be(IsoTimestamp.Format(Utc(2025, 1, 1, 22)));
        var rolled = spec.NextAfter(initial);
        IsoTimestamp.Format(rolled).Should().Be(IsoTimestamp.Format(Utc(2025, 1, 2, 22)));
    }

    [Fact]
    public void ProfileSchedulerTracksPendingRunsUntilCompletion()
    {
        var spec = new ScheduleSpec("backup", "profiles/backup.json", TimeSpan.FromHours(12), startAt: Utc(2025, 3, 1, 8));
        var scheduler = new ProfileScheduler([spec]);
        var now = Utc(2025, 3, 1, 9);

        var due = scheduler.Due(reference: now);
        due.Should().HaveCount(1);
        var run = due[0];
        run.Name.Should().Be("backup");
        IsoTimestamp.Format(run.ScheduledFor).Should().Be(IsoTimestamp.Format(Utc(2025, 3, 1, 8)));

        // Subsequent polls keep surfacing the pending run until it is marked complete.
        var repeat = scheduler.Due(reference: now);
        IsoTimestamp.Format(repeat[0].ScheduledFor).Should().Be(IsoTimestamp.Format(run.ScheduledFor));

        scheduler.MarkComplete("backup", completedAt: run.ScheduledFor);
        var peeked = scheduler.Peek("backup");
        IsoTimestamp.Format(peeked).Should().Be(IsoTimestamp.Format(Utc(2025, 3, 1, 20)));

        var later = scheduler.Due(reference: Utc(2025, 3, 1, 21));
        IsoTimestamp.Format(later[0].ScheduledFor).Should().Be(IsoTimestamp.Format(Utc(2025, 3, 1, 20)));
    }

    [Fact]
    public void SkipUntilResetsScheduleAnchor()
    {
        var spec = new ScheduleSpec("cleanup", "profiles/cleanup.json", TimeSpan.FromDays(1), startAt: Utc(2025, 4, 1, 2));
        var scheduler = new ProfileScheduler([spec]);
        scheduler.SkipUntil("cleanup", Utc(2025, 4, 3, 6, 30));

        // Windowless schedules align directly to the supplied resume timestamp.
        IsoTimestamp.Format(scheduler.Peek("cleanup")).Should().Be(IsoTimestamp.Format(Utc(2025, 4, 3, 6, 30)));
    }

    [Fact]
    public void ScheduleWindowContainsHandlesOvernightBounds()
    {
        var window = ScheduleWindow.FromDict(Payload(("start", "21:30"), ("end", "01:30"), ("timezone", "UTC")));
        var inside = Utc(2025, 5, 1, 23);
        var afterMidnight = Utc(2025, 5, 2, 1);
        var outside = Utc(2025, 5, 1, 12);
        window.Contains(inside).Should().BeTrue();
        window.Contains(afterMidnight).Should().BeTrue();
        window.Contains(outside).Should().BeFalse();
    }

    [Fact]
    public void SnapshotAndRestoreStatePreservesPendingRuns()
    {
        var start = Utc(2025, 6, 1, 8);
        var spec = new ScheduleSpec("nightly", "profiles/nightly.json", TimeSpan.FromDays(1), startAt: start);
        var scheduler = new ProfileScheduler([spec]);

        // Trigger a pending run so the snapshot captures both fields.
        scheduler.Due(reference: start);
        var snapshot = scheduler.SnapshotState();
        ((OrderedDictionary<string, object?>)snapshot["nightly"]!)["pending"].Should().Be(IsoTimestamp.Format(start));

        var restored = new ProfileScheduler([spec]);
        restored.ApplyState(snapshot);
        var restoredState = restored.SnapshotState();
        ((OrderedDictionary<string, object?>)restoredState["nightly"]!)["pending"].Should().Be(IsoTimestamp.Format(start));
    }
}
