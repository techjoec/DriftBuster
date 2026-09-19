using DriftBuster.Backend.Models;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>Field parsing, definition validation and the scheduler's pending/complete/skip cycle.</summary>
public sealed class SchedulerTests
{
    private static readonly DateTimeOffset March1 = new(2025, 3, 1, 8, 0, 0, TimeSpan.Zero);

    internal static ScheduleDefinition Definition(string name = "backup", string every = "12h", string? startAt = "2025-03-01T08:00:00Z", ScheduleWindowDefinition? window = null)
        => new() { Name = name, Profile = "nightly", Every = every, StartAt = startAt, Window = window };

    [Theory]
    [InlineData("15m", 900)]
    [InlineData("1h30m", 5400)]
    [InlineData("1.5d", 129600)]
    [InlineData(" 2H ", 7200)]
    [InlineData("PT45M", 2700)]
    [InlineData("pt1h30m", 5400)]
    [InlineData("P1D", 86400)]
    public void Intervals_accept_compact_tokens_and_iso_durations(string text, double seconds)
        => ScheduleParsing.ParseInterval(text).TotalSeconds.Should().Be(seconds);

    [Theory]
    [InlineData("0m")]
    [InlineData("90")]
    [InlineData("15 minutes")]
    [InlineData("PT")]
    [InlineData("")]
    public void Other_interval_text_is_refused(string text)
        => FluentActions.Invoking(() => ScheduleParsing.ParseInterval(text)).Should().Throw<ScheduleException>();

    [Fact]
    public void Timestamps_without_an_offset_are_utc()
    {
        ScheduleParsing.ParseTimestamp("2025-03-01T08:00:00").Should().Be(March1);
        ScheduleParsing.ParseTimestamp("2025-03-01T02:00:00-06:00").Should().Be(March1);
        ScheduleParsing.ParseTimestamp("2025-03-01 08:00Z").Should().Be(March1);
        ScheduleParsing.ParseTimestamp("2025-03-01").Should().Be(March1.AddHours(-8));
        FluentActions.Invoking(() => ScheduleParsing.ParseTimestamp("soon")).Should().Throw<ScheduleException>().WithMessage("Invalid ISO 8601 timestamp: 'soon'.");
        FluentActions.Invoking(() => ScheduleParsing.ParseTimestamp("08:00")).Should().Throw<ScheduleException>();
    }

    [Theory]
    [InlineData(" ", "12h", null, "name: required.")]
    [InlineData("n", "often", null, "every: Unsupported interval: 'often'.")]
    [InlineData("n", "12h", "later", "start_at: Invalid ISO 8601 timestamp: 'later'.")]
    public void Validation_names_the_field(string name, string every, string? startAt, string message)
        => FluentActions.Invoking(() => ScheduleSpec.From(Definition(name, every, startAt))).Should().Throw<ScheduleException>().WithMessage(message);

    [Fact]
    public void Window_errors_name_the_window()
    {
        FluentActions.Invoking(() => ScheduleSpec.From(Definition(window: new() { Start = "25:00", End = "06:00" })))
            .Should().Throw<ScheduleException>().WithMessage("window: Time must be HH:MM or HH:MM:SS, not '25:00'.");
        FluentActions.Invoking(() => ScheduleSpec.From(Definition(window: new() { Start = "01:00", End = "06:00", Timezone = "Mars/Olympus" })))
            .Should().Throw<ScheduleException>().WithMessage("window: Unknown time zone: 'Mars/Olympus'.");
    }

    [Fact]
    public void Tags_are_trimmed_distinct_and_ordered()
        => ScheduleSpec.From(Definition() with { Tags = [" ops", "b", "ops", " "] }).Tags.Should().Equal("b", "ops");

    [Fact]
    public void An_overnight_window_moves_a_daytime_start_to_the_evening()
    {
        var spec = ScheduleSpec.From(Definition(every: "24h", startAt: "2025-01-01T12:00:00Z", window: new() { Start = "22:00", End = "02:00" }));

        spec.InitialRun(March1).Should().Be(new DateTimeOffset(2025, 1, 1, 22, 0, 0, TimeSpan.Zero));
        spec.NextAfter(new DateTimeOffset(2025, 1, 1, 22, 0, 0, TimeSpan.Zero)).Should().Be(new DateTimeOffset(2025, 1, 2, 22, 0, 0, TimeSpan.Zero));
        spec.Window!.Contains(new DateTimeOffset(2025, 1, 2, 1, 30, 0, TimeSpan.Zero)).Should().BeTrue();
        spec.Window.Contains(new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }

    [Fact]
    public void A_due_run_stays_pending_until_completed()
    {
        var scheduler = new ProfileScheduler([ScheduleSpec.From(Definition())], new FixedTimeProvider(March1));

        var run = scheduler.Due(March1).Should().ContainSingle().Subject;
        run.ScheduledFor.Should().Be(March1);
        scheduler.Due(March1.AddHours(1)).Should().ContainSingle().Which.ScheduledFor.Should().Be(March1);

        scheduler.MarkComplete("backup");
        scheduler.StateOf("backup").Should().Be(new ScheduleStateEntry(March1.AddHours(12), null));
        scheduler.Due(March1.AddHours(1)).Should().BeEmpty();
        FluentActions.Invoking(() => scheduler.MarkComplete("backup")).Should().Throw<ScheduleException>().WithMessage("Schedule 'backup' is not pending.");
    }

    [Fact]
    public void Runs_due_together_keep_registration_order()
    {
        var scheduler = new ProfileScheduler([ScheduleSpec.From(Definition("b")), ScheduleSpec.From(Definition("a"))], new FixedTimeProvider(March1));

        scheduler.Due(March1).Select(run => run.Name).Should().Equal("b", "a");
        scheduler.Schedules().Select(spec => spec.Name).Should().Equal("a", "b");
    }

    [Fact]
    public void Skip_until_restarts_the_schedule_and_saved_state_restores()
    {
        var scheduler = new ProfileScheduler([ScheduleSpec.From(Definition())], new FixedTimeProvider(March1));
        _ = scheduler.Due(March1);
        scheduler.SkipUntil("backup", March1.AddDays(2));
        scheduler.StateOf("backup").Should().Be(new ScheduleStateEntry(March1.AddDays(2), null));

        var restored = new ProfileScheduler([ScheduleSpec.From(Definition())], new FixedTimeProvider(March1));
        restored.ApplyState(scheduler.SnapshotState());
        restored.StateOf("backup").Should().Be(scheduler.StateOf("backup"));
        FluentActions.Invoking(() => restored.SkipUntil("gone", March1)).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: 'gone'.");
        FluentActions.Invoking(() => restored.Register(ScheduleSpec.From(Definition()))).Should().Throw<ScheduleException>();
    }

    [Fact]
    public void A_schedule_without_a_start_begins_now()
    {
        var scheduler = new ProfileScheduler([ScheduleSpec.From(Definition(startAt: null))], new FixedTimeProvider(March1));
        scheduler.StateOf("backup").NextRun.Should().Be(March1);
    }
}
